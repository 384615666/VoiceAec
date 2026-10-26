; VoiceAec installer (per-user, no elevation required).
; Build:
;   ISCC.exe /DAppVersion=0.4.0 ^
;            "/DSourceDir=E:\obs-localvqe-windows\artifacts\VoiceAec-with-OBS-win-x64" ^
;            "/DOutputDir=E:\obs-localvqe-windows\artifacts\release" VoiceAec.iss

#ifndef AppVersion
  #define AppVersion "0.4.0"
#endif
#ifndef SourceDir
  #define SourceDir "E:\obs-localvqe-windows\artifacts\VoiceAec-with-OBS-win-x64"
#endif
#ifndef OutputDir
  #define OutputDir "."
#endif

#define AppName "VoiceAec"
#define AppPublisher "VoiceAec"
#define AppUrl "https://github.com/384615666/VoiceAec"

[Setup]
AppId={{8F3C1A7E-5B24-4D6B-9E2A-7C1D4B0F6A93}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}/issues
AppUpdatesURL={#AppUrl}/releases
VersionInfoVersion={#AppVersion}.0
VersionInfoCompany={#AppPublisher}
VersionInfoDescription={#AppName} 安装程序
VersionInfoProductName={#AppName}
VersionInfoProductVersion={#AppVersion}.0
DefaultDirName={localappdata}\Programs\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
UsePreviousAppDir=yes
UsePreviousLanguage=yes
UsePreviousTasks=no
OutputDir={#OutputDir}
OutputBaseFilename=VoiceAec-v{#AppVersion}-setup-x64
SetupIconFile=..\assets\VoiceAec.ico
UninstallDisplayIcon={app}\VoiceAec.exe
UninstallDisplayName={#AppName}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
WizardImageFile=installer\wizard-side-100.bmp,installer\wizard-side-125.bmp,installer\wizard-side-150.bmp,installer\wizard-side-200.bmp
WizardSmallImageFile=installer\wizard-small-100.bmp,installer\wizard-small-125.bmp,installer\wizard-small-150.bmp,installer\wizard-small-200.bmp
MinVersion=10.0
ArchitecturesAllowed=x64compatible
CloseApplications=no
RestartApplications=no
AllowNoIcons=yes
DisableWelcomePage=no

[Languages]
Name: "chinesesimplified"; MessagesFile: "languages\ChineseSimplified.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"
Name: "vbcable"; Description: "安装 VB-CABLE 虚拟声卡（必装，装完需重启）"; GroupDescription: "必需组件:"; Check: VbCableAvailable

[Files]
; Runtime, models, panel and the OBS plugin payload. A machine-specific
; voiceaec.json is never shipped: the app resolves devices on first launch.
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "voiceaec.json,*.pdb,*.log,FIXES.md,OBS-INTEGRATION.md,RELEASE-*.md"

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\VoiceAec.exe"
Name: "{group}\{cm:UninstallProgram,{#AppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\VoiceAec.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\VoiceAec.exe"; Description: "{cm:LaunchProgram,{#StringChange(AppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; Files created at runtime inside the install directory.
Type: files; Name: "{app}\voiceaec.json"
Type: files; Name: "{app}\*.log"

[Code]
var
  VbCableRan: Boolean;
  VbCableNote: TNewStaticText;

procedure InitializeWizard;
begin
  VbCableNote := TNewStaticText.Create(WizardForm);
  VbCableNote.Parent := WizardForm.TasksList.Parent;
  VbCableNote.Left := WizardForm.TasksList.Left;
  VbCableNote.Width := VbCableNote.Parent.ClientWidth - VbCableNote.Left - ScaleX(12);
  VbCableNote.Height := ScaleY(66);
  VbCableNote.Top := WizardForm.TasksList.Top + WizardForm.TasksList.Height - ScaleY(82);
  VbCableNote.WordWrap := True;
  VbCableNote.Font.Color := $00746B7E;
  VbCableNote.Caption :=
    'VB-CABLE 由 VB-Audio 免费提供（donationware，欢迎赞助）。' + #13#10 +
    '来源 www.vb-cable.com；VoiceAec 仅分发未经修改的官方安装包，' + #13#10 +
    '卸载 VoiceAec 不会移除该驱动。';
end;

function VbCableAvailable: Boolean;
begin
  Result := FileExists('{#SourceDir}\vendor\VBCABLE\VBCABLE_Setup_x64.exe');
end;

function NeedRestart(): Boolean;
begin
  Result := VbCableRan;
end;

procedure CurPageChanged(CurPageID: Integer);
begin
  if CurPageID = wpFinished then
  begin
    if VbCableRan then
      WizardForm.FinishedLabel.Caption :=
        'VoiceAec 已安装完成。' + #13#10#13#10 +
        '虚拟声卡已安装，请重启电脑；重启后才能在外放的同时消除回声，并在语音软件里选中 CABLE Output。' + #13#10#13#10 +
        '卸载：开始菜单 → VoiceAec → 卸载 VoiceAec，或 Windows「设置 → 应用」。'
    else
      WizardForm.FinishedLabel.Caption :=
        'VoiceAec 已安装完成。' + #13#10#13#10 +
        '未安装虚拟声卡也能启动，但语音软件里选不到 CABLE Output；可稍后在面板顶部点「一键安装虚拟声卡」。' + #13#10#13#10 +
        '卸载：开始菜单 → VoiceAec → 卸载 VoiceAec，或 Windows「设置 → 应用」。';
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  ResultCode: Integer;
begin
  if (CurStep = ssPostInstall) and WizardIsTaskSelected('vbcable') and VbCableAvailable then
  begin
    // Run VB-Audio's own, unmodified setup elevated. "-i -h" installs silently;
    // SW_SHOWNORMAL keeps the official window usable if a switch is rejected.
    if ShellExec('runas', ExpandConstant('{app}\vendor\VBCABLE\VBCABLE_Setup_x64.exe'),
      '-i -h', ExpandConstant('{app}\vendor\VBCABLE'), SW_SHOWNORMAL,
      ewWaitUntilTerminated, ResultCode) then
    begin
      VbCableRan := True;
      if ResultCode <> 0 then
        MsgBox('VB-CABLE 安装程序返回代码 ' + IntToStr(ResultCode) +
          '。可以稍后在 VoiceAec 面板点「一键安装虚拟声卡」重试。', mbInformation, MB_OK);
    end
    else
      MsgBox('未能启动 VB-CABLE 安装程序（可能取消了管理员授权）。' +
        '可以稍后在 VoiceAec 面板点「一键安装虚拟声卡」重试。', mbInformation, MB_OK);
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
begin
  // An update must not fail on a running instance; closing it is safe because
  // all settings are flushed when they change and audio is stateless.
  Exec(ExpandConstant('{cmd}'), '/c taskkill /IM VoiceAec.exe /T /F >nul 2>&1', '',
    SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Result := '';
end;
