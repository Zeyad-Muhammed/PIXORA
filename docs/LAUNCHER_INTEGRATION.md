# NOVU Launcher integration

## Mechanism (existing, reused)

NOVU Launcher (`F:\NOVU Launcher`, Tauri 2 + React) shows NOVU's own Windows apps in the **NOVU Store** section of
*Catalog → Apps/All*. Each store app is:

1. a Rust module `src-tauri/src/<id>.rs` with four Tauri commands:
   `<id>_status` (is `%LOCALAPPDATA%\Programs\<APP>\<APP>.exe` present), `<id>_install` (download
   `<APP>-Setup.exe` from `https://github.com/Zeyad-Muhammed/<repo>/releases/latest/download/`, run `/S /D=<dir>`,
   launch), `<id>_launch`, `<id>_uninstall` (`Uninstall.exe /S`, remove folder), and progress events on `<id>://progress`;
2. registered in `src-tauri/src/lib.rs` (`mod` + `generate_handler!`);
3. a card in `src/components/StoreApps.tsx` with its logo from `src/assets/`.

The SQLite `apps` table in the launcher is for user-added apps (Admin panel) and is not used for NOVU's own apps.

## What was added for PIXORA

Branch **`feature/pixora-store`** of the NOVU Launcher repository (based on `android-app`, where the store code
lives), commit `5405bdf`, created as a git worktree at `F:\NOVU Launcher-worktrees\pixora-store` so the checked-out
`android-app` working tree was not touched.

| File | Change |
|---|---|
| `src-tauri/src/pixora.rs` | New, same structure as `vertex.rs`: install dir `%LOCALAPPDATA%\Programs\PIXORA`, exe `PIXORA.exe`, download `…/Zeyad-Muhammed/PIXORA/releases/latest/download/PIXORA-Setup.exe`, plus 3 unit tests |
| `src-tauri/src/lib.rs` | `mod pixora;` and the four `pixora::*` commands appended to the handler list |
| `src/components/StoreApps.tsx` | `pixora` id + card ("Screen capture", 49 MB) |
| `src/assets/pixora.png` | 256×256 icon from `assets/brand/pixora-256.png` |

No other app registration was modified. Missing executable → `pixora_launch` returns "PIXORA is not installed" and
the card shows *Download & Install* (no crash). The installer's `/S /D=` contract and default path match
`packaging/pixora.nsi`.

## Verification performed

- `npm run build` (`tsc` + Vite) on the branch: success.
- `cargo check`: success. `cargo test pixora`: 3/3 passed (path contract, status, friendly missing-install error).
- Ran `PIXORA-Setup.exe /S /D=%LOCALAPPDATA%\Programs\PIXORA` (exactly what `pixora_install` does after
  downloading), re-ran the tests (status = installed), then started `PIXORA.exe` with the install folder as working
  directory as `pixora_launch` does: the app window opened.

Not verified: the full download path, because no GitHub release of PIXORA is published yet (see below), and a
visual check of the card in a running launcher build.

## Remaining steps to go live

1. Merge `feature/pixora-store` into the launcher's release branch and ship a launcher update.
2. Publish a PIXORA GitHub release whose assets include `PIXORA-Setup.exe` (see RELEASE.md). The launcher downloads
   anonymously, so the **PIXORA repository must be public** for *Download & Install* to work (VYRO/CYVEX/VERTEX
   repos are public for the same reason).

## Regression procedure

```powershell
cd "F:\NOVU Launcher-worktrees\pixora-store"
npm run build
cd src-tauri; cargo test pixora
```
Then in the launcher: Catalog → Apps → NOVU Store shows four cards (VYRO, CYVEX, VERTEX, PIXORA); PIXORA's
*Download & Install* installs and opens it; *Open* starts it; *Uninstall* removes it and the card returns to
*Download & Install*; the other three cards behave as before.
