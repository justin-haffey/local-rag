[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$HostPublishDirectory,
    [Parameter(Mandatory)][string]$VsixPath,
    [Parameter(Mandatory)][string]$ModelDirectory,
    [Parameter(Mandatory)][string]$TestRoot,
    [string]$Version = '0.1.0',
    [string]$WeaviateEndpoint = 'http://127.0.0.1:18080',
    [string]$IsccPath = (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe')
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repositoryRoot = Split-Path -Parent $PSScriptRoot
if (-not [IO.Path]::IsPathRooted($TestRoot)) { throw 'TestRoot must be an absolute path.' }
$runBoundary = [IO.Path]::GetFullPath((Join-Path $repositoryRoot 'artifacts\runs')).TrimEnd('\') + '\'
$root = [IO.Path]::GetFullPath($TestRoot)
if (-not $root.StartsWith($runBoundary, [StringComparison]::OrdinalIgnoreCase) -or $root.Contains('..')) {
    throw 'Installer execution requires an absolute isolated root beneath this repository artifacts\runs directory.'
}
if (Test-Path -LiteralPath $root) { throw 'Use a new test root; prior installer evidence must be retained.' }
if (Test-Path -LiteralPath 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{E7988E5F-3E15-45DD-A90E-7C6513893D79}_is1') {
    throw 'Another isolated installer instance is registered. Preserve and finish that run before starting another.'
}
for ($ancestor = Split-Path -Parent $root; $ancestor; $ancestor = Split-Path -Parent $ancestor) {
    if ((Test-Path -LiteralPath $ancestor) -and ((Get-Item -LiteralPath $ancestor).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw "Refusing an installer execution root beneath a reparse point: $ancestor"
    }
}
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw 'The installer test requires a three-part numeric version.' }
$hostRoot = (Resolve-Path -LiteralPath $HostPublishDirectory).Path
$vsix = (Resolve-Path -LiteralPath $VsixPath).Path
$models = (Resolve-Path -LiteralPath $ModelDirectory).Path
& (Join-Path $PSScriptRoot 'Test-LanceDbNativePackage.ps1') -HostPublishDirectory $hostRoot | Out-Null
New-Item -ItemType Directory -Path $root | Out-Null
$output = Join-Path $root 'build'
New-Item -ItemType Directory -Path $output | Out-Null
$results = [Collections.Generic.List[object]]::new()
$userData = Join-Path $env:LOCALAPPDATA 'LocalRag'
$protectedNames = @('installation.json', 'install-settings.json', 'client-token', 'management-token')
function Get-ProtectedState {
    $state = [ordered]@{}
    foreach ($name in $protectedNames) {
        $path = Join-Path $userData $name
        $state[$name] = if (Test-Path -LiteralPath $path -PathType Leaf) { (Get-FileHash -Algorithm SHA256 -LiteralPath $path).Hash } else { $null }
    }
    return ($state | ConvertTo-Json -Compress)
}
$protectedBefore = Get-ProtectedState
function Invoke-SetupCase {
    param([string]$Name, [string[]]$Extra, [bool]$Success)
    $log = Join-Path $root "$Name.log"
    $arguments = @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/SP-', "/LOG=`"$log`"") + $Extra
    $process = Start-Process -FilePath $setup -ArgumentList $arguments -WindowStyle Hidden -Wait -PassThru
    $expected = if ($Success) { $process.ExitCode -eq 0 } else { $process.ExitCode -ne 0 }
    $results.Add([ordered]@{ scenario = $Name; exitCode = $process.ExitCode; expectedSuccess = $Success; passed = $expected; log = $log })
    if (-not $expected) { throw "Installer scenario '$Name' returned unexpected exit code $($process.ExitCode)." }
}
try {
    & $IsccPath "/DAppVersion=$Version" "/DHostSource=$hostRoot" "/DVsixSource=$vsix" "/DModelSource=$models" "/DInstallerOutput=$output" "/DLocalRagTestRoot=$root" (Join-Path $repositoryRoot 'src\installer\LocalRag.iss') *> (Join-Path $root 'compiler.log')
    if ($LASTEXITCODE -ne 0) { throw "Isolated installer compiler failed: $LASTEXITCODE" }
    $setup = Join-Path $output "LocalRag-Setup-$Version.exe"
    $data = Join-Path $root 'data'
    $settingsPath = Join-Path $data 'install-settings.json'
    Invoke-SetupCase -Name 'fresh-missing-provider-refused' -Extra @() -Success $false
    if (Test-Path -LiteralPath $settingsPath) { throw 'Refused fresh setup wrote provider settings.' }
    Invoke-SetupCase -Name 'fresh-lancedb' -Extra @('/PROVIDER=LanceDb') -Success $true
    $settings = Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json
    if ($settings.schemaVersion -ne 2 -or $settings.provider -ne 'LanceDb' -or -not $settings.lanceDb.path.StartsWith($root, [StringComparison]::OrdinalIgnoreCase)) { throw 'Fresh LanceDB provider settings do not use the isolated root.' }
    $discoveryPath = Join-Path $data 'installation.json'
    $discovery = Get-Content -LiteralPath $discoveryPath -Raw | ConvertFrom-Json
    if (-not $discovery.hostExecutable.StartsWith($root, [StringComparison]::OrdinalIgnoreCase)) { throw 'Host discovery escaped the isolated root.' }
    $installedHostRoot = Split-Path -Parent $discovery.hostExecutable
    & (Join-Path $PSScriptRoot 'Test-LanceDbNativePackage.ps1') -HostPublishDirectory $installedHostRoot | Out-Null
    foreach ($name in @('model.onnx', 'vocab.txt')) {
        if ((Get-FileHash -LiteralPath (Join-Path $models $name)).Hash -ne (Get-FileHash -LiteralPath (Join-Path $data "models\bge-small-en-v1.5\$name")).Hash) { throw "Installed model asset differs: $name" }
    }
    $settingsHash = (Get-FileHash -LiteralPath $settingsPath).Hash
    Invoke-SetupCase -Name 'unsafe-provider-change-refused' -Extra @('/PROVIDER=Weaviate') -Success $false
    if ((Get-FileHash -LiteralPath $settingsPath).Hash -ne $settingsHash) { throw 'Refused provider change modified settings.' }
    Invoke-SetupCase -Name 'schema-v2-repair' -Extra @() -Success $true
    if ((Get-FileHash -LiteralPath $settingsPath).Hash -ne $settingsHash) { throw 'Schema-v2 repair changed provider settings.' }
    $settingsText = Get-Content -LiteralPath $settingsPath -Raw
    [IO.File]::WriteAllText($settingsPath, '{"schemaVersion":3,"provider":"LanceDb"}')
    $newerSchemaHash = (Get-FileHash -LiteralPath $settingsPath).Hash
    Invoke-SetupCase -Name 'newer-settings-schema-refused' -Extra @() -Success $false
    if ((Get-FileHash -LiteralPath $settingsPath).Hash -ne $newerSchemaHash) { throw 'Refused newer settings schema was overwritten.' }
    [IO.File]::WriteAllText($settingsPath, $settingsText)
    $discoveryText = Get-Content -LiteralPath $discoveryPath -Raw
    $future = $discoveryText | ConvertFrom-Json
    $future.version = '999.0.0'
    $future | ConvertTo-Json -Compress | Set-Content -LiteralPath $discoveryPath -Encoding utf8
    Invoke-SetupCase -Name 'unsupported-downgrade-refused' -Extra @() -Success $false
    [IO.File]::WriteAllText($discoveryPath, $discoveryText)
    $sentinel = Join-Path $data 'retained-state-sentinel.txt'
    [IO.File]::WriteAllText($sentinel, 'Retain user data on uninstall; this synthetic sentinel is not a database checkpoint.')
    $uninstaller = Join-Path $root 'app\unins000.exe'
    $uninstall = Start-Process -FilePath $uninstaller -ArgumentList @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', "/LOG=`"$(Join-Path $root 'uninstall.log')`"") -WindowStyle Hidden -Wait -PassThru
    if ($uninstall.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $sentinel) -or -not (Test-Path -LiteralPath $settingsPath) -or (Test-Path -LiteralPath $discoveryPath)) { throw 'Uninstall/data-retention checks failed.' }
    $results.Add([ordered]@{ scenario = 'uninstall-retains-data-removes-discovery'; exitCode = $uninstall.ExitCode; passed = $true })
    Move-Item -LiteralPath $data -Destination (Join-Path $root 'retained-lancedb-data')
    New-Item -ItemType Directory -Path $data | Out-Null
    [IO.File]::WriteAllText($settingsPath, ('{"schemaVersion":1,"weaviateEndpoint":"' + $WeaviateEndpoint + '"}'))
    Invoke-SetupCase -Name 'schema-v1-upgrade-preserves-weaviate' -Extra @() -Success $true
    $upgraded = Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json
    if ($upgraded.schemaVersion -ne 2 -or $upgraded.provider -ne 'Weaviate' -or $upgraded.weaviate.endpoint -ne $WeaviateEndpoint) { throw 'Schema-v1 upgrade failed to preserve Weaviate.' }
    Invoke-SetupCase -Name 'weaviate-schema-v2-repair' -Extra @('/PROVIDER=Weaviate') -Success $true
    $uninstall = Start-Process -FilePath $uninstaller -ArgumentList @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART') -WindowStyle Hidden -Wait -PassThru
    if ($uninstall.ExitCode -ne 0) { throw 'Final isolated uninstall failed.' }
    Move-Item -LiteralPath $data -Destination (Join-Path $root 'retained-weaviate-upgrade-data')
    $endpointUri = [Uri]$WeaviateEndpoint
    $baseUrl = '{0}://{1}' -f $endpointUri.Scheme, $endpointUri.DnsSafeHost
    Invoke-SetupCase -Name 'fresh-external-weaviate' -Extra @('/PROVIDER=Weaviate', "/WEAVIATEBASEURL=$baseUrl", "/WEAVIATEPORT=$($endpointUri.Port)") -Success $true
    $freshExternal = Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json
    if ($freshExternal.provider -ne 'Weaviate' -or $freshExternal.weaviate.endpoint -ne $WeaviateEndpoint) { throw 'Fresh external provider selection was not preserved.' }
    $uninstall = Start-Process -FilePath $uninstaller -ArgumentList @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART') -WindowStyle Hidden -Wait -PassThru
    if ($uninstall.ExitCode -ne 0) { throw 'Fresh external installation uninstall failed.' }
} finally {
    $protectedAfter = Get-ProtectedState
    $report = [ordered]@{
        schemaVersion = 1; createdUtc = [DateTime]::UtcNow.ToString('o'); testRoot = $root
        evidenceBoundary = 'Actual setup/install/repair/uninstall using shipping assets and a compile-time isolated AppId, data roots, Start Menu group, and VS Code directories. Does not certify the default installer in a clean Windows profile or host launch/index/search.'
        profileStateUnchanged = ($protectedBefore -eq $protectedAfter)
        hostManifestSha256 = (Get-FileHash -LiteralPath (Join-Path $hostRoot 'lancedb-native.manifest.json')).Hash
        vsixSha256 = (Get-FileHash -LiteralPath $vsix).Hash
        scenarios = $results
    }
    $report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $root 'installer-execution.json') -Encoding utf8
    if ($protectedBefore -ne $protectedAfter) { throw 'The real user installation files changed during the isolated installer test.' }
}
