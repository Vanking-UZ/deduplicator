; Installer for Deduplicator (NSIS). Self-contained build — no .NET runtime required on the client.
; Compile:
;   makensis /DPUBDIR=<publish folder> /DOUTFILE=<setup .exe> /DICON=<app.ico> /DVERSION=<version> Deduplicator.nsi

Unicode true

!ifndef VERSION
  !error "VERSION is required; use scripts/build-installer.ps1"
!endif
!ifndef PUBDIR
  !define PUBDIR "publish"
!endif
!ifndef OUTFILE
  !define OUTFILE "Deduplicator-Setup-${VERSION}.exe"
!endif

!define APPNAME "Deduplicator"
!define PUBLISHER "Deduplicator"
!define EXENAME "Deduplicator.exe"
!define REGKEY "Software\Microsoft\Windows\CurrentVersion\Uninstall\${APPNAME}"

Name "${APPNAME} ${VERSION}"
OutFile "${OUTFILE}"
InstallDir "$PROGRAMFILES64\${APPNAME}"
InstallDirRegKey HKLM "Software\${APPNAME}" "InstallDir"
RequestExecutionLevel admin
SetCompressor /SOLID lzma
ShowInstDetails show
ShowUninstDetails show

!ifdef ICON
  Icon "${ICON}"
  UninstallIcon "${ICON}"
!endif

VIProductVersion "${VERSION}.0"
VIAddVersionKey "ProductName" "${APPNAME}"
VIAddVersionKey "FileVersion" "${VERSION}"
VIAddVersionKey "CompanyName" "${PUBLISHER}"
VIAddVersionKey "FileDescription" "${APPNAME} installer"
VIAddVersionKey "LegalCopyright" "${PUBLISHER}"

Page directory
Page instfiles
UninstPage uninstConfirm
UninstPage instfiles

Section "Install"
  SetOutPath "$INSTDIR"
  ; The portable marker redirects writable data next to the executable and must never
  ; be installed under Program Files. The build script rejects it; /x is a second guard.
  File /r /x "Deduplicator.portable" "${PUBDIR}\*.*"

  ; Start Menu + Desktop shortcuts
  CreateDirectory "$SMPROGRAMS\${APPNAME}"
  CreateShortCut "$SMPROGRAMS\${APPNAME}\${APPNAME}.lnk" "$INSTDIR\${EXENAME}"
  CreateShortCut "$SMPROGRAMS\${APPNAME}\Удалить ${APPNAME}.lnk" "$INSTDIR\uninstall.exe"
  CreateShortCut "$DESKTOP\${APPNAME}.lnk" "$INSTDIR\${EXENAME}"

  ; Uninstaller + Add/Remove Programs
  WriteUninstaller "$INSTDIR\uninstall.exe"
  WriteRegStr HKLM "Software\${APPNAME}" "InstallDir" "$INSTDIR"
  WriteRegStr HKLM "${REGKEY}" "DisplayName" "${APPNAME}"
  WriteRegStr HKLM "${REGKEY}" "DisplayVersion" "${VERSION}"
  WriteRegStr HKLM "${REGKEY}" "Publisher" "${PUBLISHER}"
  WriteRegStr HKLM "${REGKEY}" "DisplayIcon" "$INSTDIR\${EXENAME}"
  WriteRegStr HKLM "${REGKEY}" "UninstallString" "$INSTDIR\uninstall.exe"
  WriteRegStr HKLM "${REGKEY}" "InstallLocation" "$INSTDIR"
  WriteRegDWORD HKLM "${REGKEY}" "NoModify" 1
  WriteRegDWORD HKLM "${REGKEY}" "NoRepair" 1
SectionEnd

Section "Uninstall"
  Delete "$SMPROGRAMS\${APPNAME}\${APPNAME}.lnk"
  Delete "$SMPROGRAMS\${APPNAME}\Удалить ${APPNAME}.lnk"
  RMDir  "$SMPROGRAMS\${APPNAME}"
  Delete "$DESKTOP\${APPNAME}.lnk"

  ; Remove installed files
  RMDir /r "$INSTDIR"

  DeleteRegKey HKLM "${REGKEY}"
  DeleteRegKey HKLM "Software\${APPNAME}"
SectionEnd
