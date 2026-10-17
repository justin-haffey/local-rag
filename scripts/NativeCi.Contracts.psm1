Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-NativeCiRequiredTests {
    @(
        'LocalRag.Host.Tests.LanceDbNativeIntegrationTests.MissingCorruptWrongRidAndAbiPackagesFailBeforeOpeningAnyStore'
        'LocalRag.Host.Tests.LanceDbNativeIntegrationTests.HybridAlphaEndpointsAndEqualVectorScoresHaveStableOrdering'
        'LocalRag.Host.Tests.LanceDbNativeIntegrationTests.RestartReplaceSourceDeleteFiltersAndLexicalTailPreserveContract'
        'LocalRag.Host.Tests.LanceDbNativeIntegrationTests.InvalidPayloadCancellationAndApiDisposalDoNotReleaseLiveHandleCode'
        'LocalRag.Host.Tests.LanceDbNativeIntegrationTests.ManagedAbiPerformsCrudSearchOptimizeOwnershipAndReset'
        'LocalRag.Host.Tests.LanceDbRollbackIntegrationTests.QuiescedCheckpointRestoresMatchingSqliteVectorsAndOwnership'
        'LocalRag.Host.Tests.LanceDbReconciliationLiveIntegrationTests.OverflowRestartRecoveryConvergesRealOnnxAndLanceDbWithoutReembeddingUnchangedFiles'
        'LocalRag.Host.Tests.ManagerHostIntegrationTests.ConfirmedEmbeddedHttpRemoveAndResetPreserveSourceFilesAndUnrelatedState'
        'LocalRag.Host.Tests.ManagerHostIntegrationTests.EmbeddedProviderPauseRestartResumeReconcilesEditsAndDeletesBeforeSearchVisibility'
        'LocalRag.Host.Tests.ManagerHostIntegrationTests.EmbeddedNestedWindowsPathsRemainSearchableAndReconcileEditsAndDeletes'
        'LocalRag.Host.Tests.ActiveWritePauseIntegrationTests.EmbeddedPauseDrainsCommittedUpsertAndResumeRemovesDeletedUnmanifestedVectors'
        'LocalRag.Host.Tests.ActiveWritePauseIntegrationTests.EmbeddedPauseResumeReplacesModifiedUnmanifestedPathWithoutStaleVectors'
        'LocalRag.Host.Tests.ActiveWritePauseIntegrationTests.EmbeddedPauseAfterObsoleteDeleteAndRevertRestoresCommittedContent'
        'LocalRag.Host.Tests.ActiveWritePauseIntegrationTests.EmbeddedPauseAfterRemovedFileDeleteAndRestoreReindexesIdenticalContent'
        'LocalRag.Host.Tests.VectorWriteIntentRecoveryTests.ManifestRollbackRetainsIntentAndAtomicCommitRetiresOnlyPublishedIds'
        'LocalRag.Host.Tests.VectorWriteIntentRecoveryTests.IntentSchemaMigratesAdditivelyRejectsFutureVersionsAndRemovalResetRemainScoped'
        'LocalRag.Host.Tests.VectorWriteIntentRecoveryTests.UnsupportedStateFailsBeforeProviderMutation'
    )
    foreach ($window in @('before-upsert', 'partial-upsert', 'after-upsert', 'after-manifest', 'during-partial-cleanup', 'after-delete-before-acknowledgement', 'after-acknowledgement')) {
        'LocalRag.Host.Tests.VectorWriteIntentRecoveryTests.ReopenedDurableIntentRecoveryIsIdempotentAndPreservesCommittedAndOtherSourceIds(window: "' + $window + '")'
    }
    foreach ($acknowledgementFailure in @('False', 'True')) {
        foreach ($removedFile in @('False', 'True')) {
            "LocalRag.Host.Tests.VectorWriteIntentRecoveryTests.ObsoleteDeleteOrAcknowledgementFailureAfterPublicationRemainsDegradedAndRecovers(acknowledgementFailure: $acknowledgementFailure, removedFile: $removedFile)"
        }
    }
}

