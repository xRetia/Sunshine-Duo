// Power action panel (one dialog for every power entry point). Three buttons
// in one horizontal row - sign out / shut down / restart - so a mis-tap can
// be corrected to another action before anything happens. (A former
// "disconnect" action was removed: Moonlight connects over IPv6 here and
// there is no user-mode API to force-drop an IPv6 TCB.)
//
// Input surface (all four device classes):
//   - Mouse / touch: the buttons are ordinary WPF buttons (touch promotes to
//     click); the circle is a comfortable finger target.
//   - Keyboard: Left/Right steps the focus, Enter presses the focused button,
//     Esc cancels.
//   - Gamepad: MainWindow's pad poll routes dpad/stick left-right to
//     SwitchFocus, A to PressFocused, B to Cancel.
//
// The focus indicator is a single ring (FocusRing) that GLIDES between the
// four circles, console-style, instead of blinking on the new button. The
// bottom-right cancel badge auto-detects the input device (Esc for
// keyboard/mouse, B for gamepad) the same way the main window's hint bar does.
//
// There is deliberately no Cancel button: the four actions fill the row and
// the Esc / B hint keeps cancellation discoverable.

using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace GameManager;

public partial class PowerDialog : Window
{
    /// <summary>The action chosen when the dialog closed, or null when cancelled.</summary>
    internal PowerAction? Result { get; private set; }

    // 96-wide button slots + 24px gaps; the ring centers on each circle
    private const double SlotWidth = 96 + 24;

    private Button[] Buttons => new[] { LogOffButton, ShutdownButton, RestartButton };
    private int _focusIndex;
    private bool _padMode;

    /// <param name="initial">The menu entry the user came from; its button gets
    /// the initial focus so the panel behaves as that action's confirmation
    /// while still allowing a change of mind.</param>
    /// <param name="padMode">Whether the last input before opening was a gamepad;
    /// picks the cancel badge shown (B instead of Esc).</param>
    internal PowerDialog(PowerAction initial, bool padMode)
    {
        InitializeComponent();
        Title = Strings.PowerTitle;
        _padMode = padMode;

        _focusIndex = initial switch
        {
            PowerAction.LogOff => 0,
            PowerAction.Shutdown => 1,
            PowerAction.Restart => 2,
            _ => 0
        };

        UpdateCancelBadge();

        // mouse / touch on the panel flips the badge back to Esc, the same way
        // the main window's hint bar detects the input device. Keyboard is
        // covered by OnKeyDown, gamepad by MainWindow's poll (real input only).
        PreviewMouseDown += (_, _) => MarkPadInput(pad: false);
        PreviewStylusDown += (_, _) => MarkPadInput(pad: false);

        Loaded += (_, _) =>
        {
            // park the ring on the initial button (no slide-in), then focus
            Buttons[_focusIndex].Focus();
            PositionRing(_focusIndex, animate: false);
        };
    }

    // ---------------- input-device detection for the cancel badge ----------------

    /// <summary>Shows the device-appropriate badges: keyboard/mouse -> Enter +
    /// Esc, gamepad -> A + B, mirroring the main window's input-hint auto
    /// detection.</summary>
    private void UpdateCancelBadge()
    {
        EscBadge.Visibility = _padMode ? Visibility.Collapsed : Visibility.Visible;
        PadBBadge.Visibility = _padMode ? Visibility.Visible : Visibility.Collapsed;
        EnterBadge.Visibility = _padMode ? Visibility.Collapsed : Visibility.Visible;
        PadABadge.Visibility = _padMode ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Called by MainWindow when pad input drives this dialog, so the
    /// badge flips to B while the pad is in use.</summary>
    internal void MarkPadInput(bool pad)
    {
        if (_padMode == pad) return;
        _padMode = pad;
        UpdateCancelBadge();
    }

    // ---------------- focus + sliding ring ----------------

    /// <summary>Gamepad dpad/stick and keyboard arrows: step the focus. The row
    /// wraps around at both ends; the ring glides to the new button.</summary>
    internal void SwitchFocus(int dx)
    {
        int n = Buttons.Length;
        _focusIndex = ((_focusIndex + dx) % n + n) % n;
        Buttons[_focusIndex].Focus();
        PositionRing(_focusIndex, animate: true);
    }

    /// <summary>Centers FocusRing on button <paramref name="index"/>; slides there
    /// with a short eased animation (or jumps when this is the initial placement).
    /// Position = Margin base (18 = first circle inset) + slot * 120.</summary>
    private void PositionRing(int index, bool animate)
    {
        double x = index * SlotWidth;

        if (!animate)
        {
            RingSlide.X = x;
            FocusRing.Opacity = 1;
            return;
        }

        if (Math.Abs(RingSlide.X - x) < 0.5) return;

        // glide: animate the translate transform, console-style
        var anim = new DoubleAnimation(RingSlide.X, x, TimeSpan.FromMilliseconds(150))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        RingSlide.BeginAnimation(TranslateTransform.XProperty, anim);
    }

    /// <summary>Gamepad A / keyboard Enter: activate the focused button.</summary>
    internal void PressFocused()
    {
        Buttons[_focusIndex].RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
    }

    /// <summary>Gamepad B / Esc: close without acting.</summary>
    internal void Cancel()
    {
        Result = null;
        Close();
    }

    // keeps the focus index and the ring in sync when focus moves by other
    // means (mouse click, Tab, touch)
    private void OnButtonFocusChanged(object sender, KeyboardFocusChangedEventArgs e)
    {
        int i = Array.IndexOf(Buttons, e.NewFocus as Button);
        if (i >= 0 && i != _focusIndex)
        {
            _focusIndex = i;
            PositionRing(i, animate: true);
        }
    }

    private void OnLogOff(object sender, RoutedEventArgs e) => CloseWith(PowerAction.LogOff);

    private void OnShutdown(object sender, RoutedEventArgs e) => CloseWith(PowerAction.Shutdown);

    private void OnRestart(object sender, RoutedEventArgs e) => CloseWith(PowerAction.Restart);

    /// <summary>Top-right X button: same as Esc / gamepad B.</summary>
    private void OnClose(object sender, RoutedEventArgs e) => Cancel();

    private void CloseWith(PowerAction action)
    {
        Result = action;
        // DialogResult drives ShowDialog()'s return value: without setting it
        // to true the caller reads a cancelled dialog and drops the action
        // (the "clicked shutdown and nothing happened" bug).
        DialogResult = true;
        Close();
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        // keyboard use flips the badge back to Esc
        MarkPadInput(pad: false);

        switch (e.Key)
        {
            case Key.Escape:
                Cancel();
                e.Handled = true;
                break;
            case Key.Enter:
                PressFocused();
                e.Handled = true;
                break;
            case Key.Left:
                SwitchFocus(-1);
                e.Handled = true;
                break;
            case Key.Right:
                SwitchFocus(1);
                e.Handled = true;
                break;
        }
    }
}
