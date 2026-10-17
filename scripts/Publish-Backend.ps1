param(
    [string]$Configuration = "Release",
    [string]$OutputDirectory,
    [string]$CargoPath,
    [string]$ProtocPath,
    [string]$VsDevCmdPath
)

$ErrorActionPreference = "Stop"
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repositoryRoot "src\LocalRag.Host\LocalRag.Host.csproj"
$output = if ([string]::IsNullOrWhiteSpace($OutputDirectory)) { Join-Path $repositoryRoot "src\LocalRag.Host\bin\win-x64" } else { [IO.Path]::GetFullPath($OutputDirectory) }
New-Item -ItemType Directory -Force -Path $output | Out-Null

dotnet publish $project --configuration $Configuration --runtime win-x64 --self-contained true --output $output
if ($LASTEXITCODE -ne 0) { throw "Backend publish failed with exit code $LASTEXITCODE." }

& (Join-Path $PSScriptRoot 'Test-ManagerPackage.ps1') -HostPublishDirectory $output | Out-Host

$nativeArguments = @{
    Configuration = $Configuration
    OutputDirectory = $output
}
if (-not [string]::IsNullOrWhiteSpace($CargoPath)) { $nativeArguments.CargoPath = $CargoPath }
if (-not [string]::IsNullOrWhiteSpace($ProtocPath)) { $nativeArguments.ProtocPath = $ProtocPath }
if (-not [string]::IsNullOrWhiteSpace($VsDevCmdPath)) { $nativeArguments.VsDevCmdPath = $VsDevCmdPath }
& (Join-Path $PSScriptRoot 'Build-LanceDbNative.ps1') @nativeArguments
if ($LASTEXITCODE -ne 0) { throw "LanceDB native packaging failed with exit code $LASTEXITCODE." }
