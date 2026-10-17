[CmdletBinding()]
param(
    [string]$Endpoint = 'http://127.0.0.1:5198',
    [switch]$NoBrowser,
    [string]$DataDirectory = (Join-Path $env:LOCALAPPDATA 'LocalRag'),
    [string]$HostExecutable,
    [int]$TimeoutSeconds = 45
)
$ErrorActionPreference = 'Stop'
try {
    $uri = [Uri]$Endpoint
    if (-not $uri.IsLoopback -or $uri.Scheme -ne 'http' -or $uri.AbsolutePath -ne '/' -or $uri.Query -or $uri.Fragment -or $uri.UserInfo) { throw 'Endpoint must be an HTTP loopback origin without a path, query or credentials.' }
    $Endpoint = $uri.GetLeftPart([UriPartial]::Authority)
    [IO.Directory]::CreateDirectory($DataDirectory) | Out-Null
    function Read-OrCreateSecret([string]$Name, [string]$Override) {
        if ($Override) { return $Override }
        $secretPath = Join-Path $DataDirectory $Name
        if (-not [IO.File]::Exists($secretPath)) {
            $bytes = New-Object byte[] 32
            $rng = [Security.Cryptography.RandomNumberGenerator]::Create()
            try { $rng.GetBytes($bytes) } finally { $rng.Dispose() }
            $secret = [Convert]::ToBase64String($bytes).TrimEnd('=').Replace('+','-').Replace('/','_')
            try {
                $stream = [IO.File]::Open($secretPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
                try { $payload = [Text.Encoding]::UTF8.GetBytes($secret); $stream.Write($payload,0,$payload.Length); $stream.Flush($true) } finally { $stream.Dispose() }
            } catch [IO.IOException] { if (-not [IO.File]::Exists($secretPath)) { throw } }
        }
        # Secrets belong to the interactive Windows account; never print them or put them in process arguments.
        $acl = New-Object Security.AccessControl.FileSecurity
        $identity = [Security.Principal.WindowsIdentity]::GetCurrent().User
        $acl.SetOwner($identity); $acl.SetAccessRuleProtection($true,$false)
        $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule($identity,'FullControl','Allow')))
        Set-Acl -LiteralPath $secretPath -AclObject $acl
        for ($attempt=0; $attempt -lt 20; $attempt++) {
            try { $value = [IO.File]::ReadAllText($secretPath).Trim(); if ($value.Length -ge 32) { return $value } } catch [IO.IOException] { }
            Start-Sleep -Milliseconds 50
        }
        throw 'The shared credential file is incomplete. Close other Local RAG launchers and retry.'
    }
    function Test-HostLive {
        try { return (Invoke-WebRequest -Uri "$Endpoint/health/live" -UseBasicParsing -TimeoutSec 2).StatusCode -eq 200 } catch { return $false }
    }
    $digest = [Security.Cryptography.SHA256]::Create()
    try { $hash = ([BitConverter]::ToString($digest.ComputeHash([Text.Encoding]::UTF8.GetBytes($Endpoint.ToLowerInvariant())))).Replace('-','').Substring(0,24) } finally { $digest.Dispose() }
    $mutex = New-Object Threading.Mutex($false, "Local\LocalRag.Start.$hash")
    $acquired = $false
    try {
        try { $acquired = $mutex.WaitOne([TimeSpan]::FromSeconds($TimeoutSeconds)) } catch [Threading.AbandonedMutexException] { $acquired = $true }
        if (-not $acquired) { throw 'Another launcher is still starting Local RAG. Retry shortly.' }
        $token = Read-OrCreateSecret 'client-token' $env:LOCALRAG_MCP_TOKEN
        $managementToken = Read-OrCreateSecret 'management-token' $env:LOCALRAG_MANAGEMENT_TOKEN
        if (-not (Test-HostLive)) {
            if (-not $HostExecutable) {
                $discovery = Get-Content -LiteralPath (Join-Path $DataDirectory 'installation.json') -Raw | ConvertFrom-Json
                $HostExecutable = $discovery.hostExecutable
            }
            if (-not [IO.File]::Exists($HostExecutable)) { throw 'Installed host executable was not found. Repair the Local RAG installation.' }
            $env:LocalRag__Authentication__Token = $token
            $env:LocalRag__Management__Enabled = 'true'
            $env:LocalRag__Management__Token = $managementToken
            $env:LocalRag__DataDirectory = $DataDirectory
            $env:LocalRag__Embedding__ModelDirectory = Join-Path $DataDirectory 'models/bge-small-en-v1.5'
            $env:ASPNETCORE_URLS = $Endpoint
            $settingsPath = Join-Path $DataDirectory 'install-settings.json'
            Import-Module (Join-Path $PSScriptRoot 'LocalRag.Launcher.psm1') -Force
            $providerEnvironment = Get-LocalRagProviderEnvironment -SettingsPath $settingsPath
            Set-LocalRagProviderEnvironment -ProviderEnvironment $providerEnvironment
            $startupLog = Join-Path $DataDirectory 'host-startup.log'
            $startupErrors = Join-Path $DataDirectory 'host-startup-errors.log'
            Start-Process -FilePath $HostExecutable -ArgumentList @('--urls', $Endpoint) -WorkingDirectory (Split-Path -Parent $HostExecutable) -WindowStyle Hidden -RedirectStandardOutput $startupLog -RedirectStandardError $startupErrors | Out-Null
            $deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
            while (-not (Test-HostLive)) {
                if ([DateTimeOffset]::UtcNow -gt $deadline) { throw "Local RAG did not start. Inspect $startupErrors and $startupLog; check provider lock contention and installed assets." }
                Start-Sleep -Milliseconds 300
            }
        }
        # Readiness may be degraded (for example missing models); the manager must still open with diagnostics.
        try { Invoke-RestMethod -Uri "$Endpoint/api/v1/catalog" -Headers @{ Authorization = "Bearer $token" } -TimeoutSec 5 | Out-Null }
        catch { throw "A host is listening at $Endpoint but did not accept the shared credential or current catalog API. End an older LocalRag.Host process in Task Manager and launch again. Check the configured endpoint if the problem persists." }
        if (-not $NoBrowser) {
            $ticket = Invoke-RestMethod -Uri "$Endpoint/manage/tickets" -Method Post -ContentType 'application/json' -Headers @{ Authorization = "Bearer $token" } -Body (@{managementToken=$managementToken} | ConvertTo-Json -Compress) -TimeoutSec 5
            Start-Process "$Endpoint/manage#ticket=$($ticket.ticket)" -WindowStyle Hidden | Out-Null
        }
    } finally { if ($acquired) { $mutex.ReleaseMutex() }; $mutex.Dispose() }
} catch {
    $message = 'Local RAG startup failed: ' + $_.Exception.Message
    if (-not $NoBrowser) { Add-Type -AssemblyName PresentationFramework; [System.Windows.MessageBox]::Show($message, 'Local RAG') | Out-Null }
    Write-Error $message
    exit 1
}
