[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$NativePackageDirectory,
    [Parameter(Mandatory)][string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$output = [IO.Path]::GetFullPath($OutputDirectory)
$boundary = [IO.Path]::GetFullPath((Join-Path $repositoryRoot 'artifacts\runs')).TrimEnd('\') + '\'
if (-not $output.StartsWith($boundary, [StringComparison]::OrdinalIgnoreCase)) { throw 'Process probes must run beneath artifacts\runs.' }
if (Test-Path -LiteralPath $output) { throw 'Use a new output directory to preserve earlier evidence.' }
New-Item -ItemType Directory -Path $output | Out-Null
$package = (Resolve-Path -LiteralPath $NativePackageDirectory).Path
& (Join-Path $PSScriptRoot 'Test-LanceDbNativePackage.ps1') -HostPublishDirectory $package | Out-Null
$project = Join-Path $output 'Probe.csproj'
$hostProject = [Security.SecurityElement]::Escape((Join-Path $repositoryRoot 'src\LocalRag.Host\LocalRag.Host.csproj'))
[IO.File]::WriteAllText($project, ('<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup><ItemGroup><ProjectReference Include="' + $hostProject + '" /></ItemGroup></Project>'))
$source = @'
using LocalRag.Configuration;
using LocalRag.Domain;
using LocalRag.Infrastructure.LanceDb;
using LocalRag.Infrastructure.Management;
using Microsoft.Extensions.Options;

var mode = args[0];
var root = args[1];
var options = Options.Create(new LocalRagOptions {
    DataDirectory = Path.Combine(root, "state"),
    VectorStore = new VectorStoreOptions { Provider = VectorStoreProvider.LanceDb,
        LanceDb = new LanceDbOptions { Path = Path.Combine(root, "vectors"), Table = "rag_chunks_v1", BatchSize = 100 } }
});
using var owner = new InstallationOwnershipStore(options);
using var store = new LanceDbVectorStore(options, owner);
try {
    await store.EnsureReadyAsync(CancellationToken.None);
    if (mode == "hold") {
        File.WriteAllText(Path.Combine(root, "ready"), "ready");
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        while (!File.Exists(Path.Combine(root, "stop"))) await Task.Delay(50, timeout.Token);
        return 0;
    }
    if (mode == "contend") return 90;
    if (mode == "shutdown") {
        var vector = new float[384]; vector[0] = 1;
        var chunk = new ChunkRecord("chunk", "source", "file", "fixture.txt", "text", null, 1, 1, 0, "concurrent native ownership fixture", "hash", 5, "test", DateTimeOffset.UnixEpoch, StructuralLocator: "line:1");
        await store.UpsertAsync([new VectorDocument(chunk, vector)], CancellationToken.None);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var workers = Enumerable.Range(0, 4).Select(_ => Task.Run(async () => {
            started.TrySetResult();
            try { for (var i = 0; i < 30; i++) await store.SearchAsync(new SearchRequest("ownership", ["source"], 5), vector, CancellationToken.None); }
            catch (ObjectDisposedException) { }
        })).ToArray();
        await started.Task;
        await Task.Run(store.Dispose);
        await Task.WhenAll(workers).WaitAsync(TimeSpan.FromSeconds(30));
        return 0;
    }
    return 0;
} catch (LanceDbConfigurationException error) when (mode == "contend" && error.Message.Contains("already owned", StringComparison.Ordinal)) {
    Console.WriteLine("Expected exclusive-open refusal before mutation."); return 11;
}
'@
[IO.File]::WriteAllText((Join-Path $output 'Program.cs'), $source)
& dotnet build $project -c Release *> (Join-Path $output 'build.log')
if ($LASTEXITCODE -ne 0) { throw 'Native process probe build failed.' }
$binaryRoot = Join-Path $output 'bin\Release\net10.0'
foreach ($file in Get-ChildItem -LiteralPath $package -File) { Copy-Item -LiteralPath $file.FullName -Destination $binaryRoot }
$binary = Join-Path $binaryRoot 'Probe.dll'
$state = Join-Path $output 'state'
New-Item -ItemType Directory -Path $state | Out-Null
$first = $null
$results = [Collections.Generic.List[object]]::new()
try {
    $first = Start-Process -FilePath 'dotnet.exe' -ArgumentList @(('"' + $binary + '"'), 'hold', ('"' + $state + '"')) -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $output 'hold.stdout') -RedirectStandardError (Join-Path $output 'hold.stderr')
    $deadline = [DateTime]::UtcNow.AddSeconds(30)
    while (-not (Test-Path -LiteralPath (Join-Path $state 'ready'))) {
        if ($first.HasExited -or [DateTime]::UtcNow -gt $deadline) { throw 'First process did not open the disposable LanceDB store.' }
        Start-Sleep -Milliseconds 100
    }
    $runtimeModule = @(Get-Process -Id $first.Id -Module | Where-Object ModuleName -ieq 'vcruntime140.dll')
    if ($runtimeModule.Count -ne 1 -or -not $runtimeModule[0].FileName.Equals((Join-Path $binaryRoot 'vcruntime140.dll'), [StringComparison]::OrdinalIgnoreCase)) { throw 'Native process did not load the staged app-local MSVC runtime.' }
    $results.Add([ordered]@{ scenario = 'app-local-msvc-runtime'; loadedPath = $runtimeModule[0].FileName; passed = $true })
    $before = @(Get-ChildItem -LiteralPath (Join-Path $state 'vectors') -File -Recurse | Where-Object Name -ne '.localrag-exclusive.lock' | Sort-Object FullName | ForEach-Object { [ordered]@{ path = $_.FullName; hash = (Get-FileHash -LiteralPath $_.FullName).Hash } }) | ConvertTo-Json -Compress
    & dotnet $binary contend $state *> (Join-Path $output 'contention.log')
    $contentionExit = $LASTEXITCODE
    $after = @(Get-ChildItem -LiteralPath (Join-Path $state 'vectors') -File -Recurse | Where-Object Name -ne '.localrag-exclusive.lock' | Sort-Object FullName | ForEach-Object { [ordered]@{ path = $_.FullName; hash = (Get-FileHash -LiteralPath $_.FullName).Hash } }) | ConvertTo-Json -Compress
    if ($contentionExit -ne 11 -or $before -ne $after) { throw 'Second process failed the exclusive-open/no-mutation contract.' }
    $results.Add([ordered]@{ scenario = 'EDGE-10-001'; exitCode = $contentionExit; vectorTreeUnchanged = ($before -eq $after); passed = $true })
    [IO.File]::WriteAllText((Join-Path $state 'stop'), 'stop')
    if (-not $first.WaitForExit(15000) -or $first.ExitCode -ne 0) { throw 'The first native process failed normal shutdown.' }
    & dotnet $binary shutdown $state *> (Join-Path $output 'shutdown.log')
    if ($LASTEXITCODE -ne 0) { throw 'Concurrent native search/disposal subprocess failed.' }
    & dotnet $binary reopen $state *> (Join-Path $output 'reopen.log')
    if ($LASTEXITCODE -ne 0) { throw 'Reopen after concurrent shutdown failed.' }
    $results.Add([ordered]@{ scenario = 'EDGE-10-002'; concurrentWorkers = 4; maximumSearches = 120; reopenedAfterShutdown = $true; passed = $true })
    $tableManifest = Get-ChildItem -LiteralPath (Join-Path $state 'vectors') -File -Recurse -Filter '*.manifest' | Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
    if ($null -eq $tableManifest) { throw 'No real Lance manifest was produced for the corruption scenario.' }
    $originalManifest = [IO.File]::ReadAllBytes($tableManifest.FullName)
    [IO.File]::WriteAllBytes((Join-Path $output 'original-table.manifest'), $originalManifest)
    [IO.File]::WriteAllBytes($tableManifest.FullName, [byte[]](1, 2, 3, 4))
    $corruptHash = (Get-FileHash -LiteralPath $tableManifest.FullName).Hash
    & dotnet $binary reopen $state *> (Join-Path $output 'corrupt-store.log')
    $corruptExit = $LASTEXITCODE
    if ($corruptExit -eq 0 -or (Get-FileHash -LiteralPath $tableManifest.FullName).Hash -ne $corruptHash) { throw 'Corrupt-store opening did not fail closed without replacing the damaged manifest.' }
    $results.Add([ordered]@{ scenario = 'NEG-10-005'; exitCode = $corruptExit; corruptManifestUnchanged = $true; passed = $true })
    # Restore the exact saved bytes only after the isolated failing process exits.
    [IO.File]::WriteAllBytes($tableManifest.FullName, $originalManifest)
    & dotnet $binary reopen $state *> (Join-Path $output 'restored-store.log')
    if ($LASTEXITCODE -ne 0) { throw 'Quiesced restoration of the disposable corrupt manifest did not reopen.' }
} finally {
    if ($null -ne $first -and -not $first.HasExited) {
        [IO.File]::WriteAllText((Join-Path $state 'stop'), 'stop')
        if (-not $first.WaitForExit(15000)) { Stop-Process -Id $first.Id -Force }
    }
    [ordered]@{ createdUtc = [DateTime]::UtcNow.ToString('o'); manifestSha256 = (Get-FileHash -LiteralPath (Join-Path $package 'lancedb-native.manifest.json')).Hash; results = $results } | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $output 'process-safety.json') -Encoding utf8
}
