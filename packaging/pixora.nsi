; PIXORA Capture Studio installer (NSIS). Mirrors the NOVU convention used by VERTEX/CYVEX/VYRO:
;   per-user install to %LOCALAPPDATA%\Programs\PIXORA, no admin rights, silent mode "/S /D=<dir>"
;   so NOVU Launcher can install it unattended.
; Build: packaging\build.ps1 (publishes the app, then runs makensis with /DVER=<version>).
Unicode true
!ifndef VER
  !define VER "1.0.0"
!endif
!ifndef SRC
  !define SRC "..\artifacts\publish\PIXORA"
!endif
!ifndef OUT
  !define OUT "..\artifacts\installer\PIXORA-Setup.exe"
!endif
!define APP "PIXORA Capture Studio"
!define EXE "PIXORA.exe"
!define UNKEY "Software\Microsoft\Windows\CurrentVersion\Uninstall\PIXORA"

Name "${APP}"
OutFile "${OUT}"
InstallDir "$LOCALAPPDATA\Programs\PIXORA"
RequestExecutionLevel user
SetCompressor /SOLID lzma
Icon "..\assets\brand\pixora.ico"
UninstallIcon "..\assets\brand\pixora.ico"
BrandingText "${APP} ${VER}"
VIProductVersion "${VER}.0"
VIAddVersionKey "ProductName" "${APP}"
VIAddVersionKey "CompanyName" "NOVU"
VIAddVersionKey "FileDescription" "${APP} installer"
VIAddVersionKey "FileVersion" "${VER}"
VIAddVersionKey "ProductVersion" "${VER}"
VIAddVersionKey "LegalCopyright" "Copyright (c) NOVU"

!include "MUI2.nsh"
!include "FileFunc.nsh"
!define MUI_ICON "..\assets\brand\pixora.ico"
!define MUI_UNICON "..\assets\brand\pixora.ico"
!define MUI_FINISHPAGE_RUN "$INSTDIR\${EXE}"
!define MUI_FINISHPAGE_RUN_TEXT "Start PIXORA Capture Studio"
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_LANGUAGE "English"

Section "Install"
  ; Close a running copy so files can be replaced (reinstall / update from NOVU Launcher).
  nsExec::Exec 'taskkill /F /IM ${EXE}'
  Sleep 300
  ; Never install into an unrelated non-empty folder: uninstall removes $INSTDIR recursively.
  IfFileExists "$INSTDIR\${EXE}" dir_ok
  ${DirState} "$INSTDIR" $0
  StrCmp $0 "1" 0 dir_ok
    StrCpy $INSTDIR "$INSTDIR\PIXORA"
  dir_ok:
  SetOutPath "$INSTDIR"
  File /r /x "*.pdb" "${SRC}\*.*"
  WriteUninstaller "$INSTDIR\Uninstall.exe"
  CreateShortcut "$DESKTOP\PIXORA.lnk" "$INSTDIR\${EXE}" "" "$INSTDIR\${EXE}" 0
  CreateShortcut "$SMPROGRAMS\PIXORA Capture Studio.lnk" "$INSTDIR\${EXE}" "" "$INSTDIR\${EXE}" 0
  WriteRegStr HKCU "${UNKEY}" "DisplayName" "${APP}"
  WriteRegStr HKCU "${UNKEY}" "DisplayVersion" "${VER}"
  WriteRegStr HKCU "${UNKEY}" "Publisher" "NOVU"
  WriteRegStr HKCU "${UNKEY}" "DisplayIcon" "$INSTDIR\${EXE}"
  WriteRegStr HKCU "${UNKEY}" "InstallLocation" "$INSTDIR"
  WriteRegStr HKCU "${UNKEY}" "UninstallString" '"$INSTDIR\Uninstall.exe"'
  WriteRegStr HKCU "${UNKEY}" "QuietUninstallString" '"$INSTDIR\Uninstall.exe" /S'
  WriteRegDWORD HKCU "${UNKEY}" "NoModify" 1
  WriteRegDWORD HKCU "${UNKEY}" "NoRepair" 1
SectionEnd

Section "Uninstall"
  nsExec::Exec 'taskkill /F /IM ${EXE}'
  Sleep 300
  Delete "$DESKTOP\PIXORA.lnk"
  Delete "$SMPROGRAMS\PIXORA Capture Studio.lnk"
  ; Remove the "start with Windows" entry the app may have created.
  DeleteRegValue HKCU "Software\Microsoft\Windows\CurrentVersion\Run" "PIXORA Capture Studio"
  DeleteRegKey HKCU "${UNKEY}"
  ; Program files only. Screenshots, recordings, settings and the library database are kept.
  RMDir /r "$INSTDIR"
SectionEnd
