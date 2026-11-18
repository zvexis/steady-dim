# Changelog

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
