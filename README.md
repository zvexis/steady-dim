# SteadyDim

A small Windows tray app for reliable screen dimming **without overlay windows**.

SteadyDim combines two layers:

- **DDC/CI hardware brightness** per monitor — the real backlight.
- **Optional global deep-night dim** through the Windows Magnification API.

The deep layer is applied by the desktop compositor, so new windows, taskbar
thumbnails, and Alt+Tab cannot escape it by being "above" a topmost window.
There is no transparent black window to lose a z-order race.

## Why

Many screen dimmers draw a click-through black overlay over each monitor. That
works until a new topmost/elevated window, taskbar preview, Alt+Tab surface, or
secure desktop gets above the overlay. The usual fixes are "toggle the overlay"
or "restart the app".

SteadyDim avoids that architecture:

- hardware brightness is controlled through the monitor itself;
- the extra dim is a compositor-level color transform;
- the app rescans after display, power, and session events;
- the hardware base remains dim even if the extra layer fails.

## Features

- Per-monitor DDC/CI brightness sliders, including three or more displays.
- Configurable global deep-night dim, default **80% reduction** when enabled.
- Tray panel; no main window and no overlay.
- Automatic rescan on:
  - display mode changes,
  - monitor arrival/removal,
  - resume from sleep,
  - session unlock.
- A watchdog that detects when a powered-off monitor comes back or a display is
  not yet DDC-ready.
- Retries DDC operations and verifies the brightness readback.
- Scheduled task at logon with a **10 second delay** and restart-on-failure.
- Portable settings and logs in `data\`.
- No driver, Windows service, or administrator rights.
- `Reset to full brightness` in the tray menu.
- Command-line diagnostics and recovery modes.

## Requirements

- Windows 10 or Windows 11, x64.
- .NET 9 Desktop Runtime.
- Monitor(s) with DDC/CI brightness control enabled.
  Most external monitors support it; it may need to be enabled in the monitor's
  own OSD menu.

## Run

Download the release zip and extract it to a writable folder.

1. Double-click **`Start SteadyDim.vbs`**.
   This starts the app through the installed .NET host and keeps the console
   hidden. It also avoids security products that auto-sandbox new unsigned
   apphosts and deny registry/AppData access.
2. If your security software does not sandbox unsigned apps, `SteadyDim.exe` can
   be launched directly instead.
3. In the panel, set each monitor's hardware brightness and enable
   **Deep night dim** if you need to go below the monitor's minimum.
4. Leave **Start at logon** checked to register the `SteadyDim` scheduled task.

The panel hides when it loses focus. Reopen it from the tray icon or by running
the launcher again.

## Usage

The tray panel has:

- **Deep night dim** — global compositor dim, 0–90% reduction. Opening the panel
  temporarily pauses this layer so the panel stays readable; closing restores it.
- **Hardware brightness (DDC/CI)** — one slider per monitor.
- **Start at logon** — creates or removes the scheduled task.

Tray menu:

- Open SteadyDim
- Deep night dim
- Start at logon
- Rescan displays
- Reset to full brightness
- Open log
- Exit

## Settings and logs

Settings and logs live next to the executable:

- `data\config.json`
- `data\steadydim.log`
- `data\diagnose.txt` (after `--diagnose`)

The data folder is portable, so moving the application folder keeps settings.
If the folder is not writable (for example, `C:\Program Files`), the app falls
back to `%LOCALAPPDATA%\SteadyDim`.

## Command line

```text
SteadyDim.exe --diagnose    Write a hardware/API diagnostic and exit.
SteadyDim.exe --reset       Clear deep dim, set DDC to maximum, reset config.
SteadyDim.exe --no-startup  Start without checking/registering startup.
SteadyDim.exe --startup     Startup-task launch mode.
```

When using the VBS launcher, run the DLL directly for diagnostics:

```text
dotnet SteadyDim.dll --reset
dotnet SteadyDim.dll --diagnose
```

## Limitations

- The deep layer is **global**, not per monitor. Per-monitor differences come
  from DDC brightness.
- It is a desktop compositor effect. It may not apply to the secure desktop
  (UAC) or some exclusive-fullscreen paths; the hardware base still applies.
- The app intentionally does **not** use `SetDeviceGammaRamp`. Windows rejects
  deep ramps, they reset on display events, and their behavior in HDR is
  undefined.
- Windows Magnifier also uses the Magnification API; running both at once may
  conflict.
- DDC/CI support varies by monitor, cable, dock, and driver. The app reports
  monitors that do not expose brightness control.

## Troubleshooting

- **Start at logon does not stick.** Security software may block registry or
  Task Scheduler access from the sandboxed apphost. Launch via
  `Start SteadyDim.vbs`, or add the SteadyDim folder to your security software's
  trusted/excluded files.
- **A monitor is missing.** Enable DDC/CI in its OSD, check the cable/adapter,
  then use the tray menu or restart the app so it rescans.
- **The screen is too dark.** Use the tray menu **Reset to full brightness**, or
  run `--reset`.
- **Something failed.** Check `data\steadydim.log` and run `--diagnose`.

## Build

Requires the .NET 9 SDK:

```powershell
dotnet build .\SteadyDim.csproj -c Release
dotnet publish .\SteadyDim.csproj -c Release -r win-x64 --self-contained false -o .\publish
```

Or create the release zip locally:

```powershell
.\build-release.ps1
```

The output is `dist\SteadyDim-win-x64.zip`.

## Release

The GitHub Actions workflow in `.github/workflows/build.yml` builds the app on
Windows and attaches a zip to a GitHub Release when a `v*` tag is pushed.

Create an empty GitHub repository, then push `main` and the tag:

```powershell
git remote add origin https://github.com/<owner>/steady-dim.git
git push -u origin main
git push origin v1.0.1
```

The tag push builds the release on GitHub. You can also attach the locally built
`dist\SteadyDim-win-x64.zip` manually.


## License

MIT. See [LICENSE](LICENSE).
