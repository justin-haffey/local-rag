[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Import-Module (Join-Path $PSScriptRoot 'NativeCi.Contracts.psm1') -Force

# Provisioning belongs on a disposable hosted worker, never an installed user's profile.
if ($env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_ENVIRONMENT -ne 'github-hosted' -or $env:RUNNER_OS -ne 'Windows' -or
    -not [Environment]::Is64BitProcess -or $env:GITHUB_RUN_ID -notmatch '^\d+$' -or $env:GITHUB_RUN_ATTEMPT -notmatch '^\d+$') {
    throw 'Run this provisioning/build script only in the manual GitHub-hosted Windows x64 job. Use Test-WindowsNativeCiContract.ps1 for local validation.'
}
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$evidence = Join-Path $repositoryRoot 'artifacts/ci/windows-native/evidence'
$privateRoot = Join-Path $env:RUNNER_TEMP "lr-native-$($env:GITHUB_RUN_ID)-$($env:GITHUB_RUN_ATTEMPT)"
if ((Test-Path -LiteralPath $evidence) -or (Test-Path -LiteralPath $privateRoot)) { throw 'CI output already exists; preserve it and use a fresh job attempt.' }
foreach ($path in @($evidence, $privateRoot)) {
    for ($ancestor = Split-Path -Parent $path; $ancestor; $ancestor = Split-Path -Parent $ancestor) {
        if ((Test-Path -LiteralPath $ancestor) -and ((Get-Item -LiteralPath $ancestor).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'CI outputs cannot be beneath a reparse point.' }
    }
}
New-Item -ItemType Directory -Path $evidence, $privateRoot | Out-Null
$tools = Get-Content -LiteralPath (Join-Path $repositoryRoot '.github/ci/native-tools.json') -Raw | ConvertFrom-Json
$sdk = (Get-Content -LiteralPath (Join-Path $repositoryRoot 'global.json') -Raw | ConvertFrom-Json).sdk.version
$commands = [Collections.Generic.List[object]]::new()
$stage = 'preflight'; $success = $false; $failure = $null; $sourceBefore = $null; $testOutcome = $null
$started = [DateTime]::UtcNow

function Invoke-CiCommand {
    param([Parameter(Mandatory)][string]$Name, [Parameter(Mandatory)][string]$Command, [string[]]$Arguments = @(), [hashtable]$ScriptParameters)
    $log = Join-Path $evidence "$Name.log"
    $clock = [Diagnostics.Stopwatch]::StartNew()
    $exitCode = $null
    Write-Host "CI gate: $Name"
    try {
        $global:LASTEXITCODE = 0
        if ($null -ne $ScriptParameters) { & $Command @ScriptParameters *> $log }
        else { & $Command @Arguments *> $log }
        $exitCode = $LASTEXITCODE
        if ($exitCode -ne 0) { throw "CI gate '$Name' failed with exit $exitCode; see $Name.log." }
    } finally {
        $clock.Stop()
        $commands.Add([ordered]@{ name = $Name; command = [IO.Path]::GetFileName($Command); arguments = $Arguments; scriptParameters = $ScriptParameters; exitCode = $exitCode; elapsedSeconds = $clock.Elapsed.TotalSeconds })
    }
}

function Get-CiSourceSnapshot {
    $paths = @(& git -C $repositoryRoot ls-files --cached --others --exclude-standard)
    if ($LASTEXITCODE -ne 0) { throw 'Cannot capture CI source inputs.' }
    $files = @($paths | Sort-Object -Unique | Where-Object {
        $_ -match '^(src/|scripts/|\.github/|Directory\.(Build|Packages)\.props$|global\.json$|LocalRag\.sln$)' -and
        $_ -notmatch '(^|/)(bin|obj|target|out|node_modules)/' -and $_ -notmatch '\.(vsix|log|exe)$'
    } | ForEach-Object {
        $path = Join-Path $repositoryRoot $_
        if (Test-Path -LiteralPath $path -PathType Leaf) { [ordered]@{ path = $_; sha256 = (Get-FileHash -LiteralPath $path).Hash } }
    })
    $json = $files | ConvertTo-Json -Depth 5 -Compress
    [ordered]@{ sha256 = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($json))); files = $files }
}

