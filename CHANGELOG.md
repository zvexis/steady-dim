# Changelog

## 1.1.1 — 2026-11-24

- Replaced the three one-second Magnification retries with persistent backoff
  retries (1s, 2s, 4s, 8s, 16s, then every 30s).
- Added a 10-second Magnification signature watchdog that reapplies the effect
  if it disappears without a setting change.
- Request a process restart after 10 consecutive Magnification failures.
- Added **Repair deep dim** to the tray menu.

## 1.1.0 — 2026-11-19

- Swapped the panel layout: one shared DDC hardware-brightness slider for all
  monitors, plus per-monitor gamma trim sliders.
- Added per-display gamma trim in the 0–50% range with readback verification.
- Added automatic gamma reapplication after rescans; gamma is restored to its
  captured baseline when displays are released.
- Added a per-monitor DDC availability status.
- Migrated settings to schema 2 with a shared hardware brightness value.
- `--reset` now clears gamma trims and restores an identity gamma ramp.

## 1.0.2 — 2026-11-19

- Fixed deep dim silently stopping when a monitor was added/removed or the
  display topology otherwise changed after startup.
- Detect display-topology changes and automatically restart the app after a
  short delay so the Magnification API is freshly initialized.
- Avoid in-process `MagUninitialize`/`MagInitialize` cycles, which can leave the
  full-screen magnifier in an `ERROR_INVALID_PARAMETER` state.

## 1.0.1 — 2026-11-18

- Fixed a bug where the first monitor's valid DDC physical handle `0` was
  treated as null, leaving its hardware brightness slider ineffective.
- Added DDC readback verification and detailed failures.
- Added a startup retry for displays that are not DDC-ready yet.
- Added a monitor watchdog that rescans when the Windows monitor count changes
  or a display is not DDC-ready.
- Added support for three side-by-side monitors with Left/Center/Right labels.
- Added **Rescan displays** to the tray menu.

## 1.0.0 — 2026-11-18

- Initial release.
- DDC/CI hardware brightness per monitor.
- Global deep-night dim through the Windows Magnification API.
- No overlay windows; new windows, taskbar previews, and Alt+Tab stay dimmed.
- Tray panel with per-monitor sliders and a configurable deep-dim slider.
- Automatic rescan on display change, monitor hotplug, power resume, and session unlock.
- Scheduled task at logon with a delay and restart-on-failure.
- Portable settings in the `data` folder next to the executable.
- `--reset`, `--diagnose`, and `--no-startup` command-line modes.
- VBS launcher for environments where security software sandboxes unsigned apphosts.
