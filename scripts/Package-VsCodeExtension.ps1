[CmdletBinding()]
param(
    [string]$OutputPath,
    [switch]$SkipDependencyRestore,
    [switch]$SkipTests,
    [switch]$SkipInstall
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Resolve-NativeCommand {
    param([Parameter(Mandatory)][string[]]$Names)

    foreach ($name in $Names) {
        $command = Get-Command $name -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($null -ne $command) {
            return $command.Source
        }
    }

    throw "Required command was not found: $($Names -join ', ')."
}

function Invoke-NativeCommand {
    param(
        [Parameter(Mandatory)][string]$Command,
        [Parameter(Mandatory)][string[]]$Arguments
    )

    Write-Host "> $Command $($Arguments -join ' ')"
    & $Command @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "Command failed with exit code ${LASTEXITCODE}: $Command $($Arguments -join ' ')"
    }
}

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$extensionDirectory = Join-Path $repositoryRoot 'src\vscode-extension'
$packageJsonPath = Join-Path $extensionDirectory 'package.json'

if (-not (Test-Path -LiteralPath $packageJsonPath -PathType Leaf)) {
    throw "VS Code extension package.json was not found at $packageJsonPath."
}

$packageJson = Get-Content -Raw -LiteralPath $packageJsonPath | ConvertFrom-Json

if ($packageJson.name -notmatch '^[a-z0-9][a-z0-9-]*$') {
    throw "package.json name '$($packageJson.name)' must be lowercase and URL-safe."
}
if ($packageJson.version -notmatch '^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$') {
    throw "package.json version '$($packageJson.version)' must be a semantic version."
}
if ($packageJson.publisher -notmatch '^[A-Za-z0-9][A-Za-z0-9-]*$') {
    throw "package.json publisher '$($packageJson.publisher)' must contain only letters, numbers, and hyphens."
}
if ([string]::IsNullOrWhiteSpace($packageJson.engines.vscode)) {
    throw 'package.json must declare engines.vscode.'
}
if ([string]::IsNullOrWhiteSpace($packageJson.main) -and [string]::IsNullOrWhiteSpace($packageJson.browser)) {
    throw 'package.json must declare either main or browser.'
}
if (-not ($packageJson.scripts.PSObject.Properties.Name -contains 'vscode:prepublish')) {
    throw 'package.json must define a vscode:prepublish script.'
}

$nodeCommand = Resolve-NativeCommand -Names @('node.exe', 'node')
$npmCommand = Resolve-NativeCommand -Names @('npm.cmd', 'npm')
$nodeVersionText = (& $nodeCommand --version).Trim().TrimStart('v')
if ($LASTEXITCODE -ne 0) {
    throw 'Unable to determine the installed Node.js version.'
}
$nodeMajorVersion = [int]($nodeVersionText.Split('.')[0])
if ($nodeMajorVersion -lt 20) {
    throw "@vscode/vsce requires Node.js 20 or later; found $nodeVersionText."
}

if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $artifactDirectory = Join-Path $repositoryRoot 'artifacts'
    $OutputPath = Join-Path $artifactDirectory "$($packageJson.name)-$($packageJson.version).vsix"
} elseif (-not [System.IO.Path]::IsPathRooted($OutputPath)) {
    $OutputPath = Join-Path $repositoryRoot $OutputPath
}
$OutputPath = [System.IO.Path]::GetFullPath($OutputPath)
$outputDirectory = Split-Path -Parent $OutputPath
New-Item -ItemType Directory -Force -Path $outputDirectory | Out-Null

Push-Location $extensionDirectory
try {
    if (-not $SkipDependencyRestore) {
        Invoke-NativeCommand -Command $npmCommand -Arguments @('ci')
    }

    Invoke-NativeCommand -Command $npmCommand -Arguments @('run', 'compile')

    if (-not $SkipTests) {
        if ($packageJson.scripts.PSObject.Properties.Name -contains 'lint') {
            Invoke-NativeCommand -Command $npmCommand -Arguments @('run', 'lint')
        }
        if ($packageJson.scripts.PSObject.Properties.Name -contains 'test') {
            Invoke-NativeCommand -Command $npmCommand -Arguments @('test')
        } else {
            Write-Host 'No npm test script is configured; skipping extension tests.'
        }
    }

    $entryPoint = if (-not [string]::IsNullOrWhiteSpace($packageJson.main)) { $packageJson.main } else { $packageJson.browser }
    $entryPointPath = Join-Path $extensionDirectory $entryPoint
    if (-not (Test-Path -LiteralPath $entryPointPath -PathType Leaf)) {
        throw "Compiled extension entry point was not found at $entryPointPath."
    }

    $vsceCommand = Join-Path $extensionDirectory 'node_modules\.bin\vsce.cmd'
    if (-not (Test-Path -LiteralPath $vsceCommand -PathType Leaf)) {
        throw "Local @vscode/vsce was not found. Run npm ci or omit -SkipDependencyRestore."
    }

    Invoke-NativeCommand -Command $vsceCommand -Arguments @('ls')
    Invoke-NativeCommand -Command $vsceCommand -Arguments @('package', '--out', $OutputPath)
} finally {
    Pop-Location
}

if (-not (Test-Path -LiteralPath $OutputPath -PathType Leaf)) {
    throw "VSIX packaging completed without creating $OutputPath."
}

$artifact = Get-Item -LiteralPath $OutputPath
Write-Host "Created $($artifact.FullName) ($($artifact.Length) bytes)."

if (-not $SkipInstall) {
    $codeCommand = Resolve-NativeCommand -Names @('code.cmd', 'code.exe', 'code')
    Invoke-NativeCommand -Command $codeCommand -Arguments @('--install-extension', $artifact.FullName, '--force')

    $extensionId = "$($packageJson.publisher).$($packageJson.name)"
    $installed = $false
    for ($attempt = 0; $attempt -lt 10; $attempt++) {
        $installedExtensions = & $codeCommand --list-extensions --show-versions
        if ($LASTEXITCODE -ne 0) {
            throw 'VS Code could not list installed extensions after installation.'
        }
        if ($installedExtensions -match "(?im)^$([regex]::Escape($extensionId))@$([regex]::Escape($packageJson.version))$") {
            $installed = $true
            break
        }
        Start-Sleep -Milliseconds 500
    }
    if (-not $installed) {
        throw "VS Code did not report $extensionId@$($packageJson.version) after installation."
    }

    Write-Host "Installed and verified $extensionId@$($packageJson.version)."
} else {
    Write-Host 'Skipped VS Code installation because -SkipInstall was specified.'
}
