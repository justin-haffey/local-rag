[CmdletBinding()]
param([string]$IsccPath)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$issPath = Join-Path $repositoryRoot 'src\installer\LocalRag.iss'
$iss = Get-Content -Raw -LiteralPath $issPath
foreach ($requiredContract in @(
    "Fresh unattended installation requires /PROVIDER=Weaviate or /PROVIDER=LanceDb.",
    "Setup refuses to change the existing vector-store provider",
    '{"schemaVersion":2,"provider":"Weaviate","weaviate":{"endpoint":"',
    '{"schemaVersion":2,"provider":"LanceDb","lanceDb":{"path":"',
    "if CompareText(SelectedProvider, 'Weaviate') <> 0 then Exit;"
)) {
    if (-not $iss.Contains($requiredContract)) { throw "Installer contract marker is missing: $requiredContract" }
}

if ([string]::IsNullOrWhiteSpace($IsccPath)) {
    $command = Get-Command 'ISCC.exe' -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($null -ne $command) { $IsccPath = $command.Source }
}
if ([string]::IsNullOrWhiteSpace($IsccPath)) {
    foreach ($candidate in @(
        (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
        (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe'),
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe')
    )) {
        if (Test-Path -LiteralPath $candidate -PathType Leaf) { $IsccPath = $candidate; break }
    }
}
if ([string]::IsNullOrWhiteSpace($IsccPath) -or -not (Test-Path -LiteralPath $IsccPath -PathType Leaf)) {
    Write-Warning 'Inno Setup 6 was not found; static installer contract tests passed but compiler validation was skipped.'
    exit 0
}

$fixtureRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('local-rag-inno-' + [guid]::NewGuid().ToString('N'))
$hostSource = Join-Path $fixtureRoot 'host'
$modelSource = Join-Path $fixtureRoot 'model'
$outputDirectory = Join-Path $fixtureRoot 'output'
New-Item -ItemType Directory -Path $hostSource, $modelSource, $outputDirectory | Out-Null
try {
    [System.IO.File]::WriteAllText((Join-Path $hostSource 'LocalRag.Host.exe'), 'fixture')
    [System.IO.File]::WriteAllText((Join-Path $fixtureRoot 'local-rag.vsix'), 'fixture')
    [System.IO.File]::WriteAllText((Join-Path $modelSource 'model.onnx'), 'fixture')
    [System.IO.File]::WriteAllText((Join-Path $modelSource 'vocab.txt'), 'fixture')
    & $IsccPath `
        '/DAppVersion=0.0.0' `
        "/DHostSource=$hostSource" `
        "/DVsixSource=$(Join-Path $fixtureRoot 'local-rag.vsix')" `
        "/DModelSource=$modelSource" `
        "/DInstallerOutput=$outputDirectory" `
        $issPath | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "Inno Setup compiler validation failed with exit code $LASTEXITCODE." }
    Write-Host 'Installer script contract and compiler tests passed.'
} finally {
    $resolvedFixture = [System.IO.Path]::GetFullPath($fixtureRoot)
    $resolvedTemp = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    if (-not $resolvedFixture.StartsWith($resolvedTemp, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to remove fixture outside the temporary directory: $resolvedFixture"
    }
    Remove-Item -LiteralPath $resolvedFixture -Recurse -Force
}
