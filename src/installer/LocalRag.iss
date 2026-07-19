#ifndef AppVersion
  #define AppVersion "0.0.0-dev"
#endif
#ifndef HostSource
  #error HostSource must point to a validated self-contained host publish.
#endif
#ifndef VsixSource
  #error VsixSource must point to the packaged Local RAG VSIX.
#endif
#ifndef ModelSource
  #error ModelSource must point to the validated embedding model assets.
#endif
#ifndef InstallerOutput
  #define InstallerOutput "."
#endif

[Setup]
AppId={{BC34EB80-D1CA-483A-BF52-CC6D421A67A6}
AppName=Local RAG
AppVersion={#AppVersion}
AppPublisher=Starlinx LLC
DefaultDirName={localappdata}\Programs\LocalRag
DefaultGroupName=Local RAG
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir={#InstallerOutput}
OutputBaseFilename=LocalRag-Setup-{#AppVersion}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
CloseApplications=no
RestartApplications=no
Uninstallable=yes
UninstallDisplayName=Local RAG {#AppVersion}
UninstallDisplayIcon={app}\Host\{#AppVersion}\LocalRag.Host.exe
VersionInfoVersion={#AppVersion}

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Files]
Source: "{#HostSource}\*"; DestDir: "{app}\Host\{#AppVersion}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#VsixSource}"; DestDir: "{tmp}"; DestName: "local-rag-{#AppVersion}.vsix"; Flags: deleteafterinstall
Source: "{#ModelSource}\model.onnx"; DestDir: "{localappdata}\LocalRag\models\bge-small-en-v1.5"; Flags: ignoreversion uninsneveruninstall
Source: "{#ModelSource}\vocab.txt"; DestDir: "{localappdata}\LocalRag\models\bge-small-en-v1.5"; Flags: ignoreversion uninsneveruninstall

[UninstallDelete]
Type: files; Name: "{localappdata}\LocalRag\installation.json"

[Code]
var
  ConnectivityPage: TInputQueryWizardPage;
  CodeCommand: String;
  WeaviateEndpoint: String;

function Quote(const Value: String): String;
begin
  Result := '"' + Value + '"';
end;

function JsonEscape(Value: String): String;
begin
  StringChangeEx(Value, '\', '\\', True);
  StringChangeEx(Value, '"', '\"', True);
  Result := Value;
end;

function IsValidHost(const Value: String): Boolean;
var
  Index: Integer;
  Character: Char;
begin
  Result := Length(Value) > 0;
  for Index := 1 to Length(Value) do
  begin
    Character := Value[Index];
    if not (((Character >= 'a') and (Character <= 'z')) or
            ((Character >= 'A') and (Character <= 'Z')) or
            ((Character >= '0') and (Character <= '9')) or
            (Character = '.') or (Character = '-')) then
    begin
      Result := False;
      Exit;
    end;
  end;
end;

function TryBuildEndpoint(BaseUrl, PortText: String; var Endpoint, ErrorMessage: String): Boolean;
var
  Host: String;
  Port: Integer;
begin
  BaseUrl := Trim(BaseUrl);
  while (Length(BaseUrl) > 0) and (BaseUrl[Length(BaseUrl)] = '/') do
    Delete(BaseUrl, Length(BaseUrl), 1);

  if Pos('http://', Lowercase(BaseUrl)) = 1 then
    Host := Copy(BaseUrl, 8, Length(BaseUrl))
  else if Pos('https://', Lowercase(BaseUrl)) = 1 then
    Host := Copy(BaseUrl, 9, Length(BaseUrl))
  else
  begin
    ErrorMessage := 'Base URL must begin with http:// or https://.';
    Result := False;
    Exit;
  end;

  if not IsValidHost(Host) then
  begin
    ErrorMessage := 'Base URL must contain only a scheme and a valid host name, without a path or port.';
    Result := False;
    Exit;
  end;

  Port := StrToIntDef(Trim(PortText), 0);
  if (Port < 1) or (Port > 65535) then
  begin
    ErrorMessage := 'Port must be a number from 1 through 65535.';
    Result := False;
    Exit;
  end;

  Endpoint := BaseUrl + ':' + IntToStr(Port);
  ErrorMessage := '';
  Result := True;
end;

function FindCodeCommand(): String;
var
  Candidate: String;
  SearchPath: String;
  Separator: Integer;
begin
  Candidate := ExpandConstant('{localappdata}\Programs\Microsoft VS Code\bin\code.cmd');
  if FileExists(Candidate) then begin Result := Candidate; Exit; end;

  Candidate := ExpandConstant('{pf}\Microsoft VS Code\bin\code.cmd');
  if FileExists(Candidate) then begin Result := Candidate; Exit; end;

  Candidate := ExpandConstant('{pf32}\Microsoft VS Code\bin\code.cmd');
  if FileExists(Candidate) then begin Result := Candidate; Exit; end;

  SearchPath := GetEnv('PATH') + ';';
  while Length(SearchPath) > 0 do
  begin
    Separator := Pos(';', SearchPath);
    Candidate := Trim(Copy(SearchPath, 1, Separator - 1));
    Delete(SearchPath, 1, Separator);
    if Length(Candidate) > 0 then
    begin
      Candidate := AddBackslash(Candidate) + 'code.cmd';
      if FileExists(Candidate) then begin Result := Candidate; Exit; end;
    end;
  end;
  Result := '';
end;

procedure InitializeWizard();
begin
  ConnectivityPage := CreateInputQueryPage(
    wpSelectDir,
    'External Weaviate connection',
    'Configure the separately managed Weaviate instance.',
    'Local RAG will use this endpoint but will not install, start, stop, or configure Weaviate.');
  ConnectivityPage.Add('Base URL:', False);
  ConnectivityPage.Add('Port:', False);
  ConnectivityPage.Values[0] := ExpandConstant('{param:WEAVIATEBASEURL|http://localhost}');
  ConnectivityPage.Values[1] := ExpandConstant('{param:WEAVIATEPORT|8080}');
end;

function NextButtonClick(CurPageID: Integer): Boolean;
var
  ErrorMessage: String;
begin
  Result := True;
  if CurPageID = ConnectivityPage.ID then
  begin
    Result := TryBuildEndpoint(ConnectivityPage.Values[0], ConnectivityPage.Values[1], WeaviateEndpoint, ErrorMessage);
    if not Result then MsgBox(ErrorMessage, mbError, MB_OK);
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ErrorMessage: String;
begin
  Result := '';
  if not TryBuildEndpoint(ConnectivityPage.Values[0], ConnectivityPage.Values[1], WeaviateEndpoint, ErrorMessage) then
  begin
    Result := 'Invalid Weaviate connection: ' + ErrorMessage;
    Exit;
  end;

  CodeCommand := FindCodeCommand();
  if CodeCommand = '' then
    Result := 'Visual Studio Code was not found. Install VS Code with its command-line launcher, then run Local RAG Setup again.';
end;

procedure WriteDiscoveryFiles();
var
  DataDirectory: String;
  HostExecutable: String;
  SettingsJson: String;
  InstallationJson: String;
begin
  DataDirectory := ExpandConstant('{localappdata}\LocalRag');
  if not ForceDirectories(DataDirectory) then
    RaiseException('Unable to create Local RAG data directory: ' + DataDirectory);

  HostExecutable := ExpandConstant('{app}\Host\{#AppVersion}\LocalRag.Host.exe');
  SettingsJson := '{"schemaVersion":1,"weaviateEndpoint":"' + JsonEscape(WeaviateEndpoint) + '"}' + #13#10;
  InstallationJson := '{"schemaVersion":1,"version":"{#AppVersion}","hostExecutable":"' + JsonEscape(HostExecutable) + '"}' + #13#10;

  if not SaveStringToFile(DataDirectory + '\install-settings.json', SettingsJson, False) then
    RaiseException('Unable to write Local RAG installer settings.');
  if not SaveStringToFile(DataDirectory + '\installation.json', InstallationJson, False) then
    RaiseException('Unable to write Local RAG installation discovery.');
end;

procedure InstallVsCodeExtension();
var
  ResultCode: Integer;
  VsixPath: String;
  Parameters: String;
begin
  VsixPath := ExpandConstant('{tmp}\local-rag-{#AppVersion}.vsix');
  Parameters := '/D /S /C "' + Quote(CodeCommand) + ' --install-extension ' + Quote(VsixPath) + ' --force"';
  if not Exec(ExpandConstant('{cmd}'), Parameters, '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
    RaiseException('Unable to launch the Visual Studio Code extension installer.');
  if ResultCode <> 0 then
    RaiseException('Visual Studio Code failed to install the Local RAG extension (exit code ' + IntToStr(ResultCode) + '). Run Setup again after repairing the VS Code command-line launcher.');
end;

procedure ProbeConnectivity();
var
  ResultCode: Integer;
  Parameters: String;
begin
  { Connectivity is advisory only: the installer never owns the external Weaviate lifecycle. }
  Parameters := '-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -Command "try { Invoke-WebRequest -UseBasicParsing -TimeoutSec 3 -Uri ''' +
    WeaviateEndpoint + '/v1/.well-known/ready'' | Out-Null; exit 0 } catch { exit 1 }"';
  if (not Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'), Parameters, '', SW_HIDE, ewWaitUntilTerminated, ResultCode)) or
     (ResultCode <> 0) then
  begin
    Log('WARNING: Weaviate connectivity probe failed for ' + WeaviateEndpoint + '. Setup will continue; Weaviate remains externally managed.');
    if not WizardSilent then
      MsgBox('Local RAG was installed, but Weaviate did not respond at ' + WeaviateEndpoint + '. Setup did not change or start Weaviate. Verify the endpoint before using Local RAG.', mbInformation, MB_OK);
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
  begin
    WriteDiscoveryFiles();
    InstallVsCodeExtension();
    ProbeConnectivity();
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  ResultCode: Integer;
  UninstallCodeCommand: String;
  Parameters: String;
begin
  if CurUninstallStep = usUninstall then
  begin
    UninstallCodeCommand := FindCodeCommand();
    if UninstallCodeCommand <> '' then
    begin
      Parameters := '/D /S /C "' + Quote(UninstallCodeCommand) + ' --uninstall-extension starlinx-llc.local-rag"';
      if (not Exec(ExpandConstant('{cmd}'), Parameters, '', SW_HIDE, ewWaitUntilTerminated, ResultCode)) or (ResultCode <> 0) then
        Log('WARNING: VS Code did not remove starlinx-llc.local-rag during uninstall.');
    end
    else
      Log('WARNING: VS Code command-line launcher was unavailable during uninstall; the extension was not removed.');
  end;
end;
