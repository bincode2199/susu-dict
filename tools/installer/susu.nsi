; Su-Su per-user installer (F18.1). Compiled by tools/build-installer.ps1; see docs/development/BUILD.md and docs/evidence/F18/F18.md.
;
; makensis defines (all passed by the build script):
;   /DVERSION=<x.y.z>     product version shown in Apps & features
;   /DSTAGE=<dir>         the staged application folder (susu.exe, native DLLs, ui, assets, LICENSES) with staged-manifest.json
;   /DOUTFILE=<path>      installer to write
;   /DUNSIGNED            build is not code-signed: marks the installer's name text, never hidden
;
; Behaviour (TEST-PLAN UPD07; ARCHITECTURE install section):
;   - per user, no administrator rights, nothing outside the user's profile and HKCU
;   - optional "start when I sign in" (unchecked by default)
;   - missing WebView2 Runtime: clear message and exit code 3; the installer never downloads anything itself
;   - a running Su-Su is asked to exit through its own shutdown path (susu.exe --installer-exit); the tasks in flight are shown first; never killed
;   - uninstall removes files, shortcuts, the start-at-login entry and the plugin sandbox (AppContainer) profiles; the user's data is kept
;     unless the user answers Yes to the explicit question (default No)
;   - exit codes: 0 ok, 1 user cancelled, 3 WebView2 missing, 4 tasks in flight in a silent run, 5 running Su-Su did not exit, 6 another installer runs

Unicode true
SetCompressor /SOLID lzma
RequestExecutionLevel user
ManifestSupportedOS Win10
ShowInstDetails nevershow
ShowUninstDetails nevershow

!ifndef VERSION
  !error "pass /DVERSION=<x.y.z>"
!endif
!ifndef STAGE
  !error "pass /DSTAGE=<staged folder>"
!endif
!ifndef OUTFILE
  !define OUTFILE "Su-Su-Setup.exe"
!endif

!define APP "Su-Su"
!define UNINSTKEY "Software\Microsoft\Windows\CurrentVersion\Uninstall\Su-Su"
!define RUNKEY "Software\Microsoft\Windows\CurrentVersion\Run"
; Microsoft's published WebView2 Evergreen Runtime client id (EdgeUpdate registry), machine-wide or per-user install.
!define WV2CLIENT "{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}"
!define WV2PAGE "https://developer.microsoft.com/microsoft-edge/webview2/"

!ifdef UNSIGNED
  Name "${APP} ${VERSION} (UNSIGNED BUILD)"
!else
  Name "${APP} ${VERSION}"
!endif
OutFile "${OUTFILE}"
InstallDir "$LOCALAPPDATA\Programs\Su-Su"
InstallDirRegKey HKCU "${UNINSTKEY}" "InstallLocation"

!include MUI2.nsh
!include LogicLib.nsh
!include FileFunc.nsh
!include WinMessages.nsh
!insertmacro GetSize

!define MUI_ICON "${STAGE}\assets\susu.ico"
!define MUI_UNICON "${STAGE}\assets\susu.ico"
!define MUI_ABORTWARNING
!define MUI_FINISHPAGE_RUN "$INSTDIR\susu.exe"
!define MUI_FINISHPAGE_RUN_TEXT "Start Su-Su"
!insertmacro MUI_PAGE_COMPONENTS
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_LANGUAGE "English"
!insertmacro MUI_LANGUAGE "SimpChinese"

