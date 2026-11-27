# GameManager

A game launcher for multi-seat Windows setups. It lists every `.lnk` under
`C:\MultiSeatGames\Links` as a tile grid and launches games in the right user
session. The UI is brought up by opening `steam://open/bigpicture` from
anywhere - browser, Run dialog, whatever the seat has access to.

Single-file WPF app on .NET 9, around 210 KB, framework-dependent.

## How it works

The `steam://` protocol is registered in HKLM, so it works for every user.
The protocol handler passes the target session ID to GameManagerService (a
Windows service running as SYSTEM) over a named pipe. The service then takes
that user's elevated token (WTSQueryUserToken + TokenLinkedToken) and starts
the UI inside their session with CreateProcessAsUser. The UI never runs as
SYSTEM.

Each user gets one UI instance, guarded by a named mutex. If an instance is
already running, the service sends ACTIVATE over the pipe and the existing
window comes to the front instead of spawning a new one.

A process started by a service has no foreground rights, so on startup the
window re-asserts topmost and takes keyboard focus for about two seconds
(AttachThreadInput) or until the user interacts.

## Input

- Click a tile to highlight it, click again to launch. Double-click also
  launches.
- Arrow keys move, Enter launches.
- Gamepad: d-pad or left stick to move, A or X to confirm.
- Touch: flick scrolling with inertia.
- F5 reloads the shortcut folder, F11 toggles borderless fullscreen, Esc
  leaves fullscreen.

## Build

Needs the .NET 9 SDK.

    dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o publish

`GameManager.json` is written next to the exe on first run.

## Configuration

GameManager.json, next to the exe:

- `LinksFolder` - where .lnk files are read from. Default
  `C:\MultiSeatGames\Links`.
- `StartFullScreen` - open as borderless fullscreen. The `--fullscreen`
  command-line flag overrides it.

## Installation

The Advanced menu in the UI covers it, all elevated:

- Register / unregister the `steam://` protocol for all users
- Install / remove the service (auto start)

## Power panel

The power button in the header opens the power panel directly - no dropdown
step in between. One row with all actions, circular line icons in soft hues,
a focus ring that glides between the buttons, so a mis-tap can still be
corrected to another action:

- **Sign out** - logs the current user off.
- **Shut down / Restart** - forced (`shutdown /f /t 0`), each behind a
  second-stage confirmation dialog (dark card, focus starts on Cancel).

(A former "disconnect stream" action was removed: Moonlight connects to this
host over IPv6, and Windows' SetTcpEntry is IPv4-only - there is no user-mode
API to force-drop an IPv6 TCB, so the TCP-cut approach never reached the
stream. Exit the stream from the Moonlight client instead.)

Both dialogs work with mouse, touch, keyboard (Left/Right to move, Enter to
confirm, Esc to cancel) and gamepad (d-pad/stick, A, B). The key/button hint
badges auto-detect the live input device (Enter+Esc vs A+B); the power panel's
top-right X closes it.

## Remote-session GPU acceleration

RDP-type sessions (including multi-seat seats) enumerate the software
display adapter first by default, so this WPF UI silently renders on the
CPU - while native D3D11/12 games (Unity, Unreal) still get the physical
GPU. Two layers are needed to fix that:

**Layer 1 - application (enabled by default, nothing to do).** The
`Switch.System.Windows.Media.EnableHardwareAccelerationInRdp` runtime
switch (a .NET 8+ WPF opt-in, baked into the exe via
`RuntimeHostConfigurationOption` in the csproj). Without it WPF forces
software rendering whenever it detects a remote session, even when the
system policy below exposes the physical GPU. No admin rights needed;
effective on the next process start.

**Layer 2 - system policy (manual, elevated once per machine).** The
"Use hardware graphics adapters for all Remote Desktop Services
sessions" group policy. Set these registry values (HKLM
`SOFTWARE\Policies\Microsoft\Windows NT\Terminal Services`, all DWORD 1):

```powershell
$key = 'HKLM:\SOFTWARE\Policies\Microsoft\Windows NT\Terminal Services'
Set-ItemProperty -Path $key -Name bEnumerateHWBeforeSW -Value 1 -Type DWord
Set-ItemProperty -Path $key -Name bAVC444Mode -Value 1 -Type DWord
Set-ItemProperty -Path $key -Name AVCHardwareEncodePreferred -Value 1 -Type DWord
```

`bEnumerateHWBeforeSW` makes sessions enumerate the physical GPU before
the software adapter; `bAVC444Mode` prefers the higher-quality codec and
`AVCHardwareEncodePreferred` keeps the RDP encoder off the CPU. Requires
Windows 10 / Server 2016 or newer, applies to **newly created sessions**
only - reconnect existing seats or reboot to pick it up.

The About box render diagnostics tell you the current state: whether you
are in a remote session, whether the GPU policy is on, and the measured
active framerate.

With layer 1 alone the app *asks* for hardware but may still find only
the software adapter in the session; with layer 2 alone WPF still
refuses the hardware path because it sees a remote session. Both on =
the WPF UI renders on the physical GPU inside RDP-type sessions.

## Rebuilding while it's running

The service and any running UI instance lock the exe. Before publishing:

    taskkill /f /im GameManager.exe
    sc stop GameManagerService

then build, then `sc start GameManagerService`. Both need an elevated prompt.

## Files

- `App.xaml(.cs)` - styles, mode dispatch on startup
- `MainWindow.xaml(.cs)` - the UI, keyboard/gamepad/touch/scroll handling
- `ServiceHost.cs` - service host, pipe server, per-session startup
- `Launcher.cs` - protocol registration, elevation, config path
- `SingleInstance.cs` - per-user mutex and activation
- `GameInfo.cs` - tile model
- `GamePad.cs` - joyGetPosEx polling
- `ShellIcons.cs` - icon extraction from .lnk
- `Config.cs` - json read/write
- `Strings.cs` - bilingual (Chinese/English) UI strings, auto-detected

## License

Copyright (C) 2026 xRetia

This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.

This program is distributed in the hope that it will be useful,
but WITHOUT ANY WARRANTY; without even the implied warranty of
MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
GNU General Public License for more details.

You should have received a copy of the GNU General Public License
along with this program in the [LICENSE](LICENSE) file. If not, see
<https://www.gnu.org/licenses/>.