function Assert-NativeCiTrx {
    param([Parameter(Mandatory)][string]$Path, [string[]]$RequiredTests = (Get-NativeCiRequiredTests))
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw "Required native TRX is missing: $Path" }
    [xml]$document = Get-Content -LiteralPath $Path -Raw
    $counters = $document.SelectSingleNode('//*[local-name()="ResultSummary"]/*[local-name()="Counters"]')
    $results = @($document.SelectNodes('//*[local-name()="Results"]/*[local-name()="UnitTestResult"]'))
    if ($null -eq $counters -or $results.Count -eq 0) { throw 'Native CI must execute tests; empty TRX is not a pass.' }
    foreach ($name in @('failed', 'error', 'timeout', 'aborted', 'inconclusive', 'notRunnable', 'notExecuted')) {
        if ($counters.GetAttribute($name) -notmatch '^\d+$' -or [int]$counters.GetAttribute($name) -ne 0) {
            throw "Native CI has failed, skipped or unavailable evidence: $name."
        }
    }
    if ([int]$counters.GetAttribute('total') -ne $results.Count -or
        [int]$counters.GetAttribute('executed') -ne $results.Count -or
        [int]$counters.GetAttribute('passed') -ne $results.Count -or
        @($results | Where-Object { $_.GetAttribute('outcome') -ne 'Passed' }).Count -ne 0) {
        throw 'Every selected native CI test must execute and pass.'
    }
    foreach ($test in $RequiredTests) {
        if (@($results | Where-Object { $_.GetAttribute('testName') -eq $test }).Count -ne 1) {
            throw "Required native test did not execute exactly once: $test"
        }
    }
    [pscustomobject]@{ executed = $results.Count; passed = $results.Count; required = $RequiredTests.Count; skipped = 0 }
}

function Assert-NativeCiEvidenceDirectory {
    param([Parameter(Mandatory)][string]$Path)
    $directory = Get-Item -LiteralPath $Path -ErrorAction Stop
    if (-not $directory.PSIsContainer -or ($directory.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw 'Evidence must be a real directory, not a file or reparse point.'
    }
    $entries = @(Get-ChildItem -LiteralPath $directory.FullName -Recurse -Force)
    $files = @($entries | Where-Object { -not $_.PSIsContainer })
    if ($files.Count -eq 0) { throw 'No CI evidence exists; do not upload an empty success artifact.' }
    $utf8 = [Text.UTF8Encoding]::new($false, $true)
    foreach ($entry in $entries) {
        if ($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Reparse points are forbidden in CI evidence: $($entry.Name)" }
        if ($entry.PSIsContainer) { continue }
        if ($entry.Extension -notin @('.json', '.log', '.trx', '.txt')) {
            throw "Binary or unapproved CI evidence type: $($entry.Name)"
        }
        $bytes = [IO.File]::ReadAllBytes($entry.FullName)
        if ($bytes.Length -ge 2 -and $bytes[0] -eq 0x4D -and $bytes[1] -eq 0x5A) { throw 'PE binaries must never enter uploaded CI evidence.' }
        $content = $utf8.GetString($bytes).TrimStart([char]0xFEFF)
        if ($entry.Extension -eq '.json') { $null = $content | ConvertFrom-Json -ErrorAction Stop }
        if ($entry.Extension -eq '.trx') { $null = [xml]$content }
    }
    [pscustomobject]@{ files = $files.Count; binaryUploads = 0; runtimeRedistributionApproved = $false }
}

function Assert-NativeCiModelAssets {
    param([Parameter(Mandatory)][string]$ManifestPath, [Parameter(Mandatory)][string]$ModelDirectory)
    $manifest = Get-Content -LiteralPath $ManifestPath -Raw | ConvertFrom-Json
    $assets = @($manifest.files)
    if ($assets.Count -ne 2 -or @($assets.path | Sort-Object -Unique).Count -ne 2 -or
        'model.onnx' -notin $assets.path -or 'vocab.txt' -notin $assets.path) { throw 'The model gate requires exactly the pinned ONNX model and vocabulary.' }
    foreach ($asset in $assets) {
        if ($asset.sha256 -notmatch '^[A-Fa-f0-9]{64}$' -or $asset.sizeBytes -le 0) { throw 'Model asset identity is incomplete.' }
        $path = Join-Path $ModelDirectory $asset.path
        $item = Get-Item -LiteralPath $path -ErrorAction Stop
        if ($item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -or
            $item.Length -ne $asset.sizeBytes -or (Get-FileHash -LiteralPath $path).Hash -ne $asset.sha256) {
            throw "A required model asset is unavailable or differs from its pinned identity: $($asset.path)"
        }
    }
    [pscustomobject]@{ verifiedAssets = $assets.Count; manifestSha256 = (Get-FileHash -LiteralPath $ManifestPath).Hash }
}

Export-ModuleMember -Function Get-NativeCiRequiredTests, Assert-NativeCiTrx, Assert-NativeCiEvidenceDirectory, Assert-NativeCiModelAssets
