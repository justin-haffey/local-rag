[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [Parameter(Mandatory)]
    [string]$OutputDirectory,
    [string]$TargetDirectory,
    [string]$CargoPath,
    [string]$ProtocPath,
    [string]$VsDevCmdPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Resolve-Tool {
    param([string]$RequestedPath, [string]$CommandName, [string[]]$Fallbacks)
    if (-not [string]::IsNullOrWhiteSpace($RequestedPath)) {
        $resolved = [System.IO.Path]::GetFullPath($RequestedPath)
        if (-not (Test-Path -LiteralPath $resolved -PathType Leaf)) {
            throw "$CommandName was not found at $resolved."
        }
        return $resolved
    }
    $command = Get-Command $CommandName -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($null -ne $command) { return $command.Source }
    foreach ($fallback in $Fallbacks) {
        if (-not [string]::IsNullOrWhiteSpace($fallback) -and (Test-Path -LiteralPath $fallback -PathType Leaf)) {
            return [System.IO.Path]::GetFullPath($fallback)
        }
    }
    throw "$CommandName was not found. Install the pinned native toolchain or pass its path explicitly."
}

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$nativeRoot = Join-Path $repositoryRoot 'src\LocalRag.LanceDb.Native'
$output = [System.IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Force -Path $output | Out-Null
$target = 'x86_64-pc-windows-msvc'
if ([string]::IsNullOrWhiteSpace($TargetDirectory)) {
    $TargetDirectory = if ([string]::IsNullOrWhiteSpace($env:CARGO_TARGET_DIR)) { Join-Path $nativeRoot 'target' } else { $env:CARGO_TARGET_DIR }
}
$targetDirectoryPath = [System.IO.Path]::GetFullPath($TargetDirectory)

$cargo = Resolve-Tool -RequestedPath $CargoPath -CommandName 'cargo.exe' -Fallbacks @(
    (Join-Path $env:USERPROFILE '.cargo\bin\cargo.exe'),
    (Join-Path $env:TEMP 'localrag-cargo\bin\cargo.exe')
)
$protoc = Resolve-Tool -RequestedPath $ProtocPath -CommandName 'protoc.exe' -Fallbacks @(
    $env:PROTOC,
    (Join-Path $env:TEMP 'localrag-protoc-35.0\bin\protoc.exe')
)
$vsDevCmd = Resolve-Tool -RequestedPath $VsDevCmdPath -CommandName 'VsDevCmd.bat' -Fallbacks @(
    (Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\2022\BuildTools\Common7\Tools\VsDevCmd.bat'),
    (Join-Path $env:ProgramFiles 'Microsoft Visual Studio\2022\Community\Common7\Tools\VsDevCmd.bat'),
    (Join-Path $env:ProgramFiles 'Microsoft Visual Studio\2022\Professional\Common7\Tools\VsDevCmd.bat'),
    (Join-Path $env:ProgramFiles 'Microsoft Visual Studio\2022\Enterprise\Common7\Tools\VsDevCmd.bat')
)

$profile = if ($Configuration -eq 'Release') { 'release' } else { 'debug' }
$environmentLines = & $env:ComSpec /d /c "`"$vsDevCmd`" -arch=x64 -host_arch=x64 >nul && set"
if ($LASTEXITCODE -ne 0) { throw "Visual Studio build environment initialization failed with exit code $LASTEXITCODE." }
foreach ($line in $environmentLines) {
    $separator = $line.IndexOf('=')
    if ($separator -gt 0) {
        [Environment]::SetEnvironmentVariable(
            $line.Substring(0, $separator),
            $line.Substring($separator + 1),
            [EnvironmentVariableTarget]::Process)
    }
}
$env:PROTOC = $protoc
$buildArguments = @('build', '--locked', '--target', $target, '--target-dir', $targetDirectoryPath)
if ($Configuration -eq 'Release') { $buildArguments += '--release' }
Push-Location $nativeRoot
try {
    & $cargo @buildArguments
    if ($LASTEXITCODE -ne 0) { throw "Locked LanceDB native build failed with exit code $LASTEXITCODE." }
} finally {
    Pop-Location
}

$librarySource = Join-Path $targetDirectoryPath "$target\$profile\localrag_lancedb.dll"
if (-not (Test-Path -LiteralPath $librarySource -PathType Leaf)) {
    throw "The native build did not create $librarySource."
}
$libraryDestination = Join-Path $output 'localrag_lancedb.dll'
Copy-Item -LiteralPath $librarySource -Destination $libraryDestination -Force
& (Join-Path $PSScriptRoot 'Stage-MsvcRuntime.ps1') -OutputDirectory $output | Out-Host
$pdbSource = Join-Path $targetDirectoryPath "$target\$profile\localrag_lancedb.pdb"
if (Test-Path -LiteralPath $pdbSource -PathType Leaf) {
    Copy-Item -LiteralPath $pdbSource -Destination (Join-Path $output 'localrag_lancedb.pdb') -Force
}

$metadataText = & $cargo metadata --locked --filter-platform $target --format-version 1 --manifest-path (Join-Path $nativeRoot 'Cargo.toml')
if ($LASTEXITCODE -ne 0) { throw "Cargo dependency inventory failed with exit code $LASTEXITCODE." }
$metadata = $metadataText | ConvertFrom-Json
$resolvedIds = @($metadata.resolve.nodes | ForEach-Object id)
$packages = @(
    $metadata.packages |
        Where-Object { $_.id -in $resolvedIds } |
        Sort-Object name, version |
        ForEach-Object {
            if ([string]::IsNullOrWhiteSpace([string]$_.license)) {
                throw "Resolved native dependency '$($_.name) $($_.version)' does not declare a license."
            }
            [ordered]@{
                name = [string]$_.name
                version = [string]$_.version
                license = [string]$_.license
                source = [string]$_.source
                repository = [string]$_.repository
            }
        }
)
$noticePath = Join-Path $output 'THIRD-PARTY-NOTICES-lancedb.json'
$notice = [ordered]@{
    schemaVersion = 1
    generatedFrom = 'Cargo.lock'
    package = 'localrag-lancedb-native'
    dependencies = $packages
}
[System.IO.File]::WriteAllText(
    $noticePath,
    (($notice | ConvertTo-Json -Depth 6) + "`r`n"),
    [System.Text.UTF8Encoding]::new($false)
)

$requiredExports = @(
    'lrag_abi_negotiate', 'lrag_db_open', 'lrag_db_close',
    'lrag_cancel_new', 'lrag_cancel_request', 'lrag_cancel_release',
    'lrag_db_ensure_ready', 'lrag_db_upsert_ipc', 'lrag_db_delete_chunks',
    'lrag_db_delete_source', 'lrag_db_search', 'lrag_db_verify_ownership',
    'lrag_db_reset_owned', 'lrag_db_optimize', 'lrag_buffer_data',
    'lrag_buffer_len', 'lrag_buffer_release', 'lrag_error_code',
    'lrag_error_message', 'lrag_error_message_len', 'lrag_error_release'
)
$manifest = [ordered]@{
    schemaVersion = 1
    abiVersion = 1
    rid = 'win-x64'
    libraryFile = 'localrag_lancedb.dll'
    sha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $libraryDestination).Hash
    requiredExports = $requiredExports
    licenses = @(
        [ordered]@{
            file = 'THIRD-PARTY-NOTICES-lancedb.json'
            sha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $noticePath).Hash
        }
        [ordered]@{
            file = 'THIRD-PARTY-NOTICES-msvc.txt'
            sha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath (Join-Path $output 'THIRD-PARTY-NOTICES-msvc.txt')).Hash
        }
    )
}
$manifestPath = Join-Path $output 'lancedb-native.manifest.json'
[System.IO.File]::WriteAllText(
    $manifestPath,
    (($manifest | ConvertTo-Json -Depth 6) + "`r`n"),
    [System.Text.UTF8Encoding]::new($false)
)

& (Join-Path $PSScriptRoot 'Test-LanceDbNativePackage.ps1') -HostPublishDirectory $output | Out-Host
if ($LASTEXITCODE -ne 0) { throw "Native package validation failed with exit code $LASTEXITCODE." }

# Keep build provenance beside the strict runtime ABI manifest, whose schema is
# deliberately independent of build-machine details.
$sourceFiles = @('Cargo.toml', 'Cargo.lock', 'rust-toolchain.toml', 'src/lib.rs', 'include/localrag_lancedb.h')
$sourceHashes = @($sourceFiles | ForEach-Object {
    [ordered]@{ path = $_; sha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath (Join-Path $nativeRoot $_)).Hash }
})
$provenance = [ordered]@{
    schemaVersion = 1
    createdUtc = [DateTime]::UtcNow.ToString('o')
    sourceHead = (& git -C $repositoryRoot rev-parse HEAD | Out-String).Trim()
    workingTree = @(& git -C $repositoryRoot status --porcelain=v1 --untracked-files=all)
    sources = $sourceHashes
    buildScriptSha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $PSCommandPath).Hash
    cargo = (& $cargo --version | Out-String).Trim()
    protoc = (& $protoc --version | Out-String).Trim()
    target = $target
    targetDirectory = $targetDirectoryPath
    arguments = $buildArguments
    librarySha256 = $manifest.sha256
    manifestSha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $manifestPath).Hash
    dependencyInventorySha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $noticePath).Hash
    msvcRuntimeManifestSha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath (Join-Path $output 'msvc-runtime.manifest.json')).Hash
    peImportClosureSha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath (Join-Path $output 'msvc-runtime-imports.json')).Hash
    byteIdenticalBuildVerified = $false
}
[System.IO.File]::WriteAllText(
    (Join-Path $output 'lancedb-build.provenance.json'),
    (($provenance | ConvertTo-Json -Depth 8) + "`r`n"),
    [System.Text.UTF8Encoding]::new($false))

[pscustomobject]@{
    LibraryPath = $libraryDestination
    ManifestPath = $manifestPath
    DependencyCount = $packages.Count
}
