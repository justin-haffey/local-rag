[CmdletBinding()]
param(
    [string]$ModelDirectory,
    [string]$ManifestPath
)

$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($ModelDirectory)) { $ModelDirectory = Join-Path $env:LOCALAPPDATA 'LocalRag\models\bge-small-en-v1.5' }
if ([string]::IsNullOrWhiteSpace($ManifestPath)) { $ManifestPath = Join-Path $PSScriptRoot 'embedding-model.manifest.json' }
$manifest = Get-Content -Raw -LiteralPath $ManifestPath | ConvertFrom-Json
New-Item -ItemType Directory -Force -Path $ModelDirectory | Out-Null

foreach ($asset in $manifest.files) {
    $destination = Join-Path $ModelDirectory $asset.path
    $temporary = Join-Path ([System.IO.Path]::GetTempPath()) ("LocalRag-$([guid]::NewGuid().ToString('N')).download")
    if (Test-Path -LiteralPath $destination) {
        $existingHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $destination).Hash
        if ($existingHash -eq $asset.sha256 -and (Get-Item -LiteralPath $destination).Length -eq $asset.sizeBytes) {
            Write-Host "Verified $($asset.path)"
            continue
        }
        Remove-Item -LiteralPath $destination -Force
    }

    Remove-Item -LiteralPath $temporary -Force -ErrorAction SilentlyContinue
    & curl.exe --fail --location --retry 3 --silent --show-error --output $temporary $asset.url
    if ($LASTEXITCODE -ne 0) {
        Remove-Item -LiteralPath $temporary -Force -ErrorAction SilentlyContinue
        throw "Download failed for $($asset.path)."
    }
    $downloaded = Get-Item -LiteralPath $temporary
    $actualHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $temporary).Hash
    if ($downloaded.Length -ne $asset.sizeBytes -or $actualHash -ne $asset.sha256) {
        Remove-Item -LiteralPath $temporary -Force -ErrorAction SilentlyContinue
        throw "Verification failed for $($asset.path). Expected $($asset.sizeBytes) bytes and SHA-256 $($asset.sha256); received $($downloaded.Length) bytes and $actualHash."
    }
    Move-Item -LiteralPath $temporary -Destination $destination
    Write-Host "Installed and verified $($asset.path)"
}

Write-Host "Verified ONNX profile $($manifest.profileId) in $ModelDirectory"
