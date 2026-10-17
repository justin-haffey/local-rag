[CmdletBinding()]
param([Parameter(Mandatory)][string]$HostPublishDirectory)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath($HostPublishDirectory)
foreach ($relative in @('LocalRag.Host.dll','Start-LocalRag.ps1','LocalRag.Launcher.psm1','wwwroot/manager.js','wwwroot/manager-links.js','wwwroot/manager.css')) {
    $file = Join-Path $root $relative
    if (-not (Test-Path -LiteralPath $file -PathType Leaf) -or (Get-Item -LiteralPath $file).Length -eq 0) { throw "Manager package asset is missing or empty: $relative" }
}
# Razor's SDK compiles the page into the host assembly; raw .cshtml files are not required in publish output.
$assemblyBytes = [IO.File]::ReadAllBytes((Join-Path $root 'LocalRag.Host.dll'))
$strings = [Text.Encoding]::UTF8.GetString($assemblyBytes)
if (-not $strings.Contains('Pages_Manage')) { throw 'Published host does not contain the compiled Manage Razor page.' }
$launcher = Get-Content -LiteralPath (Join-Path $root 'Start-LocalRag.ps1') -Raw
foreach ($contract in @('LocalRag.Start.', '-WindowStyle Hidden', '/manage/tickets', '/health/live', 'client-token', 'management-token')) {
    if (-not $launcher.Contains($contract)) { throw "Packaged launcher is missing lifecycle contract: $contract" }
}
$parseErrors = $null; $parseTokens = $null
[Management.Automation.Language.Parser]::ParseFile((Join-Path $root 'Start-LocalRag.ps1'), [ref]$parseTokens, [ref]$parseErrors) | Out-Null
if ($parseErrors.Count -gt 0) { throw 'Packaged launcher contains PowerShell parse errors.' }
Write-Host 'Manager package assets, compiled Razor page and shared launcher contracts validated.'
