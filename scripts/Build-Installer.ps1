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

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $buildId = [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssZ') + '-' + [guid]::NewGuid().ToString('N').Substring(0, 8)
    $OutputDirectory = Join-Path $repositoryRoot "artifacts\releases\$version\$buildId"
}
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
$nativePackageTestScript = Join-Path $PSScriptRoot 'Test-LanceDbNativePackage.ps1'
$manifestPath = Join-Path $PSScriptRoot 'embedding-model.manifest.json'
$issPath = Join-Path $repositoryRoot 'src\installer\LocalRag.iss'
$hostPublishDirectory = Join-Path $OutputDirectory 'host'
$vsixPath = Join-Path $OutputDirectory "local-rag-$version.vsix"
if ((Test-Path -LiteralPath $hostPublishDirectory) -or (Test-Path -LiteralPath $vsixPath) -or (Test-Path -LiteralPath (Join-Path $OutputDirectory "LocalRag-Setup-$version.exe"))) {
    throw 'Use a new installer output directory. Existing release assets must not be overwritten.'
}
$stageDirectory = Join-Path $OutputDirectory ('.installer-stage-' + [guid]::NewGuid().ToString('N'))
$hostStage = Join-Path $stageDirectory 'host'
$modelStage = Join-Path $stageDirectory 'model'

function Get-BuildInputs {
    $paths = @(& git -C $repositoryRoot ls-files --cached --others --exclude-standard)
    if ($LASTEXITCODE -ne 0) { throw 'Source snapshot inventory failed.' }
    $entries = @($paths | Sort-Object -Unique | Where-Object {
        ($_ -match '^(src/|scripts/|Directory\.(Build|Packages)\.props$|global\.json$|LocalRag\.sln$)') -and
        ($_ -notmatch '(^|/)(bin|obj|target|out|node_modules)/') -and ($_ -notmatch '\.(vsix|log|exe)$')
    } | ForEach-Object {
        $path = Join-Path $repositoryRoot $_
        if (Test-Path -LiteralPath $path -PathType Leaf) {
            [ordered]@{ path = $_; sha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $path).Hash }
        }
    })
    $json = $entries | ConvertTo-Json -Depth 5 -Compress
    $sha = [Security.Cryptography.SHA256]::Create()
    try { $hash = [BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($json))).Replace('-', '') }
    finally { $sha.Dispose() }
    return [ordered]@{ sha256 = $hash; files = $entries }
}
$sourceSnapshot = Get-BuildInputs
[ordered]@{
    schemaVersion = 1
    head = (& git -C $repositoryRoot rev-parse HEAD | Out-String).Trim()
    capturedUtc = [DateTime]::UtcNow.ToString('o')
    definition = 'SHA-256 of sorted compiled-source/build-script relative paths and SHA-256 values, including untracked input files; excludes generated output/cache directories.'
    inputs = $sourceSnapshot
} | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'build-source-snapshot.json') -Encoding utf8

try {
    & $publishScript -Configuration $Configuration -OutputDirectory $hostPublishDirectory
    if ($LASTEXITCODE -ne 0) { throw "Backend publishing failed with exit code $LASTEXITCODE." }

    foreach ($requiredHostFile in @('LocalRag.Host.exe', 'LocalRag.Host.dll', 'LocalRag.Host.deps.json', 'LocalRag.Host.runtimeconfig.json', 'appsettings.json', 'onnxruntime.dll', 'e_sqlite3.dll', 'Start-LocalRag.ps1','LocalRag.Launcher.psm1', 'wwwroot/manager.js', 'wwwroot/manager-links.js', 'wwwroot/manager.css')) {
        $requiredPath = Join-Path $hostPublishDirectory $requiredHostFile
        if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf)) { throw "Published host layout is incomplete: $requiredHostFile is missing." }
    }
    & (Join-Path $PSScriptRoot 'Test-ManagerPackage.ps1') -HostPublishDirectory $hostPublishDirectory | Out-Host
    & $nativePackageTestScript -HostPublishDirectory $hostPublishDirectory | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "LanceDB native package validation failed with exit code $LASTEXITCODE." }

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
    $afterSnapshot = Get-BuildInputs
    if ($afterSnapshot.sha256 -ne $sourceSnapshot.sha256) {
        throw 'Build inputs changed while packaging. Preserve these unapproved artifacts and rebuild from a stable source snapshot.'
    }
    [ordered]@{
        schemaVersion = 1
        version = $version
        createdUtc = [DateTime]::UtcNow.ToString('o')
        sourceSnapshotSha256 = $sourceSnapshot.sha256
        sourceSnapshotFileSha256 = (Get-FileHash -LiteralPath (Join-Path $OutputDirectory 'build-source-snapshot.json')).Hash
        installer = [ordered]@{ file = [IO.Path]::GetFileName($setupPath); sha256 = $setupHash; sizeBytes = (Get-Item -LiteralPath $setupPath).Length }
        vsix = [ordered]@{ file = [IO.Path]::GetFileName($vsixPath); sha256 = (Get-FileHash -LiteralPath $vsixPath).Hash; sizeBytes = (Get-Item -LiteralPath $vsixPath).Length }
        nativeManifest = [ordered]@{ file = 'host/lancedb-native.manifest.json'; sha256 = (Get-FileHash -LiteralPath (Join-Path $hostPublishDirectory 'lancedb-native.manifest.json')).Hash }
        nativeProvenanceSha256 = (Get-FileHash -LiteralPath (Join-Path $hostPublishDirectory 'lancedb-build.provenance.json')).Hash
        msvcRuntimeManifestSha256 = (Get-FileHash -LiteralPath (Join-Path $hostPublishDirectory 'msvc-runtime.manifest.json')).Hash
        modelManifestSha256 = (Get-FileHash -LiteralPath $manifestPath).Hash
        validation = 'Managed publish, manager assets, native PE/exports/hash/licenses, extension packaging checks, VSIX content and model hashes; source inputs unchanged during packaging. Installer runtime acceptance is recorded separately.'
        approvedForDistribution = $false
    } | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'release-manifest.json') -Encoding utf8
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
