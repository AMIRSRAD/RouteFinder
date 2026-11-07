Unicode true
RequestExecutionLevel admin

!include "x64.nsh"
!include "WinVer.nsh"
!include "MUI2.nsh"
!define UninstallKey "Software\Microsoft\Windows\CurrentVersion\Uninstall\RouteFinder"
!ifdef AppDirectory
    !define InstalledApp "app\RouteFinder.exe"
!else
    !define InstalledApp "RouteFinder.exe"
!endif

Name "RouteFinder"
Icon "..\src\RouteFinder.WinUI\Assets\RouteFinder.ico"
UninstallIcon "..\src\RouteFinder.WinUI\Assets\RouteFinder.ico"
OutFile "${SetupPath}"
InstallDir "$PROGRAMFILES64\RouteFinder"
!define MUI_ICON "..\src\RouteFinder.WinUI\Assets\RouteFinder.ico"
!define MUI_UNICON "..\src\RouteFinder.WinUI\Assets\RouteFinder.ico"
!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_LANGUAGE "English"

Function .onInit
    ${IfNot} ${IsNativeAMD64}
    ${OrIfNot} ${AtLeastWin10}
    ${OrIfNot} ${AtLeastBuild} 17763
        MessageBox MB_ICONSTOP|MB_OK "RouteFinder requires Windows 10 version 1809 or later on x64." /SD IDOK
        Abort
    ${EndIf}
    SetRegView 64
    SetShellVarContext all
    ${If} $INSTDIR == "$PROGRAMFILES64\RouteFinder"
        ReadRegStr $0 HKLM "${UninstallKey}" "InstallLocation"
        ${If} $0 != ""
            StrCpy $INSTDIR $0
        ${EndIf}
    ${EndIf}
FunctionEnd

Section "RouteFinder"
    ClearErrors
    !ifdef AppDirectory
        SetOutPath "$INSTDIR\app"
        File /r /x "*.pdb" "${AppDirectory}\*"
    !else
        SetOutPath "$INSTDIR"
        File /oname=RouteFinder.exe "${AppPath}"
    !endif
    ${If} ${Errors}
        SetErrorLevel 1
        Abort
    ${EndIf}
    WriteUninstaller "$INSTDIR\Uninstall.exe"
    CreateShortCut "$SMPROGRAMS\RouteFinder.lnk" "$INSTDIR\${InstalledApp}" "" "$INSTDIR\${InstalledApp}" 0
    WriteRegStr HKLM "${UninstallKey}" "DisplayName" "RouteFinder"
    WriteRegStr HKLM "${UninstallKey}" "DisplayVersion" "${AppVersion}"
    WriteRegStr HKLM "${UninstallKey}" "InstallLocation" "$INSTDIR"
    WriteRegStr HKLM "${UninstallKey}" "DisplayIcon" "$INSTDIR\${InstalledApp}"
    WriteRegStr HKLM "${UninstallKey}" "UninstallString" '$\"$INSTDIR\Uninstall.exe$\"'
    WriteRegDWORD HKLM "${UninstallKey}" "NoModify" 1
    WriteRegDWORD HKLM "${UninstallKey}" "NoRepair" 1
SectionEnd

Section "Uninstall"
    SetRegView 64
    SetShellVarContext all
    ClearErrors
    Delete "$INSTDIR\${InstalledApp}"
    ${If} ${Errors}
        MessageBox MB_ICONSTOP|MB_OK "Close RouteFinder before uninstalling." /SD IDOK
        SetErrorLevel 1
        Abort
    ${EndIf}
    !ifdef AppDirectory
        !include "${UninstallList}"
        RMDir "$INSTDIR\app"
    !endif
    Delete "$SMPROGRAMS\RouteFinder.lnk"
    Delete "$INSTDIR\Uninstall.exe"
    DeleteRegKey HKLM "${UninstallKey}"
    RMDir "$INSTDIR"
SectionEnd
