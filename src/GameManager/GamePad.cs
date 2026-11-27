// Gamepad input via the classic WinMM joystick API (works for Xbox pads through DirectInput).
// Navigation: dpad (POV hat) and left stick. Confirm: A (button 1) or X (button 3).
// Back: B (button 2) - closes menus/dialogs, leaves the header button row.

using System;
using System.Runtime.InteropServices;

namespace GameManager;

internal static class GamePad
{
    [StructLayout(LayoutKind.Sequential)]
    private struct JOYINFOEX
    {
        public int dwSize;
        public int dwFlags;
        public int dwXpos;
        public int dwYpos;
        public int dwZpos;
        public int dwRpos;
        public int dwUpos;
        public int dwVpos;
        public int dwButtons;
        public int dwButtonNumber;
        public int dwPOV;
        public int dwReserved1;
        public int dwReserved2;
    }

    private const int JOY_RETURNX = 0x1;
    private const int JOY_RETURNY = 0x2;
    private const int JOY_RETURNZ = 0x4;
    private const int JOY_RETURNU = 0x10;
    private const int JOY_RETURNPOV = 0x40;
    private const int JOY_RETURNBUTTONS = 0x80;

    private const int MaxPads = 4;
    private const int AxisCenter = 32768;
    private const int AxisDeadzone = 20000;

    // Right stick (for analog scrolling): a lighter deadzone than the navigation
    // stick so gentle deflections still scroll, but with enough drift tolerance
    // that a worn controller cannot crawl the About window on its own.
    private const int ScrollDeadzone = 5000;

    /// <summary>Maps a raw axis (0..65535, center 32768) to -1..+1 with deadzone;
    /// the curve is linear past the zone. 0 when inside the zone.</summary>
    private static double NormalizeScrollAxis(int value)
    {
        int offset = value - AxisCenter;
        if (Math.Abs(offset) <= ScrollDeadzone) return 0;
        double span = 32768 - ScrollDeadzone;
        return Math.Clamp(offset / span, -1.0, 1.0);
    }

    // dwPOV reports JOY_POVCENTERED (0xffff = 65535) when the dpad is neutral;
    // 0..35999 are the only valid directional readings (hundredths of a degree).
    private const int PovMax = 35999;

    // Xbox controller button mapping as seen by DirectInput: 1 = A, 2 = B, 3 = X, 4 = Y.
    private const int ButtonA = 1 << 0;
    private const int ButtonB = 1 << 1;
    private const int ButtonX = 1 << 2;

    [DllImport("winmm.dll")]
    private static extern int joyGetPosEx(int uJoyID, ref JOYINFOEX pji);

    // ---------------- rumble (launch feedback) ----------------

    // WinMM has no force-feedback API; generic HID rumble reports are per-vendor.
    // XInput covers virtually every modern pad (Xbox layout + compatibles), so the
    // launch pulse goes through xinput1_4 (Win8+, present on 10/11); non-XInput
    // devices simply stay silent - the pulse is pure feedback, never critical.
    // Fires at all four user indexes: WinMM ids and XInput slots do not correlate,
    // and in a session usually only one pad is visible anyway.

    [StructLayout(LayoutKind.Sequential)]
    private struct XINPUT_VIBRATION
    {
        public ushort wLeftMotorSpeed;
        public ushort wRightMotorSpeed;
    }

    [DllImport("xinput1_4.dll", EntryPoint = "XInputSetState")]
    private static extern int XInputSetState14(int dwUserIndex, ref XINPUT_VIBRATION pVibration);

    [DllImport("xinput9_1_0.dll", EntryPoint = "XInputSetState")]
    private static extern int XInputSetState9(int dwUserIndex, ref XINPUT_VIBRATION pVibration);

    private static int _xinputDll; // 0 unknown, 1 -> xinput1_4, 2 -> xinput9_1_0, -1 none

    private static int SetRumble(int userIndex, ushort speed)
    {
        var vib = new XINPUT_VIBRATION { wLeftMotorSpeed = speed, wRightMotorSpeed = speed };
        if (_xinputDll == 1) return XInputSetState14(userIndex, ref vib);
        if (_xinputDll == 2) return XInputSetState9(userIndex, ref vib);
        return int.MinValue;
    }

