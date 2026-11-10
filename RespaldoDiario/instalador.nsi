Unicode true
!include "MUI2.nsh"
!include "x64.nsh"

Name "Respaldo Diario"
OutFile "RespaldoDiario-Instalador.exe"
InstallDir "$PROGRAMFILES64\RespaldoDiario"
RequestExecutionLevel admin
SetCompressor /SOLID lzma
VIProductVersion "1.0.0.0"
VIAddVersionKey "ProductName" "Respaldo Diario"
VIAddVersionKey "FileDescription" "Instalador de Respaldo Diario"
VIAddVersionKey "FileVersion" "1.0.0.0"
VIAddVersionKey "ProductVersion" "1.0.0.0"
VIAddVersionKey "LegalCopyright" ""

!define MUI_ABORTWARNING
!define MUI_FINISHPAGE_RUN "$INSTDIR\RespaldoDiario.exe"
!define MUI_FINISHPAGE_RUN_TEXT "Abrir Respaldo Diario para configurarlo"

!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_LANGUAGE "Spanish"

Section "Instalar"
  SetOutPath "$INSTDIR"
  File "RespaldoDiario.exe"
  WriteUninstaller "$INSTDIR\Desinstalar.exe"

  CreateDirectory "$SMPROGRAMS\Respaldo Diario"
  CreateShortcut "$SMPROGRAMS\Respaldo Diario\Respaldo Diario.lnk" "$INSTDIR\RespaldoDiario.exe"
  CreateShortcut "$SMPROGRAMS\Respaldo Diario\Desinstalar.lnk" "$INSTDIR\Desinstalar.exe"
  CreateShortcut "$DESKTOP\Respaldo Diario.lnk" "$INSTDIR\RespaldoDiario.exe"

  WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\RespaldoDiario" "DisplayName" "Respaldo Diario"
  WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\RespaldoDiario" "DisplayVersion" "1.0.0"
  WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\RespaldoDiario" "DisplayIcon" "$INSTDIR\RespaldoDiario.exe"
  WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\RespaldoDiario" "UninstallString" '"$INSTDIR\Desinstalar.exe"'
  WriteRegDWORD HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\RespaldoDiario" "NoModify" 1
  WriteRegDWORD HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\RespaldoDiario" "NoRepair" 1
SectionEnd

Section "Uninstall"
  nsExec::Exec 'schtasks.exe /delete /f /tn RespaldoDiario'
  Delete "$INSTDIR\RespaldoDiario.exe"
  Delete "$INSTDIR\Desinstalar.exe"
  RMDir "$INSTDIR"
  Delete "$SMPROGRAMS\Respaldo Diario\*.lnk"
  RMDir "$SMPROGRAMS\Respaldo Diario"
  Delete "$DESKTOP\Respaldo Diario.lnk"
  DeleteRegKey HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\RespaldoDiario"
  ; La configuración y el registro (C:\ProgramData\RespaldoDiario) se conservan a propósito.
SectionEnd
