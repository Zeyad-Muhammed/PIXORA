# PIXORA Capture Studio

Screenshot, annotation and screen-recording app for Windows, part of the NOVU suite.
Native **WinUI 3 / .NET 10**, distributed through **NOVU Launcher** (NOVU Store) and a standalone installer.

Everything stays on the PC: no cloud upload, no telemetry.

## Features (v1.0.0)

| Area | What works |
|---|---|
| **Capture** | Region (frozen-screen overlay with 8 resize handles, live pixel size, arrow-key nudge, click-to-snap to a window), window (hover highlight; PrintWindow so covered windows still render), full virtual desktop, single display. Capture delay, global hotkeys, tray menu, clipboard copy, auto-save with name templates. |
| **Editor** | Select/move/resize, crop (non-destructive), arrow, line, rectangle, ellipse, text, numbered steps, highlight, **blur and pixelate that rewrite the saved pixels**, colour/stroke/opacity/size, layer order, undo/redo, zoom/fit, copy, save / save as PNG, JPEG, WebP, drag-and-drop and paste. Unsaved-changes prompts. |
| **Recording** | Display, window or region; system audio (WASAPI loopback) and microphone with independent live volume; 15/24/30/60 fps; native→480p output; quality presets; countdown; pause/resume; floating HUD that is excluded from the capture; H.264/AAC MP4 via Media Foundation. |
| **Library** | SQLite index of screenshots and recordings; grid/list; thumbnails; search; sort; All/Screenshots/Recordings/Favorites/Trash; rename, open, reveal, copy, export, favourite, multi-select; trash (files untouched), remove entry, delete to Recycle Bin with confirmation; missing-file detection. |
| **Preview** | Playback with system transport controls (play/pause, seek, volume), details, rename, save as, export, reveal. |
| **Settings** | Startup, tray, notifications, theme, folders, name templates, format/quality, delay, recording defaults, audio sources, global shortcuts, restore defaults; validated before saving. |

Not in this release: webcam overlay (see [docs/TROUBLESHOOTING.md](docs/TROUBLESHOOTING.md#known-limitations)).

## Build

Prerequisites: Windows 10 1903+ / Windows 11, **.NET SDK 10.0.4xx**. Visual Studio is *not* required. For the installer: NSIS 3.

```powershell
dotnet build PIXORA.slnx -c Debug
dotnet test tests/PIXORA.Tests             # 59 tests; capture/recording tests need a real desktop
dotnet run --project src/PIXORA.App
pwsh packaging/build.ps1                   # tests + self-contained publish + artifacts/installer/PIXORA-Setup.exe
```

## Layout

```
src/PIXORA.Core        settings, paths, naming templates, hotkey parsing, logging (no UI, no Windows deps)
src/PIXORA.Editor      annotation model, renderer (blur/pixelate), undo/redo, image export (SkiaSharp)
src/PIXORA.Library     SQLite media index, scanning, thumbnails
src/PIXORA.Capture     GDI screen/window capture, monitor/window enumeration, clipboard, screenshot saving
src/PIXORA.Recording   Windows.Graphics.Capture + NAudio + MediaTranscoder recorder, media probing
src/PIXORA.App         WinUI 3 app (MVVM + DI), overlay/HUD windows, tray + global hotkeys
tests/PIXORA.Tests     xUnit tests for every layer
packaging/             NSIS installer + build script
tools/IconGen          regenerates the icon set from assets/brand/pixora-logo.png
docs/                  architecture, install, user guide, testing, troubleshooting, launcher, release
```

See [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md).

## Status

v1.0.0, first release. Built and tested on Windows 11 Pro 26200 (two monitors, 100% scaling). See
[docs/TESTING.md](docs/TESTING.md) for exactly what was verified and [docs/PROGRESS.md](docs/PROGRESS.md) for open items.

## Licence

No licence has been chosen yet. Until one is added, all rights are reserved by the owner.
Third-party components: [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
