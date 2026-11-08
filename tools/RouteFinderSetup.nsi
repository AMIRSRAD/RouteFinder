Unicode true
RequestExecutionLevel admin

!include "x64.nsh"
!include "WinVer.nsh"
!include "MUI2.nsh"
!include "FileFunc.nsh"
!define UninstallKey "Software\Microsoft\Windows\CurrentVersion\Uninstall\RouteFinder"
!ifdef AppDirectory
    !define InstalledApp "app\RouteFinder.exe"
    !define Payload "app"
!else
    !define InstalledApp "RouteFinder.exe"
    !define Payload "RouteFinder.exe"
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
    ${GetParameters} $R1
    ClearErrors
    ${GetOptions} $R1 "/UPDATEPID=" $R2
    ${IfNot} ${Errors}
        System::Call 'kernel32::OpenProcess(i 0x100000, i 0, i rR2) p .rR3'
        ${If} $R3 != 0
            System::Call 'kernel32::WaitForSingleObject(p rR3, i 30000) i .rR4'
            System::Call 'kernel32::CloseHandle(p rR3)'
            ${If} $R4 != 0
                MessageBox MB_ICONSTOP|MB_OK "Close RouteFinder and run the update installer again." /SD IDOK
                SetErrorLevel 1
                Abort
            ${EndIf}
        ${EndIf}
    ${EndIf}
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
    IfFileExists "$INSTDIR\${InstalledApp}" 0 stage
    System::Call 'kernel32::CreateFileW(w "$INSTDIR\${InstalledApp}", i 0x40000000, i 0, p 0, i 3, i 0, p 0) p .r0'
    ${If} $0 == -1
        MessageBox MB_ICONSTOP|MB_OK "Close RouteFinder before installing this update." /SD IDOK
        SetErrorLevel 1
        Abort
    ${EndIf}
    System::Call 'kernel32::CloseHandle(p r0)'
stage:
    CreateDirectory "$INSTDIR"
    GetTempFileName $R0 "$INSTDIR"
    IfErrors installFailed
    Delete "$R0"
    SetOutPath "$R0"
    IfErrors installFailed
    ClearErrors
    !ifdef AppDirectory
        SetOutPath "$R0\app"
        File /r /x "*.pdb" "${AppDirectory}\*"
    !else
        File /oname=RouteFinder.exe "${AppPath}"
    !endif
    IfErrors stageFailed
    SetOutPath "$INSTDIR"
    IfFileExists "$INSTDIR\${Payload}" 0 promote
    ClearErrors
    Rename "$INSTDIR\${Payload}" "$R0\previous"
    IfErrors stageFailed
promote:
    ClearErrors
    Rename "$R0\${Payload}" "$INSTDIR\${Payload}"
    IfErrors restorePrevious
    RMDir /r "$R0"
    WriteUninstaller "$INSTDIR\Uninstall.exe"
    CreateShortCut "$SMPROGRAMS\RouteFinder.lnk" "$INSTDIR\${InstalledApp}" "" "$INSTDIR\${InstalledApp}" 0
    CreateShortCut "$DESKTOP\RouteFinder.lnk" "$INSTDIR\${InstalledApp}" "" "$INSTDIR\${InstalledApp}" 0
    WriteRegStr HKLM "${UninstallKey}" "DisplayName" "RouteFinder"
    WriteRegStr HKLM "${UninstallKey}" "DisplayVersion" "${AppVersion}"
    WriteRegStr HKLM "${UninstallKey}" "InstallLocation" "$INSTDIR"
    WriteRegStr HKLM "${UninstallKey}" "DisplayIcon" "$INSTDIR\${InstalledApp}"
    WriteRegStr HKLM "${UninstallKey}" "UninstallString" '$\"$INSTDIR\Uninstall.exe$\"'
    WriteRegDWORD HKLM "${UninstallKey}" "NoModify" 1
    WriteRegDWORD HKLM "${UninstallKey}" "NoRepair" 1
    Goto installDone
restorePrevious:
    IfFileExists "$R0\previous" 0 stageFailed
    ClearErrors
    Rename "$R0\previous" "$INSTDIR\${Payload}"
    IfErrors restoreFailed
stageFailed:
    SetOutPath "$INSTDIR"
    RMDir /r "$R0"
installFailed:
    MessageBox MB_ICONSTOP|MB_OK "RouteFinder could not install the update." /SD IDOK
    SetErrorLevel 1
    Abort
restoreFailed:
    MessageBox MB_ICONSTOP|MB_OK "RouteFinder could not restore the previous installation. Its files are in $R0\previous." /SD IDOK
    SetErrorLevel 1
    Abort
installDone:
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
    Delete "$DESKTOP\RouteFinder.lnk"
    Delete "$INSTDIR\Uninstall.exe"
    DeleteRegKey HKLM "${UninstallKey}"
    RMDir "$INSTDIR"
SectionEnd
