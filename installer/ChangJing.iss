; ============================================================================
; 畅径（ChangJing）安装脚本 —— Inno Setup 6
; ----------------------------------------------------------------------------
; 用法：
;   ISCC.exe /DArch=x64 [/DAppVersion=1.1.0] ChangJing.iss
;   ISCC.exe /DArch=x86 [/DAppVersion=1.1.0] ChangJing.iss
;
; 两个架构共用同一 AppId，因此可互相覆盖升级。
; 依赖同目录下的 ChineseSimplified.isl（官方简体中文语言文件）。
; 发布产物目录由 build-installer.ps1 生成于 .build\publish-<arch>\。
; ============================================================================

#ifndef Arch
  #define Arch "x64"
#endif
#ifndef AppVersion
  #define AppVersion "1.1.0"
#endif
#ifndef OutputDir
  #define OutputDir "..\deliver\" + AppVersion
#endif
#ifndef PublishDir
  #define PublishDir ".build\publish-" + Arch
#endif

#if Arch == "x64"
  #define ARCH_IS_X64
#elif Arch == "x86"
  #define ARCH_IS_X86
#else
  #error Arch 只能是 x64 或 x86
#endif

[Setup]
AppId={{e3952796-0245-4e68-9efd-4be5f30a97e8}
AppName=畅径
AppVersion={#AppVersion}
VersionInfoVersion={#AppVersion}.0
AppPublisher=鱼塘泛舟-SQ
AppPublisherURL=https://github.com/GitHubMacrro/changjing-file-manager
AppSupportURL=https://github.com/GitHubMacrro/changjing-file-manager
AppCopyright=Copyright (c) 2026 鱼塘泛舟-SQ (GitHubMacrro)
DefaultDirName={autopf}\ChangJing
UsePreviousAppDir=yes
DisableDirPage=auto
DisableWelcomePage=no
LicenseFile=..\LICENSE
WizardStyle=modern
SetupLogging=yes
SetupIconFile=..\ChangJing.App\app.ico
UninstallDisplayIcon={app}\ChangJing.exe
Compression=lzma2/ultra64
SolidCompression=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
MinVersion=10.0.17763
AppMutex=ChangJing.App.Running
CloseApplications=yes
OutputDir={#OutputDir}
OutputBaseFilename=ChangJing-Setup-{#AppVersion}-{#Arch}

#ifdef ARCH_IS_X64
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
#endif
#ifdef ARCH_IS_X86
; 32 位版本：允许在 32 位与 64 位 Windows 上安装（以 32 位模式安装）
ArchitecturesAllowed=x86compatible
#endif

[Languages]
Name: "chinesesimplified"; MessagesFile: "ChineseSimplified.isl"

[Messages]
; 系统版本过旧时的提示（MinVersion=10.0.17763 触发）
chinesesimplified.WinVersionTooLowError=畅径需要 Windows 10 或更高版本
; 欢迎页文案：说明用途与本地运行、不联网、不收集数据
chinesesimplified.WelcomeLabel2=此向导将引导您在计算机上安装 [name/ver]。%n%n畅径用于处理资源管理器打不开、删不掉的长路径文件。%n%n畅径完全本地运行，不联网、不收集数据。

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "附加快捷方式："
Name: "contextmenu"; Description: "添加右键菜单“用畅径打开”"; GroupDescription: "附加快捷方式："

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "使用说明.html"; DestDir: "{app}"; Flags: ignoreversion
; 许可原文：MIT 正文改名 LICENSE.txt 装入安装目录，第三方许可装进 licenses\ 子目录
Source: "..\LICENSE"; DestDir: "{app}"; DestName: "LICENSE.txt"; Flags: ignoreversion
Source: "..\licenses\*"; DestDir: "{app}\licenses"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\ChangJing\畅径"; Filename: "{app}\ChangJing.exe"; WorkingDir: "{app}"
Name: "{autoprograms}\ChangJing\使用手册"; Filename: "{app}\使用说明.html"
Name: "{autoprograms}\ChangJing\卸载畅径"; Filename: "{uninstallexe}"
Name: "{autodesktop}\ChangJing"; Filename: "{app}\ChangJing.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Registry]
; 目录右键菜单
Root: HKA; Subkey: "Software\Classes\Directory\shell\ChangJing"; ValueType: string; ValueName: ""; ValueData: "用畅径打开"; Flags: uninsdeletekey; Tasks: contextmenu
Root: HKA; Subkey: "Software\Classes\Directory\shell\ChangJing"; ValueType: string; ValueName: "Icon"; ValueData: "{app}\ChangJing.exe,0"; Tasks: contextmenu
Root: HKA; Subkey: "Software\Classes\Directory\shell\ChangJing\command"; ValueType: string; ValueName: ""; ValueData: """{app}\ChangJing.exe"" ""%1"""; Flags: uninsdeletekey; Tasks: contextmenu
; 目录空白处右键菜单
Root: HKA; Subkey: "Software\Classes\Directory\Background\shell\ChangJing"; ValueType: string; ValueName: ""; ValueData: "用畅径打开"; Flags: uninsdeletekey; Tasks: contextmenu
Root: HKA; Subkey: "Software\Classes\Directory\Background\shell\ChangJing"; ValueType: string; ValueName: "Icon"; ValueData: "{app}\ChangJing.exe,0"; Tasks: contextmenu
Root: HKA; Subkey: "Software\Classes\Directory\Background\shell\ChangJing\command"; ValueType: string; ValueName: ""; ValueData: """{app}\ChangJing.exe"" ""%V"""; Flags: uninsdeletekey; Tasks: contextmenu
; 驱动器右键菜单
Root: HKA; Subkey: "Software\Classes\Drive\shell\ChangJing"; ValueType: string; ValueName: ""; ValueData: "用畅径打开"; Flags: uninsdeletekey; Tasks: contextmenu
Root: HKA; Subkey: "Software\Classes\Drive\shell\ChangJing"; ValueType: string; ValueName: "Icon"; ValueData: "{app}\ChangJing.exe,0"; Tasks: contextmenu
Root: HKA; Subkey: "Software\Classes\Drive\shell\ChangJing\command"; ValueType: string; ValueName: ""; ValueData: """{app}\ChangJing.exe"" ""%1"""; Flags: uninsdeletekey; Tasks: contextmenu
; 文件右键菜单（所有类型）
Root: HKA; Subkey: "Software\Classes\*\shell\ChangJing"; ValueType: string; ValueName: ""; ValueData: "用畅径打开"; Flags: uninsdeletekey; Tasks: contextmenu
Root: HKA; Subkey: "Software\Classes\*\shell\ChangJing"; ValueType: string; ValueName: "Icon"; ValueData: "{app}\ChangJing.exe,0"; Tasks: contextmenu
Root: HKA; Subkey: "Software\Classes\*\shell\ChangJing\command"; ValueType: string; ValueName: ""; ValueData: """{app}\ChangJing.exe"" ""%1"""; Flags: uninsdeletekey; Tasks: contextmenu

[Run]
Filename: "{app}\ChangJing.exe"; Description: "立即运行畅径"; Flags: nowait postinstall skipifsilent
Filename: "{app}\使用说明.html"; Description: "查看使用手册"; Flags: postinstall shellexec unchecked skipifsilent

[Code]
const
  // GetDriveType 返回值
  DRIVE_FIXED = 3;

  // 程序回收站结构：<卷>\$ChangJing.Recycle\<用户SID>\files\<ID>
  // files 下的每个 <ID>（文件或文件夹）对应「一个已删除项目」
  RECYCLE_DIR_NAME = '$ChangJing.Recycle';

function GetDriveTypeW(lpRootPathName: string): Cardinal;
  external 'GetDriveTypeW@kernel32.dll stdcall';

// 统计某个目录下的直接条目数（不含 . 与 ..），用于数「已删除项目」
function CountEntries(const Dir: string): Integer;
var
  FindRec: TFindRec;
begin
  Result := 0;
  if not FindFirst(AddBackslash(Dir) + '*', FindRec) then
    exit;
  try
    repeat
      if (FindRec.Name <> '.') and (FindRec.Name <> '..') then
        Result := Result + 1;
    until not FindNext(FindRec);
  finally
    FindClose(FindRec);
  end;
end;

// 统计某个卷上畅径回收站里「已删除项目」的数量
// 只数 <SID>\files\ 这一层，不递归进条目内部，避免超长路径与海量小文件拖慢卸载
function CountRecycleItems(const VolumeRoot: string): Integer;
var
  BaseDir, SidDir, FilesDir: string;
  FindRec: TFindRec;
begin
  Result := 0;

  BaseDir := AddBackslash(VolumeRoot) + RECYCLE_DIR_NAME;
  if not DirExists(BaseDir) then
    exit;

  if not FindFirst(AddBackslash(BaseDir) + '*', FindRec) then
    exit;
  try
    repeat
      if (FindRec.Name <> '.') and (FindRec.Name <> '..')
         and ((FindRec.Attributes and FILE_ATTRIBUTE_DIRECTORY) <> 0) then
      begin
        SidDir := AddBackslash(BaseDir) + FindRec.Name;
        FilesDir := AddBackslash(SidDir) + 'files';
        if DirExists(FilesDir) then
          Result := Result + CountEntries(FilesDir);
      end;
    until not FindNext(FindRec);
  finally
    FindClose(FindRec);
  end;
end;

function InitializeSetup(): Boolean;
begin
  Result := True;

#ifdef ARCH_IS_X86
  // 32 位版本运行在 64 位系统时，给出信息提示（不阻止安装）
  if (not WizardSilent) and IsWin64 then
    MsgBox('检测到您的系统为 64 位 Windows。' + #13#10 +
           '建议使用 64 位版本（ChangJing-Setup-{#AppVersion}-x64.exe）以获得更好的性能。' + #13#10#13#10 +
           '您也可以继续安装当前的 32 位版本。',
           mbInformation, MB_OK);
#endif
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  I, Count, Total: Integer;
  DriveRoot, List, AppDataDir: string;
begin
  if CurUninstallStep = usUninstall then
  begin
    // 1) 统计各固定磁盘上畅径回收站中的已删除项数（仅提示，绝不删除）
    //    只看固定磁盘，跳过光驱 / 可移动盘 / 断开的映射盘，避免卸载卡住
    Total := 0;
    List := '';
    for I := Ord('A') to Ord('Z') do
    begin
      DriveRoot := Chr(I) + ':\';
      if GetDriveTypeW(DriveRoot) <> DRIVE_FIXED then
        continue;

      Count := CountRecycleItems(DriveRoot);
      if Count > 0 then
      begin
        Total := Total + Count;
        if List <> '' then
          List := List + '、';
        List := List + DriveRoot;
      end;
    end;

    if (not UninstallSilent) and (Total > 0) then
      MsgBox('畅径回收站中还有 ' + IntToStr(Total) + ' 个已删除的项目，卸载后仍保留在 ' +
             List + ' 下的 ' + RECYCLE_DIR_NAME + ' 文件夹中。',
             mbInformation, MB_OK);

    // 2) 交互式卸载才询问是否删除设置与日志（默认「否」）；静默卸载一律保留
    if not UninstallSilent then
    begin
      if MsgBox('是否同时删除设置和日志？' + #13#10#13#10 +
                '选择“是”将删除设置、日志及临时数据（' + ExpandConstant('{localappdata}') + '\ChangJing）。' + #13#10 +
                '选择“否”将保留它们，重装后可继续使用。',
                mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
      begin
        AppDataDir := ExpandConstant('{localappdata}\ChangJing');
        DelTree(AppDataDir + '\logs', True, True, True);
        DelTree(AppDataDir + '\o', True, True, True);
        DelTree(AppDataDir + '\tmp', True, True, True);
        DelTree(AppDataDir + '\preview', True, True, True);
        DelTree(AppDataDir + '\elevation', True, True, True);
        DeleteFile(AppDataDir + '\settings.json');
        RemoveDir(AppDataDir);
      end;
    end;
  end;
end;
