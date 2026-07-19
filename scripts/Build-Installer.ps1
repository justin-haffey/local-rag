[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [string]$OutputDirectory,
    [string]$ModelCacheDirectory,
    [string]$IsccPath,
    [switch]$SkipDependencyRestore,
    [switch]$SkipExtensionTests,
    [switch]$SkipModelDownload
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Invoke-NativeCommand {
    param(
        [Parameter(Mandatory)][string]$Command,
        [Parameter(Mandatory)][string[]]$Arguments
    )
    Write-Host "> $Command $($Arguments -join ' ')"
    & $Command @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "Command failed with exit code ${LASTEXITCODE}: $Command"
    }
}

function Resolve-InnoCompiler {
    param([string]$RequestedPath)
    if (-not [string]::IsNullOrWhiteSpace($RequestedPath)) {
        $resolved = [System.IO.Path]::GetFullPath($RequestedPath)
        if (-not (Test-Path -LiteralPath $resolved -PathType Leaf)) { throw "Inno Setup compiler was not found at $resolved." }
        return $resolved
    }

    $command = Get-Command 'ISCC.exe' -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($null -ne $command) { return $command.Source }

    foreach ($candidate in @(
        (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
        (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe'),
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe')
    )) {
        if (Test-Path -LiteralPath $candidate -PathType Leaf) { return $candidate }
    }
    throw 'Inno Setup 6 was not found. Install it per-user or pass -IsccPath to ISCC.exe.'
}

function Assert-FileHashAndLength {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][long]$ExpectedLength,
        [Parameter(Mandatory)][string]$ExpectedHash
    )
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw "Required asset was not found: $Path" }
    $file = Get-Item -LiteralPath $Path
    $hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $Path).Hash
    if ($file.Length -ne $ExpectedLength -or $hash -ne $ExpectedHash) {
        throw "Asset validation failed for $Path. Expected $ExpectedLength bytes and SHA-256 $ExpectedHash; received $($file.Length) bytes and $hash."
    }
}

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$packageJsonPath = Join-Path $repositoryRoot 'src\vscode-extension\package.json'
$packageJson = Get-Content -Raw -LiteralPath $packageJsonPath | ConvertFrom-Json
$version = [string]$packageJson.version
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw "Installer builds require a three-part numeric extension version; found '$version'." }

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) { $OutputDirectory = Join-Path $repositoryRoot 'artifacts' }
if (-not [System.IO.Path]::IsPathRooted($OutputDirectory)) { $OutputDirectory = Join-Path $repositoryRoot $OutputDirectory }
$OutputDirectory = [System.IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null

if ([string]::IsNullOrWhiteSpace($ModelCacheDirectory)) {
    $ModelCacheDirectory = Join-Path $env:LOCALAPPDATA 'LocalRag\models\bge-small-en-v1.5'
}
$ModelCacheDirectory = [System.IO.Path]::GetFullPath($ModelCacheDirectory)

$compiler = Resolve-InnoCompiler -RequestedPath $IsccPath
$publishScript = Join-Path $PSScriptRoot 'Publish-Backend.ps1'
$packageScript = Join-Path $PSScriptRoot 'Package-VsCodeExtension.ps1'
$modelInstallScript = Join-Path $PSScriptRoot 'Install-LocalRagEmbeddingModel.ps1'
$manifestPath = Join-Path $PSScriptRoot 'embedding-model.manifest.json'
$issPath = Join-Path $repositoryRoot 'src\installer\LocalRag.iss'
$hostPublishDirectory = Join-Path $repositoryRoot 'src\LocalRag.Host\bin\win-x64'
$vsixPath = Join-Path $OutputDirectory "local-rag-$version.vsix"
$stageDirectory = Join-Path $OutputDirectory ('.installer-stage-' + [guid]::NewGuid().ToString('N'))
$hostStage = Join-Path $stageDirectory 'host'
$modelStage = Join-Path $stageDirectory 'model'

try {
    & $publishScript -Configuration $Configuration
    if ($LASTEXITCODE -ne 0) { throw "Backend publishing failed with exit code $LASTEXITCODE." }

    foreach ($requiredHostFile in @('LocalRag.Host.exe', 'LocalRag.Host.dll', 'LocalRag.Host.deps.json', 'LocalRag.Host.runtimeconfig.json', 'appsettings.json', 'onnxruntime.dll', 'e_sqlite3.dll')) {
        $requiredPath = Join-Path $hostPublishDirectory $requiredHostFile
        if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf)) { throw "Published host layout is incomplete: $requiredHostFile is missing." }
    }

    $packageArguments = @{
        OutputPath = $vsixPath
        SkipInstall = $true
    }
    if ($SkipDependencyRestore) { $packageArguments.SkipDependencyRestore = $true }
    if ($SkipExtensionTests) { $packageArguments.SkipTests = $true }
    & $packageScript @packageArguments
    if ($LASTEXITCODE -ne 0) { throw "VSIX packaging failed with exit code $LASTEXITCODE." }

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [System.IO.Compression.ZipFile]::OpenRead($vsixPath)
    try {
        $entryNames = @($archive.Entries | ForEach-Object FullName)
        if ($entryNames -notcontains 'extension/out/extension.js') { throw 'VSIX validation failed: extension/out/extension.js is missing.' }
        if ($entryNames | Where-Object { $_ -like 'extension/bin/*' }) { throw 'VSIX validation failed: standalone host binaries must not be embedded in the VSIX.' }
    } finally {
        $archive.Dispose()
    }

    # Weaviate is an externally managed service; this build stages only the local model assets.
    if (-not $SkipModelDownload) {
        & $modelInstallScript -ModelDirectory $ModelCacheDirectory -ManifestPath $manifestPath
        if ($LASTEXITCODE -ne 0) { throw "Model staging failed with exit code $LASTEXITCODE." }
    }

    $manifest = Get-Content -Raw -LiteralPath $manifestPath | ConvertFrom-Json
    foreach ($asset in $manifest.files) {
        Assert-FileHashAndLength -Path (Join-Path $ModelCacheDirectory $asset.path) -ExpectedLength $asset.sizeBytes -ExpectedHash $asset.sha256
    }

    New-Item -ItemType Directory -Force -Path $hostStage, $modelStage | Out-Null
    Copy-Item -Path (Join-Path $hostPublishDirectory '*') -Destination $hostStage -Recurse -Force
    foreach ($asset in $manifest.files) {
        Copy-Item -LiteralPath (Join-Path $ModelCacheDirectory $asset.path) -Destination (Join-Path $modelStage $asset.path) -Force
    }

    Invoke-NativeCommand -Command $compiler -Arguments @(
        "/DAppVersion=$version",
        "/DHostSource=$hostStage",
        "/DVsixSource=$vsixPath",
        "/DModelSource=$modelStage",
        "/DInstallerOutput=$OutputDirectory",
        $issPath
    )

    $setupPath = Join-Path $OutputDirectory "LocalRag-Setup-$version.exe"
    if (-not (Test-Path -LiteralPath $setupPath -PathType Leaf)) { throw "Inno Setup completed without creating $setupPath." }
    $setupHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $setupPath).Hash
    $hashPath = "$setupPath.sha256"
    [System.IO.File]::WriteAllText($hashPath, "$setupHash *$([System.IO.Path]::GetFileName($setupPath))`r`n", [System.Text.UTF8Encoding]::new($false))
    Write-Host "Created $setupPath"
    Write-Host "SHA-256 $setupHash"
} finally {
    # Keep cleanup constrained to the output tree in case a caller supplies an unexpected path.
    if (Test-Path -LiteralPath $stageDirectory) {
        $resolvedStage = [System.IO.Path]::GetFullPath($stageDirectory)
        $resolvedOutput = [System.IO.Path]::GetFullPath($OutputDirectory).TrimEnd('\') + '\'
        if (-not $resolvedStage.StartsWith($resolvedOutput, [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "Refusing to remove staging directory outside the installer output directory: $resolvedStage"
        }
        Remove-Item -LiteralPath $resolvedStage -Recurse -Force
    }
}
