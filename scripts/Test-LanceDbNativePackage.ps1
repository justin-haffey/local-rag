[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$HostPublishDirectory,
    [switch]$SkipBinaryInspection
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$publishDirectory = [System.IO.Path]::GetFullPath($HostPublishDirectory)
$manifestPath = Join-Path $publishDirectory 'lancedb-native.manifest.json'
if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
    throw "LanceDB native package manifest is missing: $manifestPath"
}

try {
    $manifest = Get-Content -Raw -LiteralPath $manifestPath | ConvertFrom-Json
} catch {
    throw "LanceDB native package manifest is not valid JSON: $($_.Exception.Message)"
}

$expectedProperties = @('schemaVersion', 'abiVersion', 'rid', 'libraryFile', 'sha256', 'requiredExports', 'licenses')
$actualProperties = @($manifest.PSObject.Properties.Name)
$unexpectedProperties = @($actualProperties | Where-Object { $_ -notin $expectedProperties })
$missingProperties = @($expectedProperties | Where-Object { $_ -notin $actualProperties })
if ($unexpectedProperties.Count -gt 0 -or $missingProperties.Count -gt 0) {
    throw "LanceDB native manifest fields are invalid. Missing: $($missingProperties -join ', '); unexpected: $($unexpectedProperties -join ', ')."
}
if ($manifest.schemaVersion -ne 1) { throw "Unsupported LanceDB native manifest schemaVersion '$($manifest.schemaVersion)'." }
if ($manifest.abiVersion -ne 1) { throw "Unsupported LanceDB native ABI version '$($manifest.abiVersion)'." }
if ($manifest.rid -cne 'win-x64') { throw "LanceDB native package RID must be exactly 'win-x64'; found '$($manifest.rid)'." }
if ($manifest.libraryFile -cne 'localrag_lancedb.dll') { throw "LanceDB native libraryFile must be exactly 'localrag_lancedb.dll'." }
if ([string]$manifest.sha256 -cnotmatch '^[0-9A-Fa-f]{64}$') { throw 'LanceDB native library sha256 must contain exactly 64 hexadecimal characters.' }

$exports = @($manifest.requiredExports)
if ($exports.Count -eq 0 -or @($exports | Where-Object { $_ -isnot [string] -or [string]::IsNullOrWhiteSpace($_) }).Count -gt 0) {
    throw 'LanceDB native requiredExports must be a non-empty array of export names.'
}
if (@($exports | Select-Object -Unique).Count -ne $exports.Count) {
    throw 'LanceDB native requiredExports must not contain duplicates.'
}
$expectedExports = @(
    'lrag_abi_negotiate',
    'lrag_db_open',
    'lrag_db_close',
    'lrag_cancel_new',
    'lrag_cancel_request',
    'lrag_cancel_release',
    'lrag_db_ensure_ready',
    'lrag_db_upsert_ipc',
    'lrag_db_delete_chunks',
    'lrag_db_delete_source',
    'lrag_db_search',
    'lrag_db_verify_ownership',
    'lrag_db_reset_owned',
    'lrag_db_optimize',
    'lrag_buffer_data',
    'lrag_buffer_len',
    'lrag_buffer_release',
    'lrag_error_code',
    'lrag_error_message',
    'lrag_error_message_len',
    'lrag_error_release'
)
$missingExports = @($expectedExports | Where-Object { $_ -notin $exports })
$unexpectedExports = @($exports | Where-Object { $_ -notin $expectedExports })
if ($missingExports.Count -gt 0 -or $unexpectedExports.Count -gt 0) {
    throw "LanceDB native requiredExports do not match ABI v1. Missing: $($missingExports -join ', '); unexpected: $($unexpectedExports -join ', ')."
}

$libraryPath = Join-Path $publishDirectory $manifest.libraryFile
if (-not (Test-Path -LiteralPath $libraryPath -PathType Leaf)) {
    throw "LanceDB native library is missing: $libraryPath"
}
$actualLibraryHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $libraryPath).Hash
if ($actualLibraryHash -cne ([string]$manifest.sha256).ToUpperInvariant()) {
    throw "LanceDB native library hash mismatch. Expected $($manifest.sha256); received $actualLibraryHash."
}