LangString TEXT_WV2 ${LANG_ENGLISH} "Su-Su needs the Microsoft Edge WebView2 Runtime, which was not found.$\r$\n$\r$\nOpen the Microsoft download page in your browser now? Install the Evergreen Runtime from there, then run this installer again. Su-Su does not download it for you."
LangString TEXT_WV2 ${LANG_SIMPCHINESE} "Su-Su 需要 Microsoft Edge WebView2 运行时，但没有找到。$\r$\n$\r$\n现在在浏览器中打开微软的下载页面吗？请在该页面安装 Evergreen 运行时，然后重新运行本安装程序。Su-Su 不会替你下载。"
LangString TEXT_TASKS ${LANG_ENGLISH} "Su-Su is running and has tasks in progress. Continuing closes Su-Su and stops them (nothing is resent automatically).$\r$\n$\r$\nContinue?"
LangString TEXT_TASKS ${LANG_SIMPCHINESE} "Su-Su 正在运行且有任务在进行。继续会关闭 Su-Su 并停止这些任务（不会自动重发）。$\r$\n$\r$\n继续吗？"
LangString TEXT_NOEXIT ${LANG_ENGLISH} "Su-Su did not close in time. Close it from its tray icon (Exit) and run this again. Nothing was changed."
LangString TEXT_NOEXIT ${LANG_SIMPCHINESE} "Su-Su 没有及时关闭。请从托盘图标选择“退出”后重试。没有做任何更改。"
LangString TEXT_DELDATA ${LANG_ENGLISH} "Also delete your Su-Su data (settings, saved keys, history, caches)?$\r$\n$\r$\nChoose No to keep it for a later reinstall. This cannot be undone."
LangString TEXT_DELDATA ${LANG_SIMPCHINESE} "同时删除你的 Su-Su 数据（设置、已保存的密钥、历史、缓存）吗？$\r$\n$\r$\n选“否”会保留数据，以便以后重新安装。删除后无法恢复。"
LangString TEXT_CLEANFAIL ${LANG_ENGLISH} "Su-Su could not remove all of its settings or sandbox profiles. The program files will still be removed."
LangString TEXT_CLEANFAIL ${LANG_SIMPCHINESE} "Su-Su 未能清除全部设置或沙箱配置。程序文件仍会被删除。"
LangString TEXT_BUSY ${LANG_ENGLISH} "Another Su-Su installer or uninstaller is already running."
LangString TEXT_BUSY ${LANG_SIMPCHINESE} "另一个 Su-Su 安装或卸载程序正在运行。"

Var Tasks

; ---- shared steps ----

!macro CheckSingleInstaller UN
  Function ${UN}CheckSingleInstaller
    System::Call 'kernel32::CreateMutexW(p 0, i 0, w "Local\Su-Su.Setup") p .r1 ?e'
    Pop $0
    ${If} $0 = 183 ; ERROR_ALREADY_EXISTS
      MessageBox MB_OK|MB_ICONSTOP "$(TEXT_BUSY)" /SD IDOK
      SetErrorLevel 6
      Abort
    ${EndIf}
  FunctionEnd
!macroend
!insertmacro CheckSingleInstaller ""
!insertmacro CheckSingleInstaller "un."

; Asks a running Su-Su to exit through its own shutdown path. $INSTDIR holds the installed susu.exe.
!macro CloseRunning UN
  Function ${UN}CloseRunning
    IfFileExists "$INSTDIR\susu.exe" 0 done
    ExecWait '"$INSTDIR\susu.exe" --installer-query' $0
    ${If} $0 >= 100
      IntOp $Tasks $0 - 100
      ${If} $Tasks > 0
        ; a silent run never discards tasks without being told to
        MessageBox MB_OKCANCEL|MB_ICONEXCLAMATION "$(TEXT_TASKS)" /SD IDCANCEL IDOK go
        SetErrorLevel 4
        Abort
      ${EndIf}
      go:
      ExecWait '"$INSTDIR\susu.exe" --installer-exit --timeout-ms 20000' $0
      ${If} $0 <> 0
        MessageBox MB_OK|MB_ICONSTOP "$(TEXT_NOEXIT)" /SD IDOK
        SetErrorLevel 5
        Abort
      ${EndIf}
    ${EndIf}
    done:
  FunctionEnd
!macroend
!insertmacro CloseRunning ""
!insertmacro CloseRunning "un."

; ---- installer ----

Function .onInit
  Call CheckSingleInstaller
  ; WebView2 Evergreen Runtime: machine-wide (both registry views) or per-user install.
  SetRegView 64
  ReadRegStr $0 HKLM "SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\${WV2CLIENT}" "pv"
  ${If} $0 == ""
    ReadRegStr $0 HKLM "SOFTWARE\Microsoft\EdgeUpdate\Clients\${WV2CLIENT}" "pv"
  ${EndIf}
  ${If} $0 == ""
    ReadRegStr $0 HKCU "Software\Microsoft\EdgeUpdate\Clients\${WV2CLIENT}" "pv"
  ${EndIf}
  SetRegView 32
  ${If} $0 == ""
  ${OrIf} $0 == "0.0.0.0"
    MessageBox MB_YESNO|MB_ICONEXCLAMATION "$(TEXT_WV2)" /SD IDNO IDNO nowv2
    ExecShell "open" "${WV2PAGE}"
    nowv2:
    SetErrorLevel 3
    Abort
  ${EndIf}
  Call CloseRunning
FunctionEnd

