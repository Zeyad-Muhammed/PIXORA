# Architecture

```
            ┌──────────────────────────── PIXORA.App (WinUI 3, net10.0-windows) ───────────────────────────┐
            │ MainWindow + Pages (Home, Screenshot, Record, Editor, Library, Preview, Settings)            │
            │ ViewModels (CommunityToolkit.Mvvm)    Services (DI: Microsoft.Extensions.DependencyInjection) │
            │   CaptureController  RecordingController  MediaLibraryService  EditorSession  Notifier      │
            │   ShellHost (hidden Win32 window: RegisterHotKey + Shell_NotifyIcon)                          │
            │ Views: SelectionOverlay (per monitor), CountdownWindow, RecordingHud                          │
            └───────┬───────────────┬──────────────────┬──────────────────┬───────────────────────────────┘
                    │               │                  │                  │
          PIXORA.Capture   PIXORA.Recording     PIXORA.Library      PIXORA.Editor
          GDI BitBlt /     WGC frame pool →     SQLite index,       annotation model,
          PrintWindow,     MediaStreamSource →  scan, thumbnails    Skia renderer, undo,
          clipboard,       MediaTranscoder;     (net10.0)           export (net10.0)
          ScreenshotSaver  NAudio mixer                 │                  │
                    └───────────────┴──────────────────┴──────── PIXORA.Core (net10.0) ─┘
                                 settings, paths, naming templates, hotkeys, log
```

Dependencies point downwards only. Core, Editor and Library have no Windows-only APIs and are unit-tested in isolation.
Capture references Editor only for `ImageExporter` (shared encoding/atomic save).

## Data flow

**Screenshot**: `CaptureController` hides the main window → optional `CountdownWindow` → mode:
- *Region*: every monitor is captured first (`ScreenCapture.CaptureMonitor`), then `SelectionOverlay` shows the
  frozen image full-screen per monitor (physical-pixel coordinates via `XamlRoot.RasterizationScale`). The result
  is cropped from the frozen image, so the file matches what the user saw.
- *Window*: overlay highlights windows from `EnumWindows` (Z-order, cloaked/tool windows skipped); the chosen HWND
  is captured with `PrintWindow(PW_RENDERFULLCONTENT)`, falling back to a screen copy if the app renders blank.
- *Full screen / display*: `BitBlt(CAPTUREBLT)` of the virtual desktop or one monitor.

Then `ScreenshotSaver` (template name, unique path, atomic temp-file write) → `MediaLibraryService.AddAsync`
(probe + thumbnail) → optional clipboard (CF_DIB + PNG) → optional `EditorSession.OpenBitmap`.

**Editor**: `EditorDocument` = immutable base bitmap + `List<Annotation>` + optional crop + snapshot undo stack.
`Renderer.Composite` draws annotations in order; `BlurAnnotation`/`PixelateAnnotation` overwrite the composited
pixels (blur via a Skia image filter on the region, pixelate via block averages), so redaction is part of the
pixels written by `ImageExporter`, not an overlay. Export = `Renderer.Flatten` (composite + crop).

**Recording**: `GraphicsCaptureItem` (monitor/HWND via `IGraphicsCaptureItemInterop`) → free-threaded
`Direct3D11CaptureFramePool` → each frame copied to a CPU buffer (`SoftwareBitmap.CreateCopyFromSurfaceAsync`).
A **video pump** emits constant-frame-rate BGRA samples from the latest frame (cropped for region mode) and an
**audio pump** pulls 20 ms of 48 kHz stereo PCM from `AudioMixer` (loopback + mic, resampled, per-source volume,
silence-filled). Both use one pausable clock, so pausing removes time from both streams and A/V stay aligned even
when loopback is silent. A `MediaStreamSource` feeds `MediaTranscoder` (H.264 + AAC, hardware when available)
writing `<name>.mp4.part`, renamed to `.mp4` only after a clean finish.

**Library**: `LibraryStore` (SQLite, WAL) keyed by full path. `ScanAsync` indexes the configured folders, flags
entries whose file vanished as *missing* (never deletes them), and clears the flag when files return. Trash is a
flag; permanent delete sends files to the Recycle Bin via `SHFileOperation(FOF_ALLOWUNDO)`.

## Storage locations

| What | Where |
|---|---|
| Settings | `%APPDATA%\PIXORA\settings.json` (atomic write, invalid file kept as `.bad`) |
| Library DB, thumbnails, logs, temp | `%LOCALAPPDATA%\PIXORA\` (`library.db`, `thumbs\`, `logs\`, `temp\`) |
| Screenshots (default) | `Pictures\PIXORA\Screenshots` |
| Recordings (default) | `Videos\PIXORA\Recordings` |
| Program files | `%LOCALAPPDATA%\Programs\PIXORA` (installer) |

Nothing is written to the program folder.

## NOVU integration

There is no shared NOVU Core library in the ecosystem; PIXORA is standalone and integrates through the NOVU
Launcher store contract (see [LAUNCHER_INTEGRATION.md](LAUNCHER_INTEGRATION.md)). It does not depend on any NOVU
service at runtime.

## Threading

UI work stays on the dispatcher thread; SQLite, encoding, scanning and thumbnails run on the thread pool.
`ShellHost` runs its own STA message loop. Recording frames arrive on capture threads, pumps run on dedicated
threads, and Media Foundation pulls samples through deferrals. Global state changes are marshalled back via
`DispatcherQueue`.
