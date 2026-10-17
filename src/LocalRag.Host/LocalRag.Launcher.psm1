Set-StrictMode -Version Latest

function Get-LocalRagProviderEnvironment {
    param([Parameter(Mandatory)][string]$SettingsPath)
    if (-not [IO.File]::Exists($SettingsPath)) { throw 'Installed provider settings are missing. Repair the Local RAG installation; no provider was changed.' }
    try { $settings = Get-Content -LiteralPath $SettingsPath -Raw | ConvertFrom-Json } catch { throw 'Installed provider settings are corrupt. Repair the Local RAG installation; no provider was changed.' }
    if ($null -eq $settings -or -not $settings.PSObject.Properties['schemaVersion']) { throw 'Installed provider settings have no supported schema. Repair the Local RAG installation.' }
    if ($settings.schemaVersion -eq 1) {
        if (-not $settings.PSObject.Properties['weaviateEndpoint']) { throw 'Legacy provider settings require weaviateEndpoint. Repair the installation.' }
        $provider = 'Weaviate'; $endpointText = [string]$settings.weaviateEndpoint
    } elseif ($settings.schemaVersion -eq 2 -and $settings.PSObject.Properties['provider']) {
        $provider = [string]$settings.provider
        if ($provider -eq 'Weaviate' -and $settings.PSObject.Properties['weaviate'] -and $settings.weaviate -is [pscustomobject] -and -not $settings.PSObject.Properties['lanceDb']) {
            if (-not $settings.weaviate.PSObject.Properties['endpoint']) { throw 'Weaviate provider settings require an endpoint. Repair the installation.' }
            $endpointText = [string]$settings.weaviate.endpoint
        } elseif ($provider -eq 'LanceDb' -and $settings.PSObject.Properties['lanceDb'] -and $settings.lanceDb -is [pscustomobject] -and -not $settings.PSObject.Properties['weaviate']) {
            if (-not $settings.lanceDb.PSObject.Properties['path']) { throw 'LanceDB provider settings require an absolute path. Repair the installation.' }
            $path = [string]$settings.lanceDb.path
            if ($path -notmatch '^[A-Za-z]:[\\/]' -and $path -notmatch '^\\\\[^\\]+\\[^\\]+') { throw 'LanceDB provider path must be absolute. Repair the installation.' }
            return @{ LocalRag__VectorStore__Provider = 'LanceDb'; LocalRag__VectorStore__LanceDb__Path = [IO.Path]::GetFullPath($path) }
        } else { throw 'Installed provider settings are invalid or contradictory. Repair the installation; no provider was changed.' }
    } else { throw 'Installed provider settings use an unsupported schema. Repair the installation; no provider was changed.' }
    $endpoint = $null
    if (-not [Uri]::TryCreate($endpointText, [UriKind]::Absolute, [ref]$endpoint) -or $endpoint.Scheme -notin @('http','https') -or -not $endpoint.Host -or $endpoint.UserInfo -or $endpoint.Query -or $endpoint.Fragment) {
        throw 'Weaviate endpoint must be an HTTP(S) URL without credentials, query or fragment. Repair the installation.'
    }
    return @{ LocalRag__VectorStore__Provider = $provider; LocalRag__VectorStore__Weaviate__Endpoint = $endpoint.AbsoluteUri.TrimEnd('/') }
}

function Set-LocalRagProviderEnvironment {
    param([Parameter(Mandatory)][hashtable]$ProviderEnvironment)
    # Persisted installation settings select this root. An inherited editor/test provider must not redirect it.
    foreach ($variable in @(Get-ChildItem Env: | Where-Object { $_.Name.StartsWith('LocalRag__VectorStore__', [StringComparison]::OrdinalIgnoreCase) -or $_.Name.StartsWith('LocalRag__Weaviate__', [StringComparison]::OrdinalIgnoreCase) })) {
        Remove-Item -LiteralPath ('Env:' + $variable.Name)
    }
    foreach ($key in $ProviderEnvironment.Keys) { [Environment]::SetEnvironmentVariable($key, [string]$ProviderEnvironment[$key], 'Process') }
}

Export-ModuleMember -Function Get-LocalRagProviderEnvironment, Set-LocalRagProviderEnvironment
