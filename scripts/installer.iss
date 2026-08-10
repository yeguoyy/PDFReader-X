; PDFReaderX 安装包脚本（Inno Setup 6）
; 用法: 安装 Inno Setup 6 后运行 scripts\publish.ps1 dist

#define MyAppName "PDFReader X"
#define MyAppVersion "1.0.1"
#define MyAppExeName "PDFReaderX.App.exe"
#define MyAppPublisher "PDFReaderX"

[Setup]
AppId={{8F3B9E1A-5C2D-4E7F-9A6B-3D4E5F6A7B8C}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
OutputDir=..\dist\installer
OutputBaseFilename=PDFReaderX-Setup-{#MyAppVersion}
Compression=lzma2
SolidCompression=yes
ArchitecturesInstallIn64BitMode=x64compatible
SetupIconFile=..\src\PDFReaderX.App\Resources\AppLogo.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
WizardStyle=modern

[Files]
Source: "..\dist\win-x64\{#MyAppExeName}"; DestDir: "{app}"; Flags: ignoreversion

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "附加任务:"

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "立即运行 {#MyAppName}"; Flags: nowait postinstall skipifsilent