if (-not $SkipBinaryInspection) {
    $stream = [System.IO.File]::OpenRead($libraryPath)
    $reader = [System.IO.BinaryReader]::new($stream)
    try {
        if ($reader.ReadUInt16() -ne 0x5A4D) { throw 'LanceDB native library is not a PE image.' }
        $stream.Position = 0x3C
        $peOffset = $reader.ReadInt32()
        if ($peOffset -lt 0x40 -or $peOffset -gt ($stream.Length - 6)) { throw 'LanceDB native PE header offset is invalid.' }
        $stream.Position = $peOffset
        if ($reader.ReadUInt32() -ne 0x00004550) { throw 'LanceDB native PE signature is invalid.' }
        if ($reader.ReadUInt16() -ne 0x8664) { throw 'LanceDB native library must target the x64 PE machine.' }
    } finally {
        $reader.Dispose()
        $stream.Dispose()
    }

    if ($null -eq ('LocalRagNativePackageMethods' -as [type])) {
        Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class LocalRagNativePackageMethods
{
    [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr LoadLibraryEx(string path, IntPtr file, uint flags);
    [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Ansi)]
    public static extern IntPtr GetProcAddress(IntPtr module, string name);
    [DllImport("kernel32", SetLastError = true)]
    public static extern bool FreeLibrary(IntPtr module);
}
'@
    }
    $module = [LocalRagNativePackageMethods]::LoadLibraryEx($libraryPath, [IntPtr]::Zero, 1)
    if ($module -eq [IntPtr]::Zero) {
        throw "LanceDB native library could not be inspected (Win32 error $([Runtime.InteropServices.Marshal]::GetLastWin32Error()))."
    }
    try {
        $missingBinaryExports = @($expectedExports | Where-Object {
            [LocalRagNativePackageMethods]::GetProcAddress($module, $_) -eq [IntPtr]::Zero
        })
        if ($missingBinaryExports.Count -gt 0) {
            throw "LanceDB native library is missing ABI exports: $($missingBinaryExports -join ', ')."
        }
    } finally {
        [void][LocalRagNativePackageMethods]::FreeLibrary($module)
    }
}

$licenses = @($manifest.licenses)
if ($licenses.Count -eq 0) { throw 'LanceDB native manifest must declare at least one license file.' }
foreach ($license in $licenses) {
    if ($license -is [string]) {
        $licenseFile = [string]$license
        $licenseHash = $null
    } else {
        $licenseProperties = @($license.PSObject.Properties.Name)
        if (@($licenseProperties).Count -ne 2 -or 'file' -notin $licenseProperties -or 'sha256' -notin $licenseProperties) {
            throw 'Each LanceDB native license object must contain exactly file and sha256.'
        }
        $licenseFile = [string]$license.file
        $licenseHash = [string]$license.sha256
        if ($licenseHash -cnotmatch '^[0-9A-Fa-f]{64}$') { throw "License hash for '$licenseFile' is invalid." }
    }
    if ([string]::IsNullOrWhiteSpace($licenseFile) -or [System.IO.Path]::IsPathRooted($licenseFile) -or $licenseFile.Contains('..')) {
        throw "LanceDB native license path '$licenseFile' must be a safe relative path."
    }
    $licensePath = Join-Path $publishDirectory $licenseFile
    if (-not (Test-Path -LiteralPath $licensePath -PathType Leaf)) { throw "Declared LanceDB native license file is missing: $licenseFile" }
    if ($null -ne $licenseHash) {
        $actualLicenseHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $licensePath).Hash
        if ($actualLicenseHash -cne $licenseHash.ToUpperInvariant()) { throw "License hash mismatch for '$licenseFile'." }
    }
}

& (Join-Path $PSScriptRoot 'Stage-MsvcRuntime.ps1') -OutputDirectory $publishDirectory -ValidateOnly -SkipBinaryInspection:$SkipBinaryInspection | Out-Null

[pscustomobject]@{
    ManifestPath = $manifestPath
    LibraryPath = $libraryPath
    AbiVersion = 1
    Rid = 'win-x64'
    RequiredExports = $exports
}
