; Установщик Shadepane (Inno Setup 6). Собирается в CI:
;   ISCC.exe /DAppVersion=0.2.0 /DSourceDir=<папка dotnet publish> installer\Shadepane.iss
; По умолчанию — установка для текущего пользователя без прав администратора (%LOCALAPPDATA%\Programs\Shadepane),
; поэтому автообновление не требует UAC. Администратор может выбрать установку для всех (Program Files).

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\publish\Shadepane"
#endif

[Setup]
; AppId не менять: по нему установщик находит предыдущую версию при обновлении.
AppId={{8F4C2E71-5B3A-4D6E-9C1F-2A7B8D9E0F13}
AppName=Shadepane
AppVersion={#AppVersion}
AppVerName=Shadepane {#AppVersion}
AppPublisher=aintlikeu
AppPublisherURL=https://github.com/aintlikeu/shadepane
AppSupportURL=https://github.com/aintlikeu/shadepane/issues
AppUpdatesURL=https://github.com/aintlikeu/shadepane/releases
DefaultDirName={autopf}\Shadepane
DefaultGroupName=Shadepane
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog commandline
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; WDA_EXCLUDEFROMCAPTURE полностью работает с Windows 10 2004 (сборка 19041).
MinVersion=10.0.19041
OutputDir=..\dist
OutputBaseFilename=Shadepane-{#AppVersion}-win-x64-setup
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
LicenseFile=..\LICENSE
UninstallDisplayIcon={app}\Shadepane.exe
UninstallDisplayName=Shadepane
; Закрыть запущенный браузер перед заменой файлов (обновление).
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\Shadepane"; Filename: "{app}\Shadepane.exe"
Name: "{autodesktop}\Shadepane"; Filename: "{app}\Shadepane.exe"; Tasks: desktopicon

[Run]
; Обычная установка — флажок «Запустить» на последней странице.
Filename: "{app}\Shadepane.exe"; Description: "{cm:LaunchProgram,Shadepane}"; Flags: nowait postinstall skipifsilent
; Тихая установка (автообновление из браузера) — сразу запустить обновлённый браузер.
Filename: "{app}\Shadepane.exe"; Flags: nowait skipifnotsilent

[Code]
const
  WebView2ClientKey = 'Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}';

function HasVersion(RootKey: Integer; SubKey: String): Boolean;
var
  Version: String;
begin
  Result := RegQueryStringValue(RootKey, SubKey, 'pv', Version) and (Version <> '') and (Version <> '0.0.0.0');
end;

{ WebView2 Runtime: установлен для всех (HKLM, 32-битная ветка) или для пользователя (HKCU). }
function WebView2Installed(): Boolean;
begin
  Result := HasVersion(HKLM, 'SOFTWARE\WOW6432Node\' + WebView2ClientKey) or
            HasVersion(HKLM, 'SOFTWARE\' + WebView2ClientKey) or
            HasVersion(HKCU, 'Software\' + WebView2ClientKey);
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  ErrorCode: Integer;
begin
  if (CurStep = ssPostInstall) and (not WizardSilent()) and (not WebView2Installed()) then
  begin
    if MsgBox('Для работы Shadepane нужен Microsoft WebView2 Runtime, он не найден.' + #13#10 +
              'В Windows 11 он обычно уже установлен. Открыть страницу загрузки?',
              mbConfirmation, MB_YESNO) = IDYES then
      ShellExec('open', 'https://developer.microsoft.com/microsoft-edge/webview2/', '', '', SW_SHOWNORMAL, ewNoWait, ErrorCode);
  end;
end;
