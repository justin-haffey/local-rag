[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$OutputDirectory,
    [string]$SourceDirectory,
    [switch]$ValidateOnly,
    [switch]$SkipBinaryInspection
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$output = [IO.Path]::GetFullPath($OutputDirectory)
$names = @('concrt140.dll', 'msvcp140.dll', 'msvcp140_1.dll', 'msvcp140_2.dll', 'msvcp140_atomic_wait.dll', 'msvcp140_codecvt_ids.dll', 'vccorlib140.dll', 'vcruntime140.dll', 'vcruntime140_1.dll', 'vcruntime140_threads.dll')
$manifestPath = Join-Path $output 'msvc-runtime.manifest.json'
$noticeName = 'THIRD-PARTY-NOTICES-msvc.txt'
function Get-PeImports {
    param([string]$Path, [switch]$RequireX64)
    $stream = [IO.File]::OpenRead($Path)
    $reader = [IO.BinaryReader]::new($stream)
    try {
        if ($reader.ReadUInt16() -ne 0x5A4D) { throw "Not a PE image: $Path" }
        $stream.Position = 0x3C; $pe = $reader.ReadInt32()
        $stream.Position = $pe
        if ($reader.ReadUInt32() -ne 0x4550) { throw "Invalid PE signature: $Path" }
        $machine = $reader.ReadUInt16(); $sectionCount = $reader.ReadUInt16()
        if ($RequireX64 -and $machine -ne 0x8664) { throw "MSVC runtime is not x64: $Path" }
        $stream.Position = $pe + 20; $optionalSize = $reader.ReadUInt16()
        $optional = $pe + 24; $stream.Position = $optional; $magic = $reader.ReadUInt16()
        $directoryOffset = if ($magic -eq 0x20B) { 112 } elseif ($magic -eq 0x10B) { 96 } else { throw "Unsupported PE optional header: $Path" }
        $stream.Position = $optional + $directoryOffset + 8; $importRva = $reader.ReadUInt32()
        $sections = @()
        for ($index = 0; $index -lt $sectionCount; $index++) {
            $stream.Position = $optional + $optionalSize + ($index * 40) + 8
            $virtualSize = $reader.ReadUInt32(); $address = $reader.ReadUInt32(); $rawSize = $reader.ReadUInt32(); $raw = $reader.ReadUInt32()
            $sections += @{ address = $address; length = [Math]::Max($virtualSize, $rawSize); raw = $raw }
        }
        function Get-Offset([uint32]$Rva) {
            foreach ($section in $sections) {
                if ($Rva -ge $section.address -and $Rva -lt ($section.address + $section.length)) { return [long]($section.raw + $Rva - $section.address) }
            }
            throw "Invalid PE import RVA: $Path"
        }
        if ($importRva -eq 0) { return }
        $descriptor = Get-Offset $importRva
        for ($index = 0; $index -lt 4096; $index++) {
            $stream.Position = $descriptor + ($index * 20) + 12; $nameRva = $reader.ReadUInt32()
            if ($nameRva -eq 0) { break }
            $stream.Position = Get-Offset $nameRva
            $name = [Text.StringBuilder]::new()
            for ($character = 0; $character -lt 260; $character++) {
                $value = $reader.ReadByte(); if ($value -eq 0) { break }; [void]$name.Append([char]$value)
            }
            $name.ToString()
        }
    } finally { $reader.Dispose(); $stream.Dispose() }
}
if (-not $ValidateOnly) {
    if ([string]::IsNullOrWhiteSpace($SourceDirectory)) {
        if ([string]::IsNullOrWhiteSpace($env:VCToolsRedistDir)) { throw 'MSVC redist source unavailable. Initialize VsDevCmd or pass -SourceDirectory.' }
        $SourceDirectory = Join-Path $env:VCToolsRedistDir 'x64\Microsoft.VC143.CRT'
    }
    $source = (Resolve-Path -LiteralPath $SourceDirectory).Path
    if ($source -notmatch '\\x64\\Microsoft\.VC143\.CRT$' -or $source -match 'debug_nonredist|onecore') { throw 'Use the unmodified release x64 Microsoft.VC143.CRT redistributable folder.' }
    New-Item -ItemType Directory -Path $output -Force | Out-Null
    $entries = @($names | ForEach-Object {
        $path = Join-Path $source $_
        $signature = Get-AuthenticodeSignature -FilePath $path
        if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'O=Microsoft Corporation') { throw "Untrusted Microsoft runtime: $_" }
        [void](Get-PeImports -Path $path -RequireX64)
        Copy-Item -LiteralPath $path -Destination (Join-Path $output $_) -Force
        [ordered]@{ file = $_; sha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash; version = (Get-Item -LiteralPath $path).VersionInfo.FileVersion; signerThumbprint = $signature.SignerCertificate.Thumbprint }
    })
    $notice = @'
Microsoft Visual C++ Runtime, x64 release binaries. Copyright Microsoft Corporation.
These files are copied unmodified from the installed Visual Studio release redist directory.
They are not covered by the Rust dependency licenses. Redistribution is subject to the
applicable Microsoft license and valid Visual Studio entitlement; local packaging does
not establish the distributor's entitlement. Confirm it during required human release review.
Official redistribution list: https://learn.microsoft.com/en-us/visualstudio/releases/2022/redistribution
License terms: https://visualstudio.microsoft.com/license-terms/vs2022-ga-diagnosticbuildtools/
Deployment: https://learn.microsoft.com/en-us/cpp/windows/choosing-a-deployment-method?view=msvc-170
App-local servicing requires rebuilding and distributing updated runtime files with the application.
Windows 10/11 supplies UCRT and operating-system API sets; those system binaries are not copied.
'@
    [IO.File]::WriteAllText((Join-Path $output $noticeName), $notice, [Text.UTF8Encoding]::new($false))
    [ordered]@{ schemaVersion = 1; rid = 'win-x64'; sourceDirectory = $source; files = $entries; noticeFile = $noticeName; noticeSha256 = (Get-FileHash -LiteralPath (Join-Path $output $noticeName)).Hash; redistributionEntitlementConfirmed = $false } | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $manifestPath -Encoding utf8
}
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
if ($manifest.schemaVersion -ne 1 -or $manifest.rid -cne 'win-x64' -or $manifest.noticeFile -cne $noticeName) { throw 'Invalid MSVC runtime manifest.' }
$actualNames = @($manifest.files | ForEach-Object file)
if (@(Compare-Object ($names | Sort-Object) ($actualNames | Sort-Object)).Count -ne 0 -or $actualNames.Count -ne $names.Count) { throw 'MSVC runtime file inventory does not match the supported release set.' }
foreach ($entry in $manifest.files) {
    $path = Join-Path $output $entry.file
    if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -cne $entry.sha256) { throw "MSVC runtime hash mismatch: $($entry.file)" }
    if (-not $SkipBinaryInspection) {
        [void](Get-PeImports -Path $path -RequireX64)
        $signature = Get-AuthenticodeSignature -FilePath $path
        if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Thumbprint -cne $entry.signerThumbprint -or $signature.SignerCertificate.Subject -notmatch 'O=Microsoft Corporation') { throw "MSVC runtime signature mismatch: $($entry.file)" }
    }
}
if ((Get-FileHash -LiteralPath (Join-Path $output $noticeName)).Hash -cne $manifest.noticeSha256) { throw 'MSVC runtime notice hash mismatch.' }
if (-not $SkipBinaryInspection) {
    $closure = @()
    foreach ($library in Get-ChildItem -LiteralPath $output -Recurse -File -Filter '*.dll' | Where-Object { $_.FullName -notmatch '\\x86\\' }) {
        $imports = @(Get-PeImports -Path $library.FullName)
        foreach ($dependency in $imports) {
            if ($dependency -match '^(vcruntime|msvcp|concrt|vccorlib)140.*\.dll$' -and $dependency -notin $names) { throw "Unstaged MSVC dependency $dependency imported by $($library.Name)." }
        }
        $closure += [ordered]@{ file = $library.FullName.Substring($output.TrimEnd('\').Length + 1); sha256 = (Get-FileHash -LiteralPath $library.FullName).Hash; imports = $imports }
    }
    if (-not $ValidateOnly) {
        [ordered]@{ schemaVersion = 1; scope = 'All packaged DLLs except x86 subtree; every MSVC140 dependency resolves to the staged signed x64 release set. Windows/UCRT/API-set dependencies remain supplied by the supported OS.'; modules = $closure } | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $output 'msvc-runtime-imports.json') -Encoding utf8
    }
}
[pscustomobject]@{ ManifestPath = $manifestPath; Files = $names.Count; Rid = 'win-x64' }