Section "Su-Su" SecApp
  SectionIn RO
  SetOutPath "$INSTDIR"
  File /r "${STAGE}\*.*"
  WriteUninstaller "$INSTDIR\uninstall.exe"
  CreateShortCut "$SMPROGRAMS\Su-Su.lnk" "$INSTDIR\susu.exe" "" "$INSTDIR\susu.exe" 0
  WriteRegStr HKCU "${UNINSTKEY}" "DisplayName" "Su-Su"
  WriteRegStr HKCU "${UNINSTKEY}" "DisplayVersion" "${VERSION}"
  WriteRegStr HKCU "${UNINSTKEY}" "DisplayIcon" "$INSTDIR\susu.exe"
  WriteRegStr HKCU "${UNINSTKEY}" "InstallLocation" "$INSTDIR"
  WriteRegStr HKCU "${UNINSTKEY}" "UninstallString" '"$INSTDIR\uninstall.exe"'
  WriteRegStr HKCU "${UNINSTKEY}" "QuietUninstallString" '"$INSTDIR\uninstall.exe" /S'
  WriteRegDWORD HKCU "${UNINSTKEY}" "NoModify" 1
  WriteRegDWORD HKCU "${UNINSTKEY}" "NoRepair" 1
  ${GetSize} "$INSTDIR" "/S=0K" $0 $1 $2
  IntFmt $0 "0x%08X" $0
  WriteRegDWORD HKCU "${UNINSTKEY}" "EstimatedSize" $0
SectionEnd

Section /o "Start Su-Su when I sign in" SecAutostart
  ; The Run entry is what the app itself writes (WindowPlatform.SetLaunchAtStartup); the options file lets the first start record the same choice in settings.
  WriteRegStr HKCU "${RUNKEY}" "Su-Su" '"$INSTDIR\susu.exe" --autostart'
  CreateDirectory "$LOCALAPPDATA\Su-Su"
  FileOpen $0 "$LOCALAPPDATA\Su-Su\install-options.json" w
  FileWrite $0 '{"launchAtStartup":true}'
  FileClose $0
SectionEnd

LangString DESC_APP ${LANG_ENGLISH} "Su-Su program files (current user only; no administrator rights)."
LangString DESC_APP ${LANG_SIMPCHINESE} "Su-Su 程序文件（仅当前用户，无需管理员权限）。"
LangString DESC_AUTO ${LANG_ENGLISH} "Start Su-Su in the tray when you sign in to Windows. You can change this later in Settings."
LangString DESC_AUTO ${LANG_SIMPCHINESE} "登录 Windows 时在托盘启动 Su-Su。以后可在设置中更改。"
!insertmacro MUI_FUNCTION_DESCRIPTION_BEGIN
  !insertmacro MUI_DESCRIPTION_TEXT ${SecApp} "$(DESC_APP)"
  !insertmacro MUI_DESCRIPTION_TEXT ${SecAutostart} "$(DESC_AUTO)"
!insertmacro MUI_FUNCTION_DESCRIPTION_END

; ---- uninstaller ----

Function un.onInit
  Call un.CheckSingleInstaller
  ; $INSTDIR is the folder holding uninstall.exe. Refuse to work in any folder that is not a Su-Su install.
  IfFileExists "$INSTDIR\susu.exe" ok
  MessageBox MB_OK|MB_ICONSTOP "susu.exe was not found next to the uninstaller; nothing was removed." /SD IDOK
  Abort
  ok:
  Call un.CloseRunning
FunctionEnd

Section "Uninstall"
  ; Explicit question, default No (also in a silent run): user data is deleted only on Yes.
  StrCpy $1 ""
  MessageBox MB_YESNO|MB_ICONQUESTION|MB_DEFBUTTON2 "$(TEXT_DELDATA)" /SD IDNO IDNO keepdata
  StrCpy $1 "--delete-user-data"
  keepdata:
  ; Removes the sandbox (AppContainer) profiles and the start-at-login entry; with --delete-user-data also the settings, database and caches.
  ExecWait '"$INSTDIR\susu.exe" --installer-uninstall $1' $0
  ${If} $0 <> 0
    MessageBox MB_OK|MB_ICONEXCLAMATION "$(TEXT_CLEANFAIL)" /SD IDOK
  ${EndIf}
  DeleteRegValue HKCU "${RUNKEY}" "Su-Su" ; fallback if the helper could not run
  Delete "$LOCALAPPDATA\Su-Su\install-options.json"
  Delete "$SMPROGRAMS\Su-Su.lnk"
  DeleteRegKey HKCU "${UNINSTKEY}"
  RMDir /r "$INSTDIR"
SectionEnd