    /// <summary>Short rumble pulse on every connected XInput pad. Fire-and-forget;
    /// runs on a thread-pool thread so the UI never waits on the motors.</summary>
    public static void Rumble(int milliseconds = 220)
    {
        System.Threading.Tasks.Task.Run(() =>
        {
            // pick an XInput dll once (both are safe to call when absent - DllNotFoundException)
            if (_xinputDll == 0)
            {
                try { XInputSetState14(0, ref _off); _xinputDll = 1; }
                catch { try { XInputSetState9(0, ref _off); _xinputDll = 2; }
                        catch { _xinputDll = -1; return; } }
            }
            if (_xinputDll < 0) return;

            for (int i = 0; i < MaxPads; i++) SetRumble(i, 0xC800); // ~50% duty: a soft "click", not a growl
            System.Threading.Thread.Sleep(milliseconds);
            for (int i = 0; i < MaxPads; i++) SetRumble(i, 0);
        });
    }

    private static XINPUT_VIBRATION _off; // both motors off

    /// <summary>
    /// Polls all connected pads. Any pad's dpad or left stick sets the direction;
    /// A or X on any pad confirms, B backs out. Returns true when at least one
    /// pad responded. <paramref name="scrollY"/> carries the strongest right-stick
    /// Y deflection (-1..+1) for analog scrolling; 0 when centered.
    /// </summary>
    public static bool Poll(out int dx, out int dy, out bool confirm, out bool back,
                            out double scrollY)
    {
        dx = 0;
        dy = 0;
        confirm = false;
        back = false;
        scrollY = 0;
        bool anyPad = false;

        for (int id = 0; id < MaxPads; id++)
        {
            var info = new JOYINFOEX
            {
                dwSize = Marshal.SizeOf<JOYINFOEX>(),
                dwFlags = JOY_RETURNX | JOY_RETURNY | JOY_RETURNZ | JOY_RETURNU |
                          JOY_RETURNPOV | JOY_RETURNBUTTONS
            };
            if (joyGetPosEx(id, ref info) != 0) continue;
            anyPad = true;

            // Dpad: POV in hundredths of a degree, valid range 0..35999 while pressed.
            // Neutral is JOY_POVCENTERED (0xffff), NOT -1. A naive ">= 0" check reads
            // the centered value as 655.35 deg > 315 deg, i.e. a constant phantom
            // "up" that drags the selection back to the first tile every repeat
            // cycle and also fights keyboard/touch input. Only accept 0..35999.
            // Boundaries are inclusive so the exact 45 deg diagonals reported by
            // discrete POV (JOY_RETURNPOV) register as up-right/down-right/... .
            int pov = info.dwPOV;
            if (pov >= 0 && pov <= PovMax)
            {
                double deg = pov / 100.0;
                if (deg <= 45 || deg >= 315) dy = -1;
                else if (deg >= 135 && deg <= 225) dy = 1;
                if (deg >= 45 && deg <= 135) dx = 1;
                else if (deg >= 225 && deg <= 315) dx = -1;
            }

            // Left stick axes (0..65535, centered at 32768).
            if (info.dwXpos > AxisCenter + AxisDeadzone) dx = 1;
            else if (info.dwXpos < AxisCenter - AxisDeadzone) dx = -1;
            if (info.dwYpos > AxisCenter + AxisDeadzone) dy = 1;
            else if (info.dwYpos < AxisCenter - AxisDeadzone) dy = -1;

            if ((info.dwButtons & (ButtonA | ButtonX)) != 0) confirm = true;
            if ((info.dwButtons & ButtonB) != 0) back = true;

            // Right stick Y (dwUpos on WinMM/Xbox): analog scroll input, strongest
            // deflection across pads wins so two connected pads do not fight.
            double ry = NormalizeScrollAxis(info.dwUpos);
            if (Math.Abs(ry) > Math.Abs(scrollY)) scrollY = ry;
        }

        return anyPad;
    }
}