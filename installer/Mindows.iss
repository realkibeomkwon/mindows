; Build: dotnet publish -c Release -r win-x64 --self-contained -o publish
;        iscc installer\Mindows.iss
#define AppExe "Mindows.exe"
#define AppVersion GetVersionNumbersString("..\publish\" + AppExe)

[Setup]
; Never change AppId: upgrades find the existing install by it.
AppId={{B2942517-9E3F-4699-89A8-47348B8E3006}
AppName=Mindows
AppVersion={#AppVersion}
AppPublisher=Kibeom Kwon
; Per-user: {autopf} is %LOCALAPPDATA%\Programs, and the logon task runs as the user anyway.
PrivilegesRequired=lowest
DefaultDirName={autopf}\Mindows
DisableProgramGroupPage=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; PrepareToInstall stops Mindows itself; Restart Manager would only prompt.
CloseApplications=no
UninstallDisplayIcon={app}\{#AppExe}
OutputBaseFilename=MindowsSetup-{#AppVersion}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern

[Languages]
Name: "korean"; MessagesFile: "compiler:Languages\Korean.isl"

[Files]
Source: "..\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\Mindows"; Filename: "{app}\{#AppExe}"
; The taskbar stays hidden if Mindows is killed; Start still opens with the Win key.
Name: "{autoprograms}\Mindows 작업 표시줄 복원"; Filename: "{app}\{#AppExe}"; Parameters: "--restore"

[Run]
Filename: "{app}\{#AppExe}"; Description: "Mindows 실행"; Flags: postinstall nowait skipifsilent

[UninstallRun]
Filename: "{sys}\taskkill.exe"; Parameters: "/IM {#AppExe} /F"; Flags: runhidden; RunOnceId: "Kill"
Filename: "{app}\{#AppExe}"; Parameters: "--restore"; Flags: runhidden; RunOnceId: "Restore"
Filename: "{sys}\schtasks.exe"; Parameters: "/Delete /TN Mindows /F"; Flags: runhidden; RunOnceId: "LogonTask"

[Code]
// Upgrades replace a running Mindows: stop it, then let the old build put the taskbar back
// in case the user skips the relaunch or the install is silent.
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Exe: String;
  Code, I: Integer;
begin
  Result := '';
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
  Exec(Exe, '--restore', '', SW_HIDE, ewWaitUntilTerminated, Code);
end;
