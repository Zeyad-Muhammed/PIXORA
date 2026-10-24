# Testing

## Commands

```powershell
dotnet test tests/PIXORA.Tests                              # everything (needs an interactive desktop)
dotnet test tests/PIXORA.Tests --filter "Category!=Desktop" # what CI runs (no display/GPU/audio needed)
```

Desktop tests (`[Trait("Category","Desktop")]`) capture the real screen, use the clipboard and record real MP4
files with the Windows encoder. If no monitor is present they return early instead of failing.

## Results: 2026-10-23, Windows 11 Pro 26200, 2 monitors, Release build

**74 / 74 passed** (50 non-desktop, 24 desktop) on 2026-10-25 with v1.1.0 (video editing added).

| Area | Verified by tests |
|---|---|
| Settings | defaults valid; persist across store instances (restart); invalid values rejected and not written; duplicate shortcuts rejected; corrupt file → defaults + `.bad` kept; restore defaults; unwritable folder detected |
| Naming / hotkeys | template tokens, sanitising, collision-free names; shortcut parsing |
| Editor | pixelate makes every block uniform and leaves outside pixels identical; **blur lowers detail in the exported PNG to <10% of the original** while outside pixels stay byte-identical; blur strength floor; annotation colours baked into export; arrow/line/ellipse/text render; crop changes export size without touching the base; undo/redo incl. crop; move + hit-testing; handles; step numbering; PNG/JPEG/WebP round-trip; atomic save never leaves temp files or destroys the old file; non-image rejected |
| Library | scan + persistence across instances; idempotent rescan; missing detection and recovery; search (incl. literal `%`/`_`), all sorts, categories, counts; trash/restore/remove never delete files; permanent delete requires the file to be gone; rename moves file, keeps extension, rejects collisions/invalid names; favourites persist; thumbnails created, cached, pruned |
| Capture | monitor enumeration; monitor/virtual-screen captures have exact pixel sizes and are opaque; region capture matches the same area of a full capture; empty area and invalid window give friendly errors; window enumeration + capture; clipboard image (pixel-exact) and file-list round-trips; saver uses template/format and never overwrites; unwritable folder reported |
| Recording | 3 s region recording → playable MP4 with 1 video track, correct size, duration 2.2–4.2 s, `.part` removed; with system audio → 1 audio + 1 video track, duration within 1 s of the recorder clock; pause excludes paused time; display recording scaled to 480p; start failure reported + recorder reusable; invalid window fails cleanly with no files; stop without recording rejected; device listing |
| Video timeline | split/delete shorten the output; split refuses edges and removed parts; last part cannot be deleted; trims clamp to neighbours, bounds and a 0.2 s minimum; undo/redo for every edit and a drag counts as one step; mute all / reset; playback skips removed parts and maps source → output time |
| Video export | cutting 1–3 s out of a 4.5 s recording (with audio, first part muted) gives a playable MP4 whose length matches the timeline (±0.6 s) and leaves the original byte-identical, with no `.part` left; trimming both ends matches the timeline; exporting over the original is refused; thumbnails decode at the requested height and show the real frame at their time (red/blue/green test video) |

Playability was checked by opening each file with `Windows.Media.Core.MediaSource` and reading its tracks/duration.

Video editor UI (2026-10-25, via UI Automation on a generated 6 s colour video): opened from Library → *Edit video*;
thumbnails and ruler rendered; Split, Play/Pause, Delete part, Undo and Redo changed the timeline as expected (output
length, part count and removed time read back from the page). Export through the save dialog and drag-trimming with
the mouse were not automated; the export path itself is covered by the tests above.

## Also verified manually (this machine)

- Debug and Release builds of the whole solution: 0 errors.
- App launch; Home, Screenshot, Screen Record, Settings, Library and Editor pages render (screenshots taken of the
  running app). Full-screen capture from the Home quick action: window hides, 3000×1920 PNG of both monitors saved
  to `Pictures\PIXORA\Screenshots`, added to the library with thumbnail, opened in the editor, banner shown.
- Self-contained publish runs; installer built (49 MB); silent install → files, shortcuts, uninstall registry entry
  present, app launches; silent uninstall removes program files, shortcuts and registry entry and **keeps**
  settings, library DB and captures.
- NOVU Launcher integration: see [LAUNCHER_INTEGRATION.md](LAUNCHER_INTEGRATION.md) (3 Rust tests, TypeScript build,
  install-to-launcher-path + launch).

## Not verified (be aware)

- **Windows 10**: no machine available. Target/min version allow 1903+, but it has not been run there.
- **Mixed-DPI multi-monitor** (e.g. 100% + 150%): the test machine's monitors are both at 100%. Overlay coordinates
  are designed for per-monitor DPI but this configuration is untested.
- **Editor pointer gestures** (drawing/moving/resizing with the mouse) and the region overlay drag were not
  automated: synthetic mouse input on the shared desktop was not reliable while the machine was in use. The
  underlying model is unit-tested; use the checklist below.
- Long recordings (> 3 s), 60 fps at 4K, and microphone capture (no mic test performed; device enumeration only).
- Hotkeys and tray menu were exercised only by code review and startup (registration succeeded, no conflicts shown).

## Manual checklist

1. Ctrl+Shift+S → drag region → resize with a corner handle → Enter. File saved, editor opens.
2. Ctrl+Shift+W → hover windows (highlight + title) → click. Covered window captured completely.
3. Editor: draw rectangle (R), arrow (A), text (T, then type in inspector), step (N twice → 1, 2), blur (B) over
   text, pixelate (P). Move and resize one object; Ctrl+Z / Ctrl+Y; crop (C); Ctrl+S; reopen file from Library and
   confirm the blurred text is unreadable.
4. Open another image with unsaved edits → prompt appears; Cancel keeps edits.
5. Screen Record → Window → pick a window → REC → countdown → HUD (check it is not in the video) → Pause 3 s →
   Resume → Stop. Preview plays; duration excludes the pause; audio present when enabled.
6. Library: multi-select → Favorite, Export, Move to Trash → Trash → Restore; Delete permanently → file in Recycle Bin.
   Rename a file in Explorer → Refresh → old entry "FILE MISSING", new file indexed.
7. Settings: set an invalid template (`{x}`) → Save → error listed, nothing saved. Change frame rate → restart app →
   value kept.
8. Close window with tray enabled → app keeps running in tray; tray → Quit exits; with a recording running, Quit
   saves it first.
