[CmdletBinding()]
param([string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Import-Module (Join-Path $PSScriptRoot 'NativeCi.Contracts.psm1') -Force
$repositoryRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) { $OutputDirectory = Join-Path ([IO.Path]::GetTempPath()) ('localrag-ci-contract-' + [guid]::NewGuid().ToString('N')) }
if (Test-Path -LiteralPath $OutputDirectory) { throw 'Use a fresh CI contract fixture directory.' }
New-Item -ItemType Directory -Path $OutputDirectory | Out-Null
$cases = [Collections.Generic.List[object]]::new()
$completed = $false

function Require([bool]$Condition, [string]$Name) {
    if (-not $Condition) { throw "CI contract failed: $Name" }
    $cases.Add([ordered]@{ name = $Name; passed = $true })
}
function Require-Rejection([scriptblock]$Operation, [string]$Name) {
    $rejected = $false
    try { & $Operation | Out-Null } catch { $rejected = $true }
    Require $rejected $Name
}
function New-TrxFixture([string]$Name, [string]$Mode = 'pass') {
    $tests = @(Get-NativeCiRequiredTests)
    if ($Mode -eq 'zero') { $tests = @() }
    if ($Mode -eq 'missing') { $tests = @($tests | Select-Object -Skip 1) }
    $total = $tests.Count; $skipped = if ($Mode -eq 'skip') { 1 } else { 0 }; $failed = if ($Mode -eq 'fail') { 1 } else { 0 }
    $rows = @($tests | ForEach-Object { '<UnitTestResult testName="' + [Security.SecurityElement]::Escape($_) + '" outcome="Passed" />' }) -join ''
    $xml = '<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010"><Results>' + $rows + '</Results><ResultSummary><Counters total="' + $total + '" executed="' + ($total - $skipped) + '" passed="' + ($total - $skipped - $failed) + '" failed="' + $failed + '" error="0" timeout="0" aborted="0" inconclusive="0" notRunnable="0" notExecuted="' + $skipped + '" /></ResultSummary></TestRun>'
    $path = Join-Path $OutputDirectory "$Name.trx"
    [IO.File]::WriteAllText($path, $xml)
    $path
}

try {
    foreach ($name in @('NativeCi.Contracts.psm1', 'Invoke-WindowsNativeCi.ps1', 'Test-WindowsNativeCiContract.ps1')) {
        $tokens = $null; $errors = $null
        $null = [Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot $name), [ref]$tokens, [ref]$errors)
        Require ($errors.Count -eq 0) "PowerShell syntax: $name"
    }
    $pass = New-TrxFixture 'pass'
    $result = Assert-NativeCiTrx -Path $pass
    $requiredCount = @(Get-NativeCiRequiredTests).Count
    Require ($requiredCount -eq 28 -and $result.required -eq $requiredCount -and $result.executed -eq $requiredCount -and $result.skipped -eq 0) 'Every required native/ONNX test passes'
    foreach ($mode in @('zero', 'skip', 'missing', 'fail')) {
        $path = New-TrxFixture $mode $mode
        Require-Rejection { Assert-NativeCiTrx -Path $path } "TRX refuses $mode evidence"
    }
    Require-Rejection { Assert-NativeCiTrx -Path (Join-Path $OutputDirectory 'absent.trx') } 'TRX missing is failure'
    $safe = Join-Path $OutputDirectory 'safe'; New-Item -ItemType Directory -Path $safe | Out-Null
    [IO.File]::WriteAllText((Join-Path $safe 'summary.json'), '{"success":false,"runtimeRedistributionApproved":false}')
    $guard = Assert-NativeCiEvidenceDirectory -Path $safe
    Require ($guard.files -eq 1 -and $guard.binaryUploads -eq 0) 'Failure metadata can be retained without claiming CI success'
    $bad = Join-Path $safe 'vcruntime140.dll'; [IO.File]::WriteAllBytes($bad, [byte[]](1, 2, 3))
    Require-Rejection { Assert-NativeCiEvidenceDirectory -Path $safe } 'Runtime DLL cannot be uploaded'
    # Move the intentionally rejected sample into a separate retained fixture, without deleting evidence.
    Move-Item -LiteralPath $bad -Destination (Join-Path $OutputDirectory 'rejected-runtime.dll')
    $masked = Join-Path $safe 'misnamed-runtime.txt'; [IO.File]::WriteAllBytes($masked, [byte[]](0x4D, 0x5A, 0, 0))
    Require-Rejection { Assert-NativeCiEvidenceDirectory -Path $safe } 'Renaming a PE to text cannot bypass upload guard'
    Move-Item -LiteralPath $masked -Destination (Join-Path $OutputDirectory 'rejected-runtime.txt')
    $invalid = Join-Path $safe 'invalid.log'; [IO.File]::WriteAllBytes($invalid, [byte[]](0xFF, 0xFE, 0xFF))
    Require-Rejection { Assert-NativeCiEvidenceDirectory -Path $safe } 'Non-UTF8 bytes cannot be uploaded as logs'
    Move-Item -LiteralPath $invalid -Destination (Join-Path $OutputDirectory 'rejected-encoding.log')
    $empty = Join-Path $OutputDirectory 'empty'; New-Item -ItemType Directory -Path $empty | Out-Null
    Require-Rejection { Assert-NativeCiEvidenceDirectory -Path $empty } 'No empty evidence success artifact'

    $modelRoot = Join-Path $OutputDirectory 'model'; New-Item -ItemType Directory -Path $modelRoot | Out-Null
    foreach ($name in @('model.onnx', 'vocab.txt')) { [IO.File]::WriteAllText((Join-Path $modelRoot $name), "fixture $name") }
    $modelManifest = Join-Path $OutputDirectory 'model-fixture.json'
    $modelFiles = @(Get-ChildItem -LiteralPath $modelRoot -File | ForEach-Object { @{ path = $_.Name; sha256 = (Get-FileHash -LiteralPath $_.FullName).Hash; sizeBytes = $_.Length } })
    @{ files = $modelFiles } | ConvertTo-Json -Depth 5 | Set-Content $modelManifest
    Require ((Assert-NativeCiModelAssets -ManifestPath $modelManifest -ModelDirectory $modelRoot).verifiedAssets -eq 2) 'Both model identities are verified'
    [IO.File]::WriteAllText((Join-Path $modelRoot 'vocab.txt'), 'changed vocabulary')
    Require-Rejection { Assert-NativeCiModelAssets -ManifestPath $modelManifest -ModelDirectory $modelRoot } 'Changed model asset fails before inference'
    Move-Item -LiteralPath (Join-Path $modelRoot 'model.onnx') -Destination (Join-Path $OutputDirectory 'retained-model-fixture.onnx')
    Require-Rejection { Assert-NativeCiModelAssets -ManifestPath $modelManifest -ModelDirectory $modelRoot } 'Missing model asset fails instead of skipping'
    [IO.File]::WriteAllText($modelManifest, '{"files":[]}')
    Require-Rejection { Assert-NativeCiModelAssets -ManifestPath $modelManifest -ModelDirectory $modelRoot } 'Empty model manifest cannot become a green gate'

    $workflow = Get-Content -LiteralPath (Join-Path $repositoryRoot '.github/workflows/windows-native.yml') -Raw
    $actions = [regex]::Matches($workflow, 'uses:\s+([^\s#]+)')
    Require ($actions.Count -eq 3 -and @($actions | Where-Object { $_.Groups[1].Value -notmatch '^actions/[a-z-]+@[a-f0-9]{40}$' }).Count -eq 0) 'Every action uses an official full commit pin'
    Require ($workflow.Contains('workflow_dispatch:') -and -not ($workflow -match '(?m)^\s+(pull_request_target|workflow_run|push|schedule):')) 'Workflow requires explicit dispatch'
    Require ($workflow.Contains('persist-credentials: false') -and $workflow.Contains('contents: read') -and -not $workflow.Contains('write-all')) 'Read-only token without persisted checkout credentials'
    Require ($workflow.Contains("steps.evidence-safety.outcome == 'success'") -and $workflow.Contains('path: artifacts/ci/windows-native/evidence/') -and $workflow.Contains('if-no-files-found: error')) 'Upload requires independent evidence safety and exact evidence directory'
    Require (-not $workflow.Contains('continue-on-error:') -and -not $workflow.Contains('Skip') -and -not $workflow.Contains('secrets.')) 'No false-green skips, credentials or ignored gate failures'
    $tools = Get-Content -LiteralPath (Join-Path $repositoryRoot '.github/ci/native-tools.json') -Raw | ConvertFrom-Json
    Require ($tools.rustVersion -eq '1.91.0' -and $tools.target -eq 'x86_64-pc-windows-msvc' -and $tools.protoc.sha256 -match '^[A-F0-9]{64}$' -and $tools.cargoAudit.sha256 -match '^[A-F0-9]{64}$') 'Exact native tool pins and archive digests'
    Require ($tools.protoc.url -like 'https://github.com/protocolbuffers/protobuf/releases/download/v35.0/*' -and $tools.cargoAudit.url -like 'https://github.com/rustsec/rustsec/releases/download/cargo-audit/v0.22.2/*') 'Downloads use the recorded upstream releases'

    # Exercise the exact command wrapper without provisioning or invoking a compiler.
    $tokens = $null; $errors = $null
    $ast = [Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot 'Invoke-WindowsNativeCi.ps1'), [ref]$tokens, [ref]$errors)
    $function = $ast.Find({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Invoke-CiCommand' }, $true)
    . ([scriptblock]::Create($function.Extent.Text))
    $toolFunction = $ast.Find({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Get-CiTool' }, $true)
    . ([scriptblock]::Create($toolFunction.Extent.Text))
    $privateRoot = Join-Path $OutputDirectory 'tool-fixture'; New-Item -ItemType Directory -Path $privateRoot | Out-Null
    $hashMismatch = & {
        # Stub network I/O only; the real production download verifier must reject these bytes before extraction.
        function Invoke-WebRequest {
            param($Uri, $OutFile, $MaximumRetryCount, $ConnectionTimeoutSeconds, $OperationTimeoutSeconds)
            [IO.File]::WriteAllText($OutFile, 'not the pinned archive')
        }
        try { Get-CiTool ([pscustomobject]@{ url = $tools.protoc.url; sha256 = ('0' * 64) }) 'mismatched' 'protoc.exe'; $null }
        catch { $_.Exception.Message }
    }
    Require ($hashMismatch -eq 'Downloaded mismatched hash differs from the checked-in tool manifest.') 'Actual tool verifier refuses changed archive bytes'
    Require (-not (Test-Path -LiteralPath (Join-Path $privateRoot 'mismatched'))) 'Checksum refusal occurs before archive extraction'
    Require-Rejection { Get-CiTool ([pscustomobject]@{ url = 'https://example.invalid/tool.zip'; sha256 = ('0' * 64) }) 'untrusted' 'tool.exe' } 'Unapproved tool origin fails before network I/O'
    $evidence = $safe; $commands = [Collections.Generic.List[object]]::new()
    $shell = (Get-Command pwsh -ErrorAction Stop).Source
    Require-Rejection { Invoke-CiCommand 'nonzero-tool' $shell @('-NoProfile', '-Command', 'exit 7') } 'Nonzero tool exit fails the job'
    Require ($commands[0].exitCode -eq 7) 'Actual failing exit is recorded'
    $sample = Join-Path $OutputDirectory 'parameters.ps1'
    [IO.File]::WriteAllText($sample, 'param([string]$Marker) if ($Marker -ne "literal value") { throw "Bad script parameter binding" }; Write-Output $Marker')
    Invoke-CiCommand -Name 'script-parameters' -Command $sample -ScriptParameters @{ Marker = 'literal value' }
    Require ((Get-Content (Join-Path $safe 'script-parameters.log') -Raw).Trim() -eq 'literal value') 'PowerShell scripts use named hashtable binding'
    $guardCommand = '$env:GITHUB_ACTIONS="false"; & ''' + (Join-Path $PSScriptRoot 'Invoke-WindowsNativeCi.ps1').Replace("'", "''") + ''''
    Require-Rejection { Invoke-CiCommand 'local-provisioning-refused' $shell @('-NoProfile', '-Command', $guardCommand) } 'Actual provisioning entry point refuses local execution'
    Require ((Get-Content (Join-Path $safe 'local-provisioning-refused.log') -Raw).Contains('only in the manual GitHub-hosted Windows x64 job')) 'Local refusal occurs before provisioning'
    $completed = $true
    Write-Host "Windows native CI contracts passed: $($cases.Count). No native build, model inference or remote CI execution."
} finally {
    [ordered]@{ completedUtc = [DateTime]::UtcNow.ToString('o'); completed = $completed; cases = $cases; passed = $cases.Count; boundary = 'Local fixture/static validation only. Does not constitute an actual CI run or native rebuild.'; retainedFixtureDirectory = [IO.Path]::GetFullPath($OutputDirectory) } | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $OutputDirectory 'contract-results.json') -Encoding utf8
}
if ($completed) { exit 0 }
