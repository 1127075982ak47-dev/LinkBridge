Unicode True
!include "MUI2.nsh"
!include "LogicLib.nsh"
!include "x64.nsh"
Name "LinkBridge 联桥"
OutFile "${OUTPUT}"
InstallDir "$LOCALAPPDATA\Programs\LinkBridge"
RequestExecutionLevel user
SetCompressor /SOLID lzma
VIProductVersion "${VERSION}.0"
VIAddVersionKey /LANG=2052 "ProductName" "LinkBridge 联桥"
VIAddVersionKey /LANG=2052 "FileDescription" "LinkBridge 联桥公测安装包"
VIAddVersionKey /LANG=2052 "FileVersion" "${LABEL}"
VIAddVersionKey /LANG=2052 "LegalCopyright" "2026 LinkBridge contributors"
Icon "${PAYLOAD}\LinkBridge.ico"
UninstallIcon "${PAYLOAD}\LinkBridge.ico"
!define MUI_ABORTWARNING
!define MUI_WELCOMEPAGE_TITLE "安装 LinkBridge 联桥"
!define MUI_WELCOMEPAGE_TEXT "局域网内连接 Windows 与 Mac，共用键鼠并交换文件。$\r$\n$\r$\n本包自带 .NET 运行时，不需要激活码。本轮测试双机键鼠、布局、剪贴板和文件。$\r$\n$\r$\n安装完成后请从开始菜单打开首次配对助手。音视频、窗口跨屏及高级多机不在本轮范围。$\r$\n$\r$\n公测版本 ${LABEL} · 交付批次 ${DELIVERY}"
!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_LICENSE "${PAYLOAD}\LICENSE"
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!define MUI_FINISHPAGE_TITLE "LinkBridge 已安装"
!define MUI_FINISHPAGE_TEXT "从开始菜单的 LinkBridge 文件夹打开首次配对助手和用户说明书。$\r$\n$\r$\n已有的配对设置会保留；已有指向其他版本的桌面快捷方式也会保留。$\r$\n$\r$\n首次使用需要配置两端，并由 Mac 使用者完成系统权限审核。"
!insertmacro MUI_PAGE_FINISH
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_UNPAGE_FINISH
!insertmacro MUI_LANGUAGE "SimpChinese"
Function .onInit
  ${IfNot} ${RunningX64}
    MessageBox MB_OK|MB_ICONSTOP "此安装包需要 64 位 Windows。"
    Abort
  ${EndIf}
FunctionEnd
Section "LinkBridge"
  InitPluginsDir
  SetOutPath "$PLUGINSDIR"
  File "${OPERATIONS}"
  nsExec::ExecToStack '"$SYSDIR\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -ExecutionPolicy Bypass -File "$PLUGINSDIR\Installer-Operations.ps1" -Action Prepare -InstallDirectory "$INSTDIR"'
  Pop $0
  Pop $1
  ${If} $0 != 0
    IfSilent +2
    MessageBox MB_OK|MB_ICONSTOP "安装前检查失败，请退出软件或选择专用安装目录。$\r$\n$1"
    SetErrorLevel 2
    Abort
  ${EndIf}
  SetOutPath "$INSTDIR"
  File /r "${PAYLOAD}\*"
  File "${OPERATIONS}"
  WriteUninstaller "$INSTDIR\Uninstall.exe"
  nsExec::ExecToStack '"$SYSDIR\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -ExecutionPolicy Bypass -File "$INSTDIR\Installer-Operations.ps1" -Action Integrate -InstallDirectory "$INSTDIR"'
  Pop $0
  Pop $1
  ${If} $0 != 0
    IfSilent +2
    MessageBox MB_OK|MB_ICONSTOP "程序已复制，但系统入口未能建立。请查看安装目录后重新运行安装包。$\r$\n$1"
    SetErrorLevel 3
    Abort
  ${EndIf}
SectionEnd
Section "Uninstall"
  nsExec::ExecToStack '"$SYSDIR\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -ExecutionPolicy Bypass -File "$INSTDIR\Installer-Operations.ps1" -Action Remove -InstallDirectory "$INSTDIR"'
  Pop $0
  Pop $1
  ${If} $0 != 0
    IfSilent +2
    MessageBox MB_OK|MB_ICONSTOP "卸载前检查失败。$\r$\n$1"
    SetErrorLevel 4
    Abort
  ${EndIf}
  !include "${DELETE_LIST}"
  Delete "$INSTDIR\Installer-Operations.ps1"
  Delete "$INSTDIR\linkbridge-install.json"
  Delete "$INSTDIR\Uninstall.exe"
  RMDir "$INSTDIR"
SectionEnd