function Get-CiTool {
    param([Parameter(Mandatory)]$Asset, [Parameter(Mandatory)][string]$Name, [Parameter(Mandatory)][string]$Executable)
    $uri = [Uri]$Asset.url
    if ($uri.Scheme -ne 'https' -or $uri.Host -ne 'github.com' -or $Asset.sha256 -notmatch '^[A-Fa-f0-9]{64}$') { throw 'CI tool source must be hash-pinned HTTPS on the recorded upstream GitHub release.' }
    $zip = Join-Path $privateRoot "$Name.zip"
    Invoke-WebRequest -Uri $uri -OutFile $zip -MaximumRetryCount 3 -ConnectionTimeoutSeconds 60 -OperationTimeoutSeconds 300
    if ((Get-FileHash -LiteralPath $zip).Hash -ne $Asset.sha256) { throw "Downloaded $Name hash differs from the checked-in tool manifest." }
    $destination = Join-Path $privateRoot $Name
    [IO.Compression.ZipFile]::ExtractToDirectory($zip, $destination)
    $matches = @(Get-ChildItem -LiteralPath $destination -File -Recurse -Filter $Executable)
    if ($matches.Count -ne 1) { throw "Pinned $Name archive does not contain exactly one $Executable." }
    $matches[0].FullName
}

function Copy-CiNativeMetadata {
    param([string]$HostDirectory)
    foreach ($name in @('lancedb-native.manifest.json', 'lancedb-build.provenance.json', 'THIRD-PARTY-NOTICES-lancedb.json', 'msvc-runtime.manifest.json', 'msvc-runtime-imports.json', 'THIRD-PARTY-NOTICES-msvc.txt')) {
        Copy-Item -LiteralPath (Join-Path $HostDirectory $name) -Destination (Join-Path $evidence $name)
    }
}

