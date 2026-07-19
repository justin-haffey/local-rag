param(
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repositoryRoot "src\LocalRag.Host\LocalRag.Host.csproj"
$output = Join-Path $repositoryRoot "src\LocalRag.Host\bin\win-x64"

dotnet publish $project --configuration $Configuration --runtime win-x64 --self-contained true --output $output
