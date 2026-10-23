# Installation and removal

## Requirements

- Windows 10 version 1903 (build 18362) or later, x64. Verified only on Windows 11 build 26200.
- No separate runtime: the .NET 10 runtime and Windows App SDK are bundled (self-contained).

## Install

**From NOVU Launcher**: Catalog → Apps → NOVU Store → PIXORA → *Download & Install*. The launcher downloads
`PIXORA-Setup.exe` from the latest GitHub release and runs it silently.

**Manually**: run `PIXORA-Setup.exe`. It installs per user (no administrator prompt) to
`%LOCALAPPDATA%\Programs\PIXORA`, and adds a Start menu entry ("PIXORA Capture Studio"), a Desktop shortcut and
an *Apps & features* entry. Silent install: `PIXORA-Setup.exe /S /D=<folder>` (`/D` must be last and unquoted).

If you choose a folder that already contains other files, the installer uses a `PIXORA` subfolder inside it, so
uninstalling can never remove unrelated files.

The installer is **not code-signed**, so Windows SmartScreen may warn on first run ("More info → Run anyway").

## Uninstall

*Settings → Apps → Installed apps → PIXORA Capture Studio → Uninstall*, NOVU Launcher's *Uninstall*, or
`"%LOCALAPPDATA%\Programs\PIXORA\Uninstall.exe" /S`.

Removed: program files, shortcuts, uninstall entry, the "start with Windows" entry.
**Kept**: your screenshots and recordings, `%APPDATA%\PIXORA` (settings) and `%LOCALAPPDATA%\PIXORA` (library index,
thumbnails, logs). Delete those two folders by hand for a full reset.
