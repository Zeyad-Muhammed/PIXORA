# Troubleshooting

## Logs

`%LOCALAPPDATA%\PIXORA\logs\pixora-YYYY-MM-DD.log` (Settings → About → *Open logs*). Logs contain operational
events and error messages only, never captured content. Kept for 14 days.

## Common problems

| Symptom | Cause / fix |
|---|---|
| Shortcut does nothing; Home says "In use by another app" | Another program registered it first. Choose a different combination in Settings. |
| Window capture is black | The window uses protected (DRM) content or exclusive full-screen. Use region/full-screen capture, or windowed mode. Minimised windows cannot be captured. |
| "No video frames were received" when recording | The source is minimised, protected, or the session is locked. Restore the window / unlock and retry. |
| Recording has no system audio | Nothing was playing, or no playback device exists (a warning is shown at start). Check the default output device. |
| "The microphone is unavailable or access is blocked" | Windows Settings → Privacy & security → Microphone → allow desktop apps; check the selected device. |
| Yellow border around a recorded window/display | A privacy indicator Windows draws while Windows.Graphics.Capture is active on some builds. PIXORA targets the Windows 10 API level and does not try to hide it. |
| "Cannot write to …" when saving | Folder is read-only, on a disconnected drive, or blocked by Controlled Folder Access. Pick another folder in Settings. |
| "Not enough free disk space" / recording refuses to start | Fewer than 200 MB free on the recording drive. |
| App does not start after install to a custom folder | Very long install paths (deeply nested folders) can exceed Windows path limits for WinUI resources. Use the default `%LOCALAPPDATA%\Programs\PIXORA`. |
| SmartScreen warning | The installer is not code-signed yet (see RELEASE.md). |
| Library shows FILE MISSING | The file was moved/deleted outside PIXORA. Restore it and press Refresh, or *Remove from library*. |
| A `*.mp4.part` file in the recordings folder | A recording was interrupted by a crash or power loss; it is unplayable and removed automatically on the next start (after 10 minutes). |

## Known limitations

- **Webcam overlay** (shown in the design) is not implemented in v1.0.0. No webcam controls are shown.
- **Logo** is a stand-in aperture mark; the approved Stitch logo file was not obtainable (remote URL returned 403).
  Replace `assets/brand/*` and re-run `tools/IconGen` (or drop in PNG/ICO files) when the original is available.
- **Font**: the design specifies Inter; the app uses Segoe UI Variable (bundled with Windows) to avoid shipping fonts.
- A region selection is limited to one monitor; *Full Screen* captures all monitors together.
- Recording is MP4 (H.264/AAC) only; no MKV, GIF or trimming/editing of videos.
- Audio drift over very long recordings has not been measured (recordings up to a few seconds verified).
- Windows 10 and mixed-DPI multi-monitor setups are untested (see TESTING.md).
- No auto-update inside PIXORA: updates come through NOVU Launcher (reinstall from the store).
