[CmdletBinding()]
param(
    [string]$CargoPath = 'cargo.exe',
    [string]$CargoAuditPath = 'cargo-audit.exe'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$nativeRoot = Join-Path $repositoryRoot 'src\LocalRag.LanceDb.Native'
$lockPath = Join-Path $nativeRoot 'Cargo.lock'
$target = 'x86_64-pc-windows-msvc'

Push-Location $nativeRoot
try {
    # These advisories enter Cargo.lock only through lance-testing -> Linux-only
    # pprof/flamegraph -> inferno. Prove they are absent from the shipped target
    # before applying the narrow lockfile-scanner exceptions below.
    $ErrorActionPreference = 'Continue'
    $windowsGraph = @(& $CargoPath tree --locked --target $target --prefix none --format '{p}' 2>&1)
    $treeExitCode = $LASTEXITCODE
    $ErrorActionPreference = 'Stop'
    if ($treeExitCode -ne 0) { throw 'The Windows x64 Cargo dependency graph could not be resolved.' }
    foreach ($line in $windowsGraph) {
        if ([string]$line -match '^quick-xml v(\d+\.\d+\.\d+)') {
            if ([version]$Matches[1] -lt [version]'0.41.0') {
                throw "Vulnerable quick-xml $($Matches[1]) unexpectedly entered the Windows x64 dependency graph."
            }
        }
    }

    $ErrorActionPreference = 'Continue'
    & $CargoAuditPath audit `
        --file $lockPath `
        --target-os windows `
        --target-arch x86_64 `
        --ignore RUSTSEC-2026-0194 `
        --ignore RUSTSEC-2026-0195
    $auditExitCode = $LASTEXITCODE
    $ErrorActionPreference = 'Stop'
    if ($auditExitCode -ne 0) { throw "The Windows x64 RustSec audit failed with exit code $auditExitCode." }
} finally {
    Pop-Location
}

Write-Host 'LanceDB Windows x64 supply-chain gates passed.'
