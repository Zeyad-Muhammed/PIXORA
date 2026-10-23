# Release procedure

1. Bump `<Version>` in `Directory.Build.props` (used by the app, the installer metadata and the uninstall entry).
2. Build and test:
   ```powershell
   pwsh packaging/build.ps1
   ```
   Output: `artifacts/publish/PIXORA/` (self-contained app) and `artifacts/installer/PIXORA-Setup.exe` +
   `SHA256SUMS.txt`.
3. Smoke test: `artifacts\installer\PIXORA-Setup.exe /S /D=%TEMP%\pixora-smoke`, launch, uninstall
   (`%TEMP%\pixora-smoke\Uninstall.exe /S`).
4. Commit, tag and publish a GitHub release with the installer attached (the asset name must stay
   `PIXORA-Setup.exe` because NOVU Launcher downloads `releases/latest/download/PIXORA-Setup.exe`):
   ```powershell
   git tag v1.0.0; git push origin main --tags
   gh release create v1.0.0 artifacts/installer/PIXORA-Setup.exe artifacts/installer/SHA256SUMS.txt --title "PIXORA 1.0.0" --notes-file docs/PROGRESS.md
   ```

## Packaging decisions

- **NSIS, not MSIX**: every NOVU desktop app ships an NSIS installer that the launcher runs with `/S /D=`; MSIX
  cannot be installed that way and would need a trusted signing certificate. The app is unpackaged
  (`WindowsPackageType=None`) and self-contained, so no runtime prerequisites.
- **x64 only**: the build machine and all NOVU apps target x64.
- **Size**: ~186 MB on disk, 49 MB installer (LZMA). Only the needed Windows App SDK components are referenced
  (the metapackage would add ~40 MB of AI/ML runtimes), and SkiaSharp's 90 MB native symbol file is removed on publish.

## Code signing (not done)

The installer and `PIXORA.exe` are **unsigned**. To sign, obtain an Authenticode certificate (OV/EV) and run, after
publish and after makensis:
```powershell
signtool sign /fd SHA256 /tr http://timestamp.digicert.com /td SHA256 /f <cert.pfx> /p <password> artifacts\publish\PIXORA\PIXORA.exe
signtool sign /fd SHA256 /tr http://timestamp.digicert.com /td SHA256 /f <cert.pfx> /p <password> artifacts\installer\PIXORA-Setup.exe
```
(`signtool` comes with the Windows SDK.) Never commit the certificate; `.gitignore` excludes `*.pfx`.
