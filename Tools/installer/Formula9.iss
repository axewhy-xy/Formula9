; Package the bundled Windows build into one Setup.exe.
;
; Author: Lau Xiang Yang
; Date Created: 27/11/2026
; Last Modified: 27/11/2026
;
; Description:
;     Inno Setup 6 script that Tools/build_installer.py compiles. It packs the folder
;     Tools/build_executable.py makes (Formula9.exe beside its Backend folder) into a single
;     installer with Start menu and optional desktop shortcuts and an uninstaller.
;
;     The bundled backend writes its log (Backend/logs) and sessions (Backend/state) inside the
;     install folder, so by default Formula9 installs for the current user only, into
;     %LOCALAPPDATA%\Programs\Formula9, and needs no administrator rights. Choosing "all users"
;     installs into Program Files instead and lets every user write to the Backend folder.
;
;     An update keeps Backend/state (the saved sessions) and replaces everything else, so files
;     an older build had are not left behind. Uninstalling removes the whole folder, sessions too.
;     Setup and the uninstaller both wait for Formula9 and its backend to close first, since files
;     a running program holds cannot be replaced or deleted.
;
;         ISCC.exe /DBuildDir=<build folder> /DOutputDir=<folder> /DAppVersion=1.0 Formula9.iss
;
;     Each /D value has a default below, so the script also compiles from the Inno Setup IDE
;     after Tools/build_executable.py has made Builds/Formula9.

#ifndef BuildDir
  #define BuildDir "..\..\Builds\Formula9"
#endif
#ifndef OutputDir
  #define OutputDir "..\..\Builds\Installer"
#endif
#ifndef AppVersion
  #define AppVersion "1.0"
#endif
#ifndef Publisher
  #define Publisher "Formula9"
#endif

#define AppName "Formula9"
#define AppExe "Formula9.exe"

#if !FileExists(AddBackslash(BuildDir) + AppExe)
  #error The build folder has no Formula9.exe. Run build.bat first, or pass /DBuildDir=<build folder>.
#endif
#if !FileExists(AddBackslash(BuildDir) + "Backend\serve.py")
  #error The build folder has no Backend\serve.py. Build it with build.bat (Tools/build_executable.py), not Unity's Build menu.
#endif

