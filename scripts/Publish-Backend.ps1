param(
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repositoryRoot "src\LocalRag.Host\LocalRag.Host.csproj"
$output = Join-Path $repositoryRoot "vscode-extension\bin\win32-x64"

dotnet publish $project --configuration $Configuration --runtime win-x64 --self-contained true --output $output
