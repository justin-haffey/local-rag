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
#ifdef LocalRagTestRoot
  // Only the isolated installer execution harness defines this. The normal
  // distributable keeps its existing identity and per-user paths.
  #define LocalRagDataRoot LocalRagTestRoot + "\data"
  #define LocalRagAppIdentity "{{E7988E5F-3E15-45DD-A90E-7C6513893D79}"
  #define LocalRagAppDirectory LocalRagTestRoot + "\app"
  #define LocalRagGroup "Local RAG Isolated Test"
#else
  #define LocalRagDataRoot "{localappdata}\LocalRag"
  #define LocalRagAppIdentity "{{BC34EB80-D1CA-483A-BF52-CC6D421A67A6}"
  #define LocalRagAppDirectory "{localappdata}\Programs\LocalRag"
  #define LocalRagGroup "Local RAG"
#endif

[Setup]
AppId={#LocalRagAppIdentity}
AppName=Local RAG
AppVersion={#AppVersion}
AppPublisher=Starlinx LLC
DefaultDirName={#LocalRagAppDirectory}
DefaultGroupName={#LocalRagGroup}
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
Source: "{#ModelSource}\model.onnx"; DestDir: "{#LocalRagDataRoot}\models\bge-small-en-v1.5"; Flags: ignoreversion uninsneveruninstall
Source: "{#ModelSource}\vocab.txt"; DestDir: "{#LocalRagDataRoot}\models\bge-small-en-v1.5"; Flags: ignoreversion uninsneveruninstall

[Icons]
Name: "{group}\Local RAG Manager"; Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File ""{app}\Host\{#AppVersion}\Start-LocalRag.ps1"" -DataDirectory ""{#LocalRagDataRoot}"""; WorkingDir: "{app}\Host\{#AppVersion}"

[UninstallDelete]
Type: files; Name: "{#LocalRagDataRoot}\installation.json"

[Code]
const
  ExistingSettingsNone = 0;
  ExistingSettingsValid = 1;
  ExistingSettingsInvalid = 2;

var
  ProviderPage: TInputOptionWizardPage;
  ConnectivityPage: TInputQueryWizardPage;
  LanceDbPage: TInputDirWizardPage;
  CodeCommand: String;
  SelectedProvider: String;
  WeaviateEndpoint: String;
  LanceDbPath: String;
  ExistingSettingsState: Integer;
  ExistingProvider: String;
  ExistingWeaviateEndpoint: String;
  ExistingLanceDbPath: String;

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

function JsonUnescape(Value: String): String;
begin
  StringChangeEx(Value, '\"', '"', True);
  StringChangeEx(Value, '\\', '\', True);
  Result := Value;
end;

function TryGetJsonString(const Json, Name: String; var Value: String): Boolean;
var
  Prefix: String;
  StartIndex: Integer;
  EndIndex: Integer;
begin
  Prefix := '"' + Name + '":"';
  StartIndex := Pos(Prefix, Json);
  Result := StartIndex > 0;
  if not Result then Exit;
  StartIndex := StartIndex + Length(Prefix);
  EndIndex := StartIndex;
  while EndIndex <= Length(Json) do
  begin
    if (Json[EndIndex] = '"') and ((EndIndex = StartIndex) or (Json[EndIndex - 1] <> '\')) then Break;
    EndIndex := EndIndex + 1;
  end;
  Result := EndIndex <= Length(Json);
  if Result then Value := JsonUnescape(Copy(Json, StartIndex, EndIndex - StartIndex));
end;

function HasJsonProperty(const Json, Name: String): Boolean;
begin
  Result := Pos('"' + Name + '":', Json) > 0;
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

function IsValidEndpoint(const Value: String): Boolean;
var
  Remainder: String;
  Host: String;
  PortText: String;
  Separator: Integer;
  Port: Integer;
begin
  if Pos('http://', Lowercase(Value)) = 1 then
    Remainder := Copy(Value, 8, Length(Value))
  else if Pos('https://', Lowercase(Value)) = 1 then
    Remainder := Copy(Value, 9, Length(Value))
  else
  begin
    Result := False;
    Exit;
  end;

  Separator := Pos(':', Remainder);
  if (Separator <= 1) or (Pos('/', Remainder) > 0) or
     (Pos(':', Copy(Remainder, Separator + 1, Length(Remainder))) > 0) then
  begin
    Result := False;
    Exit;
  end;
  Host := Copy(Remainder, 1, Separator - 1);
  PortText := Copy(Remainder, Separator + 1, Length(Remainder));
  Port := StrToIntDef(PortText, 0);
  Result := IsValidHost(Host) and (Port >= 1) and (Port <= 65535);
end;

function IsValidLanceDbPath(const Value: String): Boolean;
begin
  Result := (Length(Trim(Value)) >= 3) and
    (((Value[1] >= 'A') and (Value[1] <= 'Z')) or ((Value[1] >= 'a') and (Value[1] <= 'z'))) and
    (Value[2] = ':') and (Value[3] = '\');
end;

procedure LoadExistingSettings();
var
  SettingsPath: String;
  Json: AnsiString;
  Provider: String;
begin
  ExistingSettingsState := ExistingSettingsNone;
  SettingsPath := ExpandConstant('{#LocalRagDataRoot}\install-settings.json');
  if not FileExists(SettingsPath) then Exit;
  ExistingSettingsState := ExistingSettingsInvalid;
  if not LoadStringFromFile(SettingsPath, Json) then Exit;

  if Pos('"schemaVersion":1', String(Json)) > 0 then
  begin
    if TryGetJsonString(String(Json), 'weaviateEndpoint', ExistingWeaviateEndpoint) and
       IsValidEndpoint(ExistingWeaviateEndpoint) then
    begin
      ExistingProvider := 'Weaviate';
      ExistingSettingsState := ExistingSettingsValid;
    end;
    Exit;
  end;

  if Pos('"schemaVersion":2', String(Json)) = 0 then Exit;
  if not TryGetJsonString(String(Json), 'provider', Provider) then Exit;
  if CompareText(Provider, 'Weaviate') = 0 then
  begin
    if HasJsonProperty(String(Json), 'lanceDb') then Exit;
    if not TryGetJsonString(String(Json), 'endpoint', ExistingWeaviateEndpoint) then Exit;
    if not IsValidEndpoint(ExistingWeaviateEndpoint) then Exit;
    ExistingProvider := 'Weaviate';
    ExistingSettingsState := ExistingSettingsValid;
  end
  else if CompareText(Provider, 'LanceDb') = 0 then
  begin
    if HasJsonProperty(String(Json), 'weaviate') then Exit;
    if not TryGetJsonString(String(Json), 'path', ExistingLanceDbPath) then Exit;
    if not IsValidLanceDbPath(ExistingLanceDbPath) then Exit;
    ExistingProvider := 'LanceDb';
    ExistingSettingsState := ExistingSettingsValid;
  end;
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
  LoadExistingSettings();

  ProviderPage := CreateInputOptionPage(
    wpSelectDir,
    'Vector store provider',
    'Choose the single vector store used by Local RAG.',
    'Embedded LanceDB is recommended for a fresh Windows x64 installation. External Weaviate remains available and is never installed or managed by Setup.',
    True, False);
  ProviderPage.Add('Embedded LanceDB (recommended)');
  ProviderPage.Add('Connect to external local Weaviate');
  ProviderPage.SelectedValueIndex := 0;

  ConnectivityPage := CreateInputQueryPage(
    ProviderPage.ID,
    'External Weaviate connection',
    'Configure the separately managed Weaviate instance.',
    'Local RAG will use this endpoint but will not install, start, stop, or configure Weaviate.');
  ConnectivityPage.Add('Base URL:', False);
  ConnectivityPage.Add('Port:', False);
  ConnectivityPage.Values[0] := ExpandConstant('{param:WEAVIATEBASEURL|http://localhost}');
  ConnectivityPage.Values[1] := ExpandConstant('{param:WEAVIATEPORT|8080}');

  LanceDbPage := CreateInputDirPage(
    ConnectivityPage.ID,
    'Embedded LanceDB data',
    'Choose the Local RAG-owned embedded vector-store directory.',
    'Setup preserves this mutable directory during repair, upgrade, and uninstall.',
    False, '');
  LanceDbPage.Add('');
  LanceDbPage.Values[0] := ExpandConstant('{param:LANCEDBPATH|{#LocalRagDataRoot}\data\lancedb}');

  if ExistingSettingsState = ExistingSettingsValid then
  begin
    if ExistingProvider = 'Weaviate' then ProviderPage.SelectedValueIndex := 1
    else ProviderPage.SelectedValueIndex := 0;
  end;
end;

function ShouldSkipPage(PageID: Integer): Boolean;
begin
  Result := False;
  if ExistingSettingsState = ExistingSettingsValid then
    Result := (PageID = ProviderPage.ID) or (PageID = ConnectivityPage.ID) or (PageID = LanceDbPage.ID)
  else if PageID = ConnectivityPage.ID then
    Result := ProviderPage.SelectedValueIndex <> 1
  else if PageID = LanceDbPage.ID then
    Result := ProviderPage.SelectedValueIndex <> 0;
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
  end
  else if CurPageID = LanceDbPage.ID then
  begin
    Result := IsValidLanceDbPath(LanceDbPage.Values[0]);
    if not Result then MsgBox('The LanceDB path must be an absolute Windows drive path.', mbError, MB_OK);
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ErrorMessage: String;
  RequestedProvider: String;
  DiscoveryPath: String;
  DiscoveryJson: AnsiString;
  ExistingVersion: String;
  ExistingPackedVersion: Int64;
  SetupPackedVersion: Int64;
begin
  Result := '';
#ifdef LocalRagTestRoot
  if not IsValidLanceDbPath('{#LocalRagTestRoot}') or
     (Pos('\artifacts\runs\', Lowercase('{#LocalRagTestRoot}')) = 0) or
     (Pos('..', '{#LocalRagTestRoot}') <> 0) then
  begin
    Result := 'Isolated installer test root must be an absolute path inside artifacts\runs.';
    Exit;
  end;
#endif
  DiscoveryPath := ExpandConstant('{#LocalRagDataRoot}\installation.json');
  if FileExists(DiscoveryPath) then
  begin
    if not LoadStringFromFile(DiscoveryPath, DiscoveryJson) or
       not TryGetJsonString(String(DiscoveryJson), 'version', ExistingVersion) or
       not StrToVersion(ExistingVersion, ExistingPackedVersion) or
       not StrToVersion('{#AppVersion}', SetupPackedVersion) then
    begin
      Result := 'Existing installation version is invalid. Setup refuses an unverified downgrade.';
      Exit;
    end;
    if ComparePackedVersion(ExistingPackedVersion, SetupPackedVersion) > 0 then
    begin
      Result := 'Setup refuses to downgrade a newer Local RAG installation. Use a supported upgrade or a coherent application rollback.';
      Exit;
    end;
  end;
  RequestedProvider := Trim(ExpandConstant('{param:PROVIDER|}'));
  if (RequestedProvider <> '') and (CompareText(RequestedProvider, 'Weaviate') <> 0) and
     (CompareText(RequestedProvider, 'LanceDb') <> 0) then
  begin
    Result := '/PROVIDER must be exactly Weaviate or LanceDb.';
    Exit;
  end;

  if ExistingSettingsState = ExistingSettingsInvalid then
  begin
    Result := 'Existing Local RAG provider settings are corrupt or use a newer unsupported schema. Setup refuses to overwrite them; repair the settings with a supported installer.';
    Exit;
  end;

  if ExistingSettingsState = ExistingSettingsValid then
  begin
    SelectedProvider := ExistingProvider;
    if (RequestedProvider <> '') and (CompareText(RequestedProvider, ExistingProvider) <> 0) then
    begin
      Result := 'Setup refuses to change the existing vector-store provider from ' + ExistingProvider + ' to ' + RequestedProvider + '. Reset/rebuild through an explicit migration workflow first.';
      Exit;
    end;
    WeaviateEndpoint := ExistingWeaviateEndpoint;
    LanceDbPath := ExistingLanceDbPath;
  end
  else
  begin
    if WizardSilent and (RequestedProvider = '') then
    begin
      Result := 'Fresh unattended installation requires /PROVIDER=Weaviate or /PROVIDER=LanceDb.';
      Exit;
    end;
    if RequestedProvider <> '' then SelectedProvider := RequestedProvider
    else if ProviderPage.SelectedValueIndex = 0 then SelectedProvider := 'LanceDb'
    else SelectedProvider := 'Weaviate';

    if CompareText(SelectedProvider, 'Weaviate') = 0 then
    begin
      if not TryBuildEndpoint(ConnectivityPage.Values[0], ConnectivityPage.Values[1], WeaviateEndpoint, ErrorMessage) then
      begin
        Result := 'Invalid Weaviate connection: ' + ErrorMessage;
        Exit;
      end;
    end
    else
    begin
      LanceDbPath := Trim(LanceDbPage.Values[0]);
      if not IsValidLanceDbPath(LanceDbPath) then
      begin
        Result := 'The LanceDB path must be an absolute Windows drive path.';
        Exit;
      end;
    end;
  end;

  CodeCommand := FindCodeCommand();

end;

procedure WriteDiscoveryFiles();
var
  DataDirectory: String;
  HostExecutable: String;
  SettingsJson: String;
  InstallationJson: String;
begin
  DataDirectory := ExpandConstant('{#LocalRagDataRoot}');
  if not ForceDirectories(DataDirectory) then
    RaiseException('Unable to create Local RAG data directory: ' + DataDirectory);

  HostExecutable := ExpandConstant('{app}\Host\{#AppVersion}\LocalRag.Host.exe');
  if CompareText(SelectedProvider, 'Weaviate') = 0 then
    SettingsJson := '{"schemaVersion":2,"provider":"Weaviate","weaviate":{"endpoint":"' + JsonEscape(WeaviateEndpoint) + '"}}' + #13#10
  else
    SettingsJson := '{"schemaVersion":2,"provider":"LanceDb","lanceDb":{"path":"' + JsonEscape(LanceDbPath) + '"}}' + #13#10;
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
  if CodeCommand = '' then Exit;
  VsixPath := ExpandConstant('{tmp}\local-rag-{#AppVersion}.vsix');
  Parameters := '/D /S /C "' + Quote(CodeCommand) + ' --install-extension ' + Quote(VsixPath) + ' --force"';
#ifdef LocalRagTestRoot
  Parameters := '/D /S /C "' + Quote(CodeCommand) + ' --user-data-dir ' + Quote(ExpandConstant('{#LocalRagTestRoot}\vscode-user')) + ' --extensions-dir ' + Quote(ExpandConstant('{#LocalRagTestRoot}\vscode-extensions')) + ' --install-extension ' + Quote(VsixPath) + ' --force"';
#endif
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
  if CompareText(SelectedProvider, 'Weaviate') <> 0 then Exit;
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
#ifdef LocalRagTestRoot
      Parameters := '/D /S /C "' + Quote(UninstallCodeCommand) + ' --user-data-dir ' + Quote(ExpandConstant('{#LocalRagTestRoot}\vscode-user')) + ' --extensions-dir ' + Quote(ExpandConstant('{#LocalRagTestRoot}\vscode-extensions')) + ' --uninstall-extension starlinx-llc.local-rag"';
#endif
      if (not Exec(ExpandConstant('{cmd}'), Parameters, '', SW_HIDE, ewWaitUntilTerminated, ResultCode)) or (ResultCode <> 0) then
        Log('WARNING: VS Code did not remove starlinx-llc.local-rag during uninstall.');
    end
    else
      Log('WARNING: VS Code command-line launcher was unavailable during uninstall; the extension was not removed.');
  end;
end;
