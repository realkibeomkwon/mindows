; Build: dotnet publish -c Release -r win-x64 --self-contained -o publish -p:UiAccess=true
;        iscc installer\Mindows.iss
#define AppExe "Mindows.exe"
#define AppVersion GetVersionNumbersString("..\publish\" + AppExe)

[Setup]
; Never change AppId: upgrades find the existing install by it.
AppId={{B2942517-9E3F-4699-89A8-47348B8E3006}
AppName=Mindows
AppVersion={#AppVersion}
AppPublisher=Kibeom Kwon
; uiAccess only works from a secure location, so this installs to Program Files and needs admin.
PrivilegesRequired=admin
DefaultDirName={autopf}\Mindows
DisableProgramGroupPage=yes
; Outside Program Files uiAccess would not start.
DisableDirPage=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; PrepareToInstall stops Mindows itself; Restart Manager would only prompt.
CloseApplications=no
UninstallDisplayIcon={app}\{#AppExe}
SetupIconFile=..\Mindows.ico
OutputBaseFilename=MindowsSetup-{#AppVersion}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern

[Languages]
Name: "korean"; MessagesFile: "compiler:Languages\Korean.isl"

[Files]
Source: "..\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
; Pretendard is embedded in Mindows.exe; its SIL OFL text travels with it.
Source: "..\Fonts\LICENSE.txt"; DestDir: "{app}\licenses"; DestName: "Pretendard-LICENSE.txt"; Flags: ignoreversion
; Public part only. uiAccess refuses to start unless the signing certificate is in the machine's trusted roots.
Source: "MindowsSigning.cer"; DestDir: "{tmp}"; Flags: deleteafterinstall

[Icons]
Name: "{autoprograms}\Mindows"; Filename: "{app}\{#AppExe}"
; The taskbar stays hidden if Mindows is killed; Start still opens with the Win key.
Name: "{autoprograms}\Mindows 작업 표시줄 복원"; Filename: "{app}\{#AppExe}"; Parameters: "--restore"

[Run]
; Must precede the launch below: the signed exe fails to start until the certificate is trusted.
Filename: "{sys}\certutil.exe"; Parameters: "-addstore -f Root ""{tmp}\MindowsSigning.cer"""; Flags: runhidden waituntilterminated
Filename: "{app}\{#AppExe}"; Description: "Mindows 실행"; Flags: postinstall nowait skipifsilent shellexec runasoriginaluser

[UninstallRun]
Filename: "{sys}\taskkill.exe"; Parameters: "/IM {#AppExe} /F"; Flags: runhidden; RunOnceId: "Kill"
Filename: "{app}\{#AppExe}"; Parameters: "--restore"; Flags: runhidden shellexec waituntilterminated; RunOnceId: "Restore"
Filename: "{sys}\schtasks.exe"; Parameters: "/Delete /TN Mindows /F"; Flags: runhidden; RunOnceId: "LogonTask"

[Code]
// Upgrades replace a running Mindows: stop it, then let the old build put the taskbar back
// in case the user skips the relaunch or the install is silent.
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Exe, OldUninstaller, OldKey: String;
  Code, I: Integer;
begin
  Result := '';
  // Builds before the Program Files move installed per user. An admin install only sees HKLM, so remove that copy here;
  // its uninstaller also stops Mindows, restores the taskbar and deletes the logon task.
  OldKey := 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{B2942517-9E3F-4699-89A8-47348B8E3006}_is1';
  if RegQueryStringValue(HKCU, OldKey, 'UninstallString', OldUninstaller) then
  begin
    if not FileExists(RemoveQuotes(OldUninstaller)) then
    begin
      // Stale entry of a copy that was deleted by hand; its logon task would still point at the deleted exe.
      RegDeleteKeyIncludingSubkeys(HKCU, OldKey);
      Exec(ExpandConstant('{sys}\schtasks.exe'), '/Delete /TN Mindows /F', '', SW_HIDE, ewWaitUntilTerminated, Code);
    end
    else
    begin
      Exec(RemoveQuotes(OldUninstaller), '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART', '', SW_HIDE, ewWaitUntilTerminated, Code);
      // The first-phase uninstaller can return before the copy that does the work has finished.
      for I := 1 to 300 do
      begin
        if not RegKeyExists(HKCU, OldKey) then Break;
        Sleep(100);
      end;
      if RegKeyExists(HKCU, OldKey) then
      begin
        Result := '이전 사용자별 설치를 제거하지 못했습니다. 설정의 앱 목록에서 Mindows를 먼저 제거하세요.';
        Exit;
      end;
    end;
  end;
  Exe := ExpandConstant('{app}\{#AppExe}');
  if not FileExists(Exe) then Exit;
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/IM {#AppExe} /F', '', SW_HIDE, ewWaitUntilTerminated, Code);
  // taskkill returns before the process releases its files.
  for I := 1 to 50 do
  begin
    Exec(ExpandConstant('{sys}\cmd.exe'), '/C tasklist /FI "IMAGENAME eq {#AppExe}" | find /I "{#AppExe}"', '', SW_HIDE, ewWaitUntilTerminated, Code);
    if Code <> 0 then Break;
    Sleep(100);
  end;
  // ShellExec: a uiAccess exe fails CreateProcess with 740.
  ShellExec('', Exe, '--restore', '', SW_HIDE, ewWaitUntilTerminated, Code);
end;
