# User guide

## Global shortcuts (defaults, change in Settings)

| Action | Shortcut |
|---|---|
| Capture region | Ctrl+Shift+S |
| Capture window | Ctrl+Shift+W |
| Capture full screen | Ctrl+Shift+F |
| Start / stop recording (primary display) | Ctrl+Shift+R |
| Open editor | Ctrl+Shift+E |

Win+Shift+S is not used because Windows reserves it for the Snipping Tool. If another app already owns a shortcut,
Home shows "In use by another app".

In-app: Ctrl+1…5 switch pages, Ctrl+, opens Settings.

## Screenshots

1. Home → *Capture Region* (or the Screenshot page, the tray icon, or a shortcut).
2. The screen freezes and dims. Drag a rectangle; the size label shows physical pixels. Drag the handles to resize,
   drag inside to move, arrow keys nudge by 1 px (Shift+arrows resize). Clicking without dragging selects the window
   under the cursor. **Enter** or *Capture* confirms; **Esc** or right-click cancels.
3. Window mode: hover to highlight a window, click to capture it.
4. The capture is saved (if *Save automatically* is on), copied (if enabled) and opened in the editor (if enabled).
   At least one of these is always on so a capture is never lost.

Capture delay (Screenshot page or Settings) shows a countdown you can cancel with Esc.

## Editor

Tools (letter = shortcut): Select **V**, Crop **C**, Arrow **A**, Line **L**, Rectangle **R**, Ellipse **E**,
Text **T**, Steps **N**, Highlight **H**, Blur **B**, Pixelate **P**.

- Drag to draw; hold Shift for squares/45° lines. Select objects to move them, drag handles to resize, Delete to
  remove, arrows to nudge (Shift = 10 px).
- The inspector edits colour, stroke, opacity and size of the selected object (or of new objects when nothing is
  selected). Text is edited in the inspector's *Text* box (double-click a text object to jump there).
- **Blur / Pixelate** permanently replace the pixels in the saved file. Use them for passwords, tokens and
  personal data; the original content cannot be recovered from the exported image.
- Crop: drag the area to keep. The original stays intact until you save; *Reset crop* undoes it.
- Markup stack: select layers, reorder (↑/↓), delete.
- Ctrl+Z / Ctrl+Y undo/redo · Ctrl+S save · Ctrl+Shift+S export (PNG/JPEG/WebP) · Ctrl+C copy the edited image ·
  Ctrl+V paste an image · Ctrl+O open · Ctrl+0 fit · Ctrl+Wheel zoom.
- *Save* overwrites the opened file with the edited version; *Export* writes a new file.
- PIXORA asks before discarding unsaved edits (opening another image, new capture, quitting).

## Recording

1. Screen Record page → choose *Full Screen* (pick the display), *Window* (click it) or *Selected Region* (drag).
2. Set frame rate, quality, output resolution, cursor, start delay, and audio: system sounds and/or microphone with
   their own volume (adjustable live while recording).
3. Press **REC**. PIXORA minimises; after the countdown a small control bar appears at the top of the screen with the
   elapsed time, audio levels, Pause/Resume and Stop. The bar is hidden from the recording itself.
4. Stop saves `Recording_<date>_<time>.mp4` and opens the preview.

The "REC" indicator is driven by the recorder's actual state; it shows PAUSED/SAVING when appropriate.
If the recorded window closes, the recording up to that point is saved.

## Video editing (basic)

Open a recording, then **Edit video (trim, cut, mute)** on its preview page (or *Edit video* in the Library action
bar / right-click menu).

- The **timeline** shows frame thumbnails across the whole recording and a time ruler. Click it to move the
  playhead and select a part; Ctrl+Wheel or the zoom slider zooms.
- **Split** (S) cuts the selected part in two at the playhead.
- **Delete part** (Delete) removes the selected part; removed parts are darkened on the timeline and skipped in the
  preview. At least one part always remains.
- **Trim**: drag the white grips at a part's left or right edge.
- **Mute part** (M) silences one part; **No audio** removes sound from the whole export.
- Space plays/pauses, ←/→ move 1 s (Shift: 5 s), Home/End jump to the start/end, Ctrl+Z / Ctrl+Y undo/redo,
  *Reset* restores the original cut.
- **Export video** (Ctrl+S) saves the result as a **new** MP4 (default name `<recording>_edited.mp4`) with the same
  quality as the source, adds it to the Library and opens its preview. The original recording is never modified, and
  you are asked before leaving with edits that were not exported.

## Library

Categories on the left; search, sort, grid/list at the top. Select one or many items (Ctrl/Shift-click) for the
action bar, or right-click. Double-click/Enter opens, F2 renames, Delete moves to Trash.

- **Move to Trash** only hides the entry. Files stay where they are.
- In Trash: **Restore**, **Remove from library** (file untouched) or **Delete permanently** (file goes to the Recycle
  Bin, after confirmation).
- Files moved or deleted outside PIXORA show **FILE MISSING**; *Refresh* re-scans and recovers them if they return.
- Copy: a single screenshot is copied as an image; anything else as files (paste in Explorer).

## Settings

All changes are validated on *Save settings* (writable folders, valid templates, unique shortcuts).
*Discard changes* reverts the form; *Restore defaults* resets everything except your files.
