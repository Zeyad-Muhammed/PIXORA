# Preflight (2026-10-23)

| Check | Result |
|---|---|
| `F:\` exists / writable | Yes. `F:\PIXORA` did not exist; created fresh (nothing overwritten). |
| Design archive | `%USERPROFILE%\Downloads\stitch_pixora_capture_studio.zip`: 8 screens (`code.html` + `screen.png` each) and `fluent_dark_capture/DESIGN.md` (tokens). Extracted copies kept out of the repo; reference screenshots are not committed. |
| Approved logo | Only referenced as a remote `lh3.googleusercontent.com/aida/...` image in the HTML. With approval it was requested and returned **HTTP 403**, so a stand-in mark was used at first. The owner then supplied the logo image (2026-10-23); its painted checkerboard background was removed to real transparency and saved as `assets/brand/pixora-logo.png`, from which `tools/IconGen` generates every icon size and `pixora.ico`. |
| .NET | SDK 10.0.400; runtimes Microsoft.NETCore.App / WindowsDesktop.App 10.0.11. |
| Visual Studio / Build Tools | Not installed. WinUI 3 builds with the .NET SDK + NuGet packages alone (verified). |
| Windows SDK | 10.0.26100.0 installed; build uses Microsoft.Windows.SDK.BuildTools 10.0.28000.1721 from NuGet. |
| Windows App SDK | 2.5.1 component packages (Foundation 2.3.12, InteractiveExperiences 2.1.9, WinUI 2.3.9), self-contained. |
| NSIS | `makensis.exe` found in `%LOCALAPPDATA%\tauri\NSIS` (from NOVU Launcher builds). |
| Git / GitHub CLI | git 2.43.0; gh 2.100.0, signed in as **Zeyad-Muhammed** (scopes: repo, gist, read:org). Token not displayed. |
| FFmpeg | Not installed and not needed (Media Foundation encoders are used). |
| OS | Windows 11 Pro 10.0.26200, 2 monitors. No Windows 10 machine available. |

## Existing NOVU ecosystem

- **NOVU Launcher** (`F:\NOVU Launcher`): Tauri 2 + React. Its own apps (VYRO, CYVEX, VERTEX) are integrated
  as **NOVU Store** entries: one Rust module per app (`src-tauri/src/<app>.rs`) exposing
  `<app>_status/install/launch/uninstall`, plus a card in `src/components/StoreApps.tsx`. Install = download
  `<APP>-Setup.exe` from `github.com/Zeyad-Muhammed/<repo>/releases/latest`, run with `/S /D=%LOCALAPPDATA%\Programs\<APP>`.
  The latest store code lives on branch `android-app` (v1.9.0); `main` is at v1.3.1. The repo has no git remote.
- **Other NOVU desktop apps**: Python/PyInstaller + NSIS (VERTEX, CYVEX), Flutter (Hafiza, Itisam, HabitFlow).
  There is no existing .NET/WinUI app and no shared "NOVU Core" library to reuse. The integration contract
  that exists in practice is the launcher's store mechanism above, which PIXORA follows.
- **Repo conventions**: store apps' repos are **public** (the launcher downloads their releases anonymously);
  internal projects are private.