[Setup]
; The AppId identifies Formula9 to Windows across versions; never change it, or updates install
; beside the old copy instead of over it.
AppId={{E6B921AC-B5FC-4D8A-9A7F-22210E7B0161}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#Publisher}
VersionInfoVersion={#AppVersion}
VersionInfoProductName={#AppName}
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
; Per-user by default (the backend writes into its own folder); the dialog offers all users too.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
OutputDir={#OutputDir}
OutputBaseFilename={#AppName}-Setup-{#AppVersion}
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
; Most of the bundle is Python and Unity data that compresses well; this keeps Setup.exe small.
Compression=lzma2/ultra64
SolidCompression=yes
LZMAUseSeparateProcess=yes
WizardStyle=modern
; Our own check below waits for Formula9 to close; the Restart Manager would kill the backend
; outright and skip its clean shutdown.
CloseApplications=no
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[InstallDelete]
; An update replaces the previous build, except Backend\state (sessions) and Backend\logs.
Type: filesandordirs; Name: "{app}\Formula9_Data"
Type: filesandordirs; Name: "{app}\MonoBleedingEdge"
Type: filesandordirs; Name: "{app}\D3D12"
Type: filesandordirs; Name: "{app}\Backend\python"
Type: filesandordirs; Name: "{app}\Backend\backend"
Type: filesandordirs; Name: "{app}\Backend\contracts"
Type: filesandordirs; Name: "{app}\Backend\__pycache__"

[Dirs]
; Every user may write to the Backend folder (its log, sessions and SQLite journals). This only
; matters for an all-users install into Program Files; a per-user folder is already the user's.
Name: "{app}\Backend"; Permissions: users-modify
Name: "{app}\Backend\state"; Permissions: users-modify
Name: "{app}\Backend\logs"; Permissions: users-modify

[Files]
; Backend\state and Backend\logs are made at run time, so a build that has already run does not
; ship one machine's sessions or log to another.
Source: "{#BuildDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; \
    Excludes: "\.formula9-build,\Backend\state,\Backend\logs,*_DoNotShip"

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"; WorkingDir: "{app}"
Name: "{autoprograms}\Uninstall {#AppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; \
    Flags: nowait postinstall skipifsilent

[UninstallDelete]
; What the backend made at run time: its sessions, log and Python caches.
Type: filesandordirs; Name: "{app}\Backend"
Type: dirifempty; Name: "{app}"

[Code]
// -----------------------------------------------------------------------------------
// Function: RunningFrom
// -----------------------------------------------------------------------------------
// Description:
//     This function names the programs running from Folder, other than the uninstaller:
//     Formula9.exe, and the bundled backend's python.exe, which keeps running for a few seconds
//     after the last window closes.
//
// Args:
//     Folder (String): The install folder.
//
// Returns:
//     String: One "name (process ID)" per line, or '' if nothing runs from the folder or the
//         processes could not be listed.
function RunningFrom(Folder: String): String;
var
  Locator, Service, Processes, Process: Variant;
  Prefix, Path, Name: String;
  Index, Id: Integer;
begin
  Result := '';
  Prefix := Lowercase(AddBackslash(Folder));
  try
    Locator := CreateOleObject('WbemScripting.SWbemLocator');
    Service := Locator.ConnectServer('.', 'root\CIMV2');
    Processes := Service.ExecQuery('SELECT Name, ProcessId, ExecutablePath FROM Win32_Process');
    for Index := 0 to Processes.Count - 1 do
    begin
      Process := Processes.ItemIndex(Index);
      if not VarIsNull(Process.ExecutablePath) then
      begin
        Path := Process.ExecutablePath;
        Name := Process.Name;
        // The uninstaller relaunches itself from a temporary copy and waits for it, so its
        // own unins000.exe is still running from the folder; it is not Formula9.
        if (Copy(Lowercase(Path), 1, Length(Prefix)) = Prefix) and
           not WildcardMatch(Lowercase(Name), 'unins*.exe') then
        begin
          Id := Process.ProcessId;
          Result := Result + Name + ' (process ' + IntToStr(Id) + ')' + #13#10;
        end;
      end;
    end;
  except
    // Without WMI, Setup still fails on a file in use with its own retry prompt.
    Result := '';
  end;
end;

// -----------------------------------------------------------------------------------
// Function: WaitUntilClosed
// -----------------------------------------------------------------------------------
// Description:
//     This function waits until nothing runs from Folder. While something does, it asks the user
//     to close Formula9, giving the backend time for its clean shutdown, and retries.
//
// Args:
//     Folder (String): The install folder.
//     Action (String): What is waiting, for the message ("installing", "uninstalling").
//
// Returns:
//     Boolean: True once nothing runs from Folder; False if the user cancelled.
function WaitUntilClosed(Folder: String; Action: String): Boolean;
var
  Running: String;
begin
  Result := True;
  if not DirExists(Folder) then Exit;
  Running := RunningFrom(Folder);
  while Running <> '' do
  begin
    if SuppressibleMsgBox('Formula9 is still running from ' + Folder + ':' + #13#10#13#10 + Running + #13#10 +
                          'Close every Formula9 window, wait a few seconds for its backend to stop, then click ' +
                          'Retry to continue ' + Action + '.', mbError, MB_RETRYCANCEL, IDCANCEL) = IDCANCEL then
    begin
      Result := False;
      Exit;
    end;
    Running := RunningFrom(Folder);
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  if not WaitUntilClosed(ExpandConstant('{app}'), 'installing') then
    Result := 'Formula9 is still running. Close it and run Setup again.';
end;

function InitializeUninstall(): Boolean;
begin
  Result := WaitUntilClosed(ExpandConstant('{app}'), 'uninstalling');
end;