try {
    Push-Location $repositoryRoot
    $sourceBefore = Get-CiSourceSnapshot
    [ordered]@{ head = (& git rev-parse HEAD | Out-String).Trim(); capturedUtc = [DateTime]::UtcNow.ToString('o'); inputs = $sourceBefore } | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $evidence 'source-snapshot.json') -Encoding utf8
    if ((& dotnet --version | Out-String).Trim() -ne $sdk) { throw "CI requires the exact repository SDK $sdk installed in DOTNET_INSTALL_DIR." }
    if ((Get-Content -LiteralPath 'src/LocalRag.LanceDb.Native/rust-toolchain.toml' -Raw) -notmatch ('channel\s*=\s*"' + [regex]::Escape($tools.rustVersion) + '"')) { throw 'Rust tool manifest and repository toolchain differ.' }
    $rustup = (Get-Command rustup.exe -ErrorAction Stop).Source
    $cargo = (Get-Command cargo.exe -ErrorAction Stop).Source
    $env:CARGO_HOME = Join-Path $privateRoot 'cargo'
    $env:RUSTUP_HOME = Join-Path $privateRoot 'rustup'
    $env:RUSTUP_TOOLCHAIN = $tools.rustVersion
    $env:CARGO_TARGET_DIR = Join-Path $privateRoot 'target-fresh'
    $env:CARGO_BUILD_JOBS = '2'
    $env:NUGET_PACKAGES = Join-Path $privateRoot 'nuget'
    $env:DOTNET_CLI_HOME = Join-Path $privateRoot 'dotnet-home'
    $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'

    $stage = 'pinned-tools'
    Invoke-CiCommand 'rust-toolchain' $rustup @('toolchain', 'install', $tools.rustVersion, '--profile', 'minimal', '--component', 'rustfmt', '--component', 'clippy', '--target', $tools.target)
    $rustcVersion = (& rustc.exe --version | Out-String).Trim()
    if ($rustcVersion -notlike "rustc $($tools.rustVersion) *") { throw 'The active Rust compiler differs from the pinned toolchain.' }
    $protoc = Get-CiTool $tools.protoc 'protoc' 'protoc.exe'
    $audit = Get-CiTool $tools.cargoAudit 'cargo-audit' 'cargo-audit.exe'
    if ((& $protoc --version | Out-String).Trim() -ne "libprotoc $($tools.protoc.version)") { throw 'Pinned protoc executable version mismatch.' }
    if ((& $audit --version | Out-String).Trim() -ne "cargo-audit $($tools.cargoAudit.version)") { throw 'Pinned cargo-audit executable version mismatch.' }
    $env:PROTOC = $protoc
    [ordered]@{ sdk = $sdk; rust = $rustcVersion; target = $tools.target; cargo = (& $cargo --version | Out-String).Trim(); protoc = $tools.protoc; cargoAudit = $tools.cargoAudit; rustBuildJobs = 2; toolsManifestSha256 = (Get-FileHash '.github/ci/native-tools.json').Hash; os = [Environment]::OSVersion.VersionString; logicalProcessors = [Environment]::ProcessorCount; memoryBytes = (Get-CimInstance Win32_ComputerSystem).TotalPhysicalMemory } | ConvertTo-Json -Depth 7 | Set-Content (Join-Path $evidence 'environment.json') -Encoding utf8

    $stage = 'model'
    $modelRoot = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)) 'LocalRag/models/bge-small-en-v1.5'
    if (-not ([Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)).Equals($env:LOCALAPPDATA, [StringComparison]::OrdinalIgnoreCase)) { throw 'Hosted model paths disagree; do not allow a silently missing model gate.' }
    Invoke-CiCommand -Name 'model-provision' -Command (Join-Path $PSScriptRoot 'Install-LocalRagEmbeddingModel.ps1') -ScriptParameters @{ ModelDirectory = $modelRoot }
    Assert-NativeCiModelAssets -ManifestPath (Join-Path $PSScriptRoot 'embedding-model.manifest.json') -ModelDirectory $modelRoot | ConvertTo-Json | Set-Content (Join-Path $evidence 'model-verification.json') -Encoding utf8
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'embedding-model.manifest.json') -Destination $evidence

    $nativeProject = Join-Path $repositoryRoot 'src/LocalRag.LanceDb.Native'
    $testProject = Join-Path $repositoryRoot 'src/tests/LocalRag.Host.Tests/LocalRag.Host.Tests.csproj'
    $stage = 'restore-and-static'
    Invoke-CiCommand 'managed-restore' 'dotnet' @('restore', $testProject)
    Invoke-CiCommand 'managed-build' 'dotnet' @('build', $testProject, '-c', 'Release', '--no-restore')
    Invoke-CiCommand 'rust-fmt' $cargo @('fmt', '--manifest-path', (Join-Path $nativeProject 'Cargo.toml'), '--', '--check')
    Invoke-CiCommand -Name 'supply-chain' -Command (Join-Path $PSScriptRoot 'Test-LanceDbSupplyChain.ps1') -ScriptParameters @{ CargoPath = $cargo; CargoAuditPath = $audit }
    $advisoryRevision = (& git -C (Join-Path $env:CARGO_HOME 'advisory-db') rev-parse HEAD | Out-String).Trim()
    if ($LASTEXITCODE -ne 0 -or $advisoryRevision -notmatch '^[a-f0-9]{40}$') { throw 'The audited RustSec database revision could not be recorded.' }
    [ordered]@{ advisoryRevision = $advisoryRevision; tool = $tools.cargoAudit.version; target = $tools.target; exceptions = @('RUSTSEC-2026-0194', 'RUSTSEC-2026-0195'); exceptionScope = 'Existing script proves vulnerable quick-xml versions absent from actual Windows graph before applying Linux-only lockfile exceptions; unmaintained warnings remain visible.' } | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $evidence 'supply-chain-scope.json') -Encoding utf8

    $stage = 'fresh-native-publish'
    $hostRoot = Join-Path $privateRoot 'host'
    Invoke-CiCommand -Name 'native-publish' -Command (Join-Path $PSScriptRoot 'Publish-Backend.ps1') -ScriptParameters @{ Configuration = 'Release'; OutputDirectory = $hostRoot; CargoPath = $cargo; ProtocPath = $protoc }
    [ordered]@{ visualCppTools = $env:VCToolsVersion; windowsSdk = $env:WindowsSDKVersion; compiler = (Get-Item -LiteralPath (Get-Command cl.exe -ErrorAction Stop).Source).VersionInfo.FileVersion; linker = (Get-Item -LiteralPath (Get-Command link.exe -ErrorAction Stop).Source).VersionInfo.FileVersion } | ConvertTo-Json | Set-Content (Join-Path $evidence 'msvc-environment.json') -Encoding utf8
    Copy-CiNativeMetadata $hostRoot
    Invoke-CiCommand 'rust-tests' $cargo @('test', '--locked', '--manifest-path', (Join-Path $nativeProject 'Cargo.toml'), '--target', $tools.target, '--release')
    Invoke-CiCommand 'rust-clippy' $cargo @('clippy', '--locked', '--manifest-path', (Join-Path $nativeProject 'Cargo.toml'), '--target', $tools.target, '--release', '--', '-D', 'warnings')

    $stage = 'native-onnx-tests'
    $testBin = Join-Path $repositoryRoot 'src/tests/LocalRag.Host.Tests/bin/Release/net10.0'
    $manifestPath = Join-Path $hostRoot 'lancedb-native.manifest.json'
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    $runtime = Get-Content -LiteralPath (Join-Path $hostRoot 'msvc-runtime.manifest.json') -Raw | ConvertFrom-Json
    $nativeFiles = @($manifest.libraryFile, 'lancedb-native.manifest.json', 'lancedb-build.provenance.json', 'msvc-runtime.manifest.json', 'msvc-runtime-imports.json') + @($manifest.licenses.file) + @($runtime.files.file)
    foreach ($name in ($nativeFiles | Sort-Object -Unique)) {
        if ([IO.Path]::GetFileName($name) -ne $name) { throw 'Native staging accepts file names only.' }
        Copy-Item -LiteralPath (Join-Path $hostRoot $name) -Destination (Join-Path $testBin $name) -Force
    }
    $env:LOCALRAG_NATIVE_RELEASE = '1'
    $env:LOCALRAG_LANCEDB_NATIVE_MANIFEST = $manifestPath
    $env:LOCALRAG_NATIVE_RUN_DIRECTORY = Join-Path $privateRoot 'native-data'
    $env:LOCALRAG_ONNX_TESTS = '1'
    $filter = 'Category=NativeIntegration|FullyQualifiedName~LocalRag.Host.Tests.LanceDbReconciliationLiveIntegrationTests|FullyQualifiedName~LocalRag.Host.Tests.ManagerHostIntegrationTests.ConfirmedEmbedded|FullyQualifiedName~LocalRag.Host.Tests.ManagerHostIntegrationTests.Embedded|FullyQualifiedName~LocalRag.Host.Tests.ActiveWritePauseIntegrationTests.Embedded|FullyQualifiedName~LocalRag.Host.Tests.VectorWriteIntentRecoveryTests'
    $results = Join-Path $evidence 'tests'
    Invoke-CiCommand 'native-onnx-tests' 'dotnet' @('test', $testProject, '-c', 'Release', '--no-build', '--no-restore', '--filter', $filter, '--logger', 'trx;LogFileName=native-required.trx', '--results-directory', $results)
    $testOutcome = Assert-NativeCiTrx -Path (Join-Path $results 'native-required.trx')
    $testOutcome | ConvertTo-Json | Set-Content (Join-Path $evidence 'required-test-outcome.json') -Encoding utf8

    $stage = 'process-safety'
    $processRoot = Join-Path $repositoryRoot "artifacts/runs/ci-process-$($env:GITHUB_RUN_ID)-$($env:GITHUB_RUN_ATTEMPT)"
    Invoke-CiCommand -Name 'process-safety' -Command (Join-Path $PSScriptRoot 'Test-LanceDbProcessSafety.ps1') -ScriptParameters @{ NativePackageDirectory = $hostRoot; OutputDirectory = $processRoot }
    foreach ($file in Get-ChildItem -LiteralPath $processRoot -File) {
        if ($file.Extension -eq '.json') { Copy-Item -LiteralPath $file.FullName -Destination (Join-Path $evidence ('process-' + $file.Name)) }
        elseif ($file.Extension -in @('.log', '.stdout', '.stderr')) { Copy-Item -LiteralPath $file.FullName -Destination (Join-Path $evidence ('process-' + $file.Name + '.log')) }
    }
    Invoke-CiCommand 'nuget-resolved-graph' 'dotnet' @('list', $testProject, 'package', '--include-transitive', '--format', 'json')
    [ordered]@{ directory = 'private runner host output; binaries not uploaded'; files = @(Get-ChildItem -LiteralPath $hostRoot -File -Recurse | ForEach-Object { [ordered]@{ path = [IO.Path]::GetRelativePath($hostRoot, $_.FullName).Replace('\', '/'); bytes = $_.Length; sha256 = (Get-FileHash -LiteralPath $_.FullName).Hash } }) } | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $evidence 'host-package-inventory.json') -Encoding utf8
    $sourceAfter = Get-CiSourceSnapshot
    if ($sourceBefore.sha256 -ne $sourceAfter.sha256) { throw 'CI source inputs changed during verification; preserve failure evidence and rebuild only after source stabilizes.' }
    $success = $true; $stage = 'completed'
} catch {
    $failure = $_.Exception.ToString()
    throw
} finally {
    [ordered]@{ schemaVersion = 1; startedUtc = $started.ToString('o'); completedUtc = [DateTime]::UtcNow.ToString('o'); success = $success; stage = $stage; failure = $failure; sourceSnapshotSha256 = $(if ($null -ne $sourceBefore) { $sourceBefore.sha256 } else { $null }); commands = $commands; requiredTests = $testOutcome; runtimeRedistributionApproved = $false; binariesUploaded = $false; sourceByteIdentityClaimed = $false; scope = 'Windows x64 native build/package/correctness gate only; not installer, Weaviate, desktop, performance, human acceptance or publication certification. Cargo is locked; central NuGet pins and resolved graph are recorded without claiming a transitive NuGet lockfile.' } | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $evidence 'ci-summary.json') -Encoding utf8
    $index = @(Get-ChildItem -LiteralPath $evidence -File -Recurse | ForEach-Object { [ordered]@{ path = [IO.Path]::GetRelativePath($evidence, $_.FullName).Replace('\', '/'); sha256 = (Get-FileHash -LiteralPath $_.FullName).Hash; bytes = $_.Length } })
    $index | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $evidence 'evidence-sha256.json') -Encoding utf8
    Pop-Location
}
