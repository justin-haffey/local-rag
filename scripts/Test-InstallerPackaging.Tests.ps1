$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$validator = Join-Path $PSScriptRoot 'Test-LanceDbNativePackage.ps1'
$fixtureRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('local-rag-native-package-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $fixtureRoot | Out-Null

try {
    $libraryPath = Join-Path $fixtureRoot 'localrag_lancedb.dll'
    $licensePath = Join-Path $fixtureRoot 'LICENSE-lancedb.txt'
    [System.IO.File]::WriteAllBytes($libraryPath, [byte[]](1, 2, 3, 4))
    [System.IO.File]::WriteAllText($licensePath, 'fixture license')
    $libraryHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $libraryPath).Hash
    $licenseHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $licensePath).Hash
    $manifestPath = Join-Path $fixtureRoot 'lancedb-native.manifest.json'
    $manifest = [ordered]@{
        schemaVersion = 1
        abiVersion = 1
        rid = 'win-x64'
        libraryFile = 'localrag_lancedb.dll'
        sha256 = $libraryHash
        requiredExports = @(
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
        licenses = @([ordered]@{ file = 'LICENSE-lancedb.txt'; sha256 = $licenseHash })
    }
    $manifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $manifestPath -Encoding utf8
    $runtimeNames = @('concrt140.dll', 'msvcp140.dll', 'msvcp140_1.dll', 'msvcp140_2.dll', 'msvcp140_atomic_wait.dll', 'msvcp140_codecvt_ids.dll', 'vccorlib140.dll', 'vcruntime140.dll', 'vcruntime140_1.dll', 'vcruntime140_threads.dll')
    $runtimeFiles = @($runtimeNames | ForEach-Object {
        $path = Join-Path $fixtureRoot $_
        [IO.File]::WriteAllBytes($path, [byte[]](5, 6, 7, 8))
        [ordered]@{ file = $_; sha256 = (Get-FileHash -LiteralPath $path).Hash }
    })
    $runtimeNotice = Join-Path $fixtureRoot 'THIRD-PARTY-NOTICES-msvc.txt'
    [IO.File]::WriteAllText($runtimeNotice, 'fixture notice')
    [ordered]@{ schemaVersion = 1; rid = 'win-x64'; files = $runtimeFiles; noticeFile = 'THIRD-PARTY-NOTICES-msvc.txt'; noticeSha256 = (Get-FileHash -LiteralPath $runtimeNotice).Hash } | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $fixtureRoot 'msvc-runtime.manifest.json') -Encoding utf8

    & $validator -HostPublishDirectory $fixtureRoot -SkipBinaryInspection | Out-Null
    $runtimeFault = Join-Path $fixtureRoot 'vcruntime140.dll'
    [IO.File]::WriteAllBytes($runtimeFault, [byte[]](9, 9, 9, 9))
    try {
        & $validator -HostPublishDirectory $fixtureRoot -SkipBinaryInspection | Out-Null
        throw 'Expected MSVC runtime hash mismatch to fail.'
    } catch {
        if ($_.Exception.Message -notmatch 'MSVC runtime hash mismatch') { throw }
    }
    [IO.File]::WriteAllBytes($runtimeFault, [byte[]](5, 6, 7, 8))

    $manifest.rid = 'linux-x64'
    $manifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $manifestPath -Encoding utf8
    try {
        & $validator -HostPublishDirectory $fixtureRoot -SkipBinaryInspection | Out-Null
        throw 'Expected wrong-RID native package validation to fail.'
    } catch {
        if ($_.Exception.Message -notmatch "RID must be exactly 'win-x64'") { throw }
    }

    $manifest.rid = 'win-x64'
    $manifest.sha256 = '0' * 64
    $manifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $manifestPath -Encoding utf8
    try {
        & $validator -HostPublishDirectory $fixtureRoot -SkipBinaryInspection | Out-Null
        throw 'Expected native library hash validation to fail.'
    } catch {
        if ($_.Exception.Message -notmatch 'hash mismatch') { throw }
    }

    Write-Host 'Installer native package fixture tests passed.'
} finally {
    $resolvedFixture = [System.IO.Path]::GetFullPath($fixtureRoot)
    $resolvedTemp = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    if (-not $resolvedFixture.StartsWith($resolvedTemp, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to remove fixture outside the temporary directory: $resolvedFixture"
    }
    Remove-Item -LiteralPath $resolvedFixture -Recurse -Force
}
