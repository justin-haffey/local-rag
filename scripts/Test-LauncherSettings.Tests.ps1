$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Import-Module (Join-Path $PSScriptRoot '../src/LocalRag.Host/LocalRag.Launcher.psm1') -Force
$fixtureRoot = Join-Path $PSScriptRoot ('../artifacts/runs/p3-web/launcher-settings-' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($fixtureRoot) | Out-Null
$settingsPath = Join-Path $fixtureRoot 'install-settings.json'
function Assert-True([bool]$Condition, [string]$Message) { if (-not $Condition) { throw $Message } }
$started = [DateTimeOffset]::UtcNow
$outcome = 'Failed'
$before = @{}
foreach ($variable in @(Get-ChildItem Env: | Where-Object { $_.Name -like 'LocalRag__VectorStore__*' -or $_.Name -like 'LocalRag__Weaviate__*' })) { $before[$variable.Name] = $variable.Value }
try {
    [IO.File]::WriteAllText($settingsPath, '{"schemaVersion":1,"weaviateEndpoint":"http://127.0.0.1:18080/"}')
    $legacy = Get-LocalRagProviderEnvironment $settingsPath
    Assert-True ($legacy.LocalRag__VectorStore__Provider -eq 'Weaviate') 'Legacy settings must retain Weaviate.'
    Assert-True ($legacy.LocalRag__VectorStore__Weaviate__Endpoint -eq 'http://127.0.0.1:18080') 'Legacy endpoint was changed.'
    $env:LocalRag__VectorStore__Provider = 'LanceDb'; $env:LocalRag__VectorStore__LanceDb__Path = 'C:\wrong-root'; $env:LocalRag__Weaviate__Endpoint = 'http://wrong-root'
    Set-LocalRagProviderEnvironment $legacy
    Assert-True ($env:LocalRag__VectorStore__Provider -eq 'Weaviate') 'An inherited provider overrode persisted settings.'
    Assert-True (-not (Test-Path Env:LocalRag__VectorStore__LanceDb__Path)) 'Inherited LanceDB path was not cleared.'
    Assert-True (-not (Test-Path Env:LocalRag__Weaviate__Endpoint)) 'Inherited legacy endpoint was not cleared.'
    [IO.File]::WriteAllText($settingsPath, '{"schemaVersion":2,"provider":"LanceDb","lanceDb":{"path":"C:\\test vectors"}}')
    $embedded = Get-LocalRagProviderEnvironment $settingsPath
    Assert-True ($embedded.LocalRag__VectorStore__Provider -eq 'LanceDb') 'LanceDB provider was changed.'
    Assert-True ($embedded.LocalRag__VectorStore__LanceDb__Path -eq 'C:\test vectors') 'LanceDB path was changed.'
    foreach ($invalid in @('{"schemaVersion":99}', '{"schemaVersion":1}', '{"schemaVersion":1,"weaviateEndpoint":"http://user:secret@localhost"}', '{"schemaVersion":2,"provider":"LanceDb","lanceDb":{"path":"relative"}}', '{"schemaVersion":2,"provider":"Weaviate","weaviate":{"endpoint":"http://localhost"},"lanceDb":{"path":"C:\\other"}}', '{broken')) {
        [IO.File]::WriteAllText($settingsPath, $invalid)
        $failed = $false; try { Get-LocalRagProviderEnvironment $settingsPath | Out-Null } catch { $failed = $true }
        Assert-True $failed 'Invalid/contradictory provider settings were accepted.'
    }
    $outcome = 'Passed'
    Write-Host 'Launcher provider settings: v1/v2, inherited environment isolation and six malformed/conflict cases passed.'
} finally {
    foreach ($variable in @(Get-ChildItem Env: | Where-Object { $_.Name -like 'LocalRag__VectorStore__*' -or $_.Name -like 'LocalRag__Weaviate__*' })) { Remove-Item -LiteralPath ('Env:' + $variable.Name) }
    foreach ($key in $before.Keys) { [Environment]::SetEnvironmentVariable($key, [string]$before[$key], 'Process') }
    Remove-Module LocalRag.Launcher -ErrorAction SilentlyContinue
    $sourceFiles = @($PSCommandPath, (Join-Path $PSScriptRoot '../src/LocalRag.Host/LocalRag.Launcher.psm1'))
    $hashes = @($sourceFiles | ForEach-Object { [ordered]@{path=[IO.Path]::GetFullPath($_);sha256=(Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash} })
    [ordered]@{startedUtc=$started.ToString('O');finishedUtc=[DateTimeOffset]::UtcNow.ToString('O');outcome=$outcome;cases='v1/v2 selection, inherited environment cleanup, six invalid settings';sources=$hashes} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $fixtureRoot 'manifest.json') -Encoding utf8
}
