# Progress log

## 2026-10-23: v1.0.0

**Done**
- Preflight (docs/PREFLIGHT.md). WinUI 3 confirmed buildable with the .NET SDK alone.
- Solution with 6 projects + tests; design tokens from the Stitch `DESIGN.md` (dark charcoal surfaces, `#C75C64`
  accent used for primary/active states only), custom control templates for buttons/toggles with hover, pressed,
  checked, disabled and keyboard-focus states; light theme tokens included.
- Capture: region/window/full/display, delay, clipboard, auto-save, overlay with handles/size/confirm/cancel.
- Editor: all required tools, real blur/pixelate, crop, undo/redo, zoom/fit, copy/save/export (PNG/JPEG/WebP),
  unsaved-change prompts, drag-drop/paste.
- Recording: display/window/region, system audio + mic with live volume, fps/resolution/quality, countdown,
  pause/resume, HUD excluded from capture, MP4 finalisation via `.part` rename, crash leftovers cleaned.
- Library, Preview, Settings, tray + global hotkeys, single instance (second launch focuses the first),
  start with Windows.
- NSIS installer; NOVU Launcher store integration on branch `feature/pixora-store` (commit 5405bdf).
- Docs, CI workflow, third-party notices.

**Build / test**: Debug + Release build 0 errors. 59/59 tests pass. Installer 49 MB, install/uninstall verified.

**Open items**
- Webcam overlay (not implemented).
- Code signing certificate.
- Make the GitHub repo public (or otherwise host the installer) and publish release v1.0.0 so NOVU Launcher can
  download it; merge the launcher branch.
- Manual checklist in TESTING.md for pointer gestures; Windows 10 and mixed-DPI testing.
- Remaining compiler warnings are SkiaSharp `SKPath` obsolescence notices (API still supported).
