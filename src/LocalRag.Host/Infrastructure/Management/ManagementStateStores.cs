using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LocalRag.Configuration;
using Microsoft.Extensions.Options;

namespace LocalRag.Infrastructure.Management;

public sealed class ManagementConfirmationStore(IOptions<LocalRagOptions> options)
{
    private const int MaximumChallenges = 128;
    private readonly object _sync = new();
    private readonly Dictionary<string, Challenge> _challenges = new(StringComparer.Ordinal);

    public ConfirmationChallenge Create(string action, string target, string principalId)
    {
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        var expires = DateTimeOffset.UtcNow.AddSeconds(options.Value.Management.ConfirmationLifetimeSeconds);
        var challenge = new Challenge(
            Hash(token),
            action,
            Hash(target),
            principalId,
            expires);
        lock (_sync)
        {
            RemoveExpired();
            if (_challenges.Count >= MaximumChallenges)
            {
                var oldest = _challenges.MinBy(pair => pair.Value.ExpiresUtc).Key;
                _challenges.Remove(oldest);
            }
            _challenges[challenge.TokenHash] = challenge;
        }
        return new(token, expires);
    }

    public bool Consume(string token, string action, string target, string principalId)
    {
        var suppliedHash = Hash(token);
        lock (_sync)
        {
            RemoveExpired();
            if (!_challenges.Remove(suppliedHash, out var challenge)) return false;
            return challenge.ExpiresUtc >= DateTimeOffset.UtcNow &&
                FixedEquals(challenge.Action, action) &&
                FixedEquals(challenge.TargetHash, Hash(target)) &&
                FixedEquals(challenge.PrincipalId, principalId);
        }
    }

    private void RemoveExpired()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var key in _challenges.Where(pair => pair.Value.ExpiresUtc < now).Select(pair => pair.Key).ToArray())
        {
            _challenges.Remove(key);
        }
    }

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static bool FixedEquals(string left, string right)
    {
        var leftBytes = Encoding.UTF8.GetBytes(left);
        var rightBytes = Encoding.UTF8.GetBytes(right);
        return leftBytes.Length == rightBytes.Length &&
            CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }

    private sealed record Challenge(
        string TokenHash,
        string Action,
        string TargetHash,
        string PrincipalId,
        DateTimeOffset ExpiresUtc);
}

public sealed record ConfirmationChallenge(string Token, DateTimeOffset ExpiresUtc);

public sealed class InstallationOwnershipStore : IDisposable
{
    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public InstallationOwnershipStore(IOptions<LocalRagOptions> options)
    {
        var dataDirectory = Environment.ExpandEnvironmentVariables(options.Value.DataDirectory);
        _path = Path.Combine(dataDirectory, "installation-owner.id");
    }

    public async Task<string> GetOrCreateAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (File.Exists(_path))
            {
                var existing = (await File.ReadAllTextAsync(_path, cancellationToken)).Trim();
                if (Guid.TryParseExact(existing, "N", out _)) return existing;
                throw new InvalidOperationException("Installation ownership evidence is invalid.");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var ownershipId = Guid.NewGuid().ToString("N");
            await File.WriteAllTextAsync(_path, ownershipId, cancellationToken);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(_path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
            return ownershipId;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();
}

public sealed record ResetState(
    int Version,
    string OwnershipId,
    string OperationId,
    string State,
    string PhaseCode);

public sealed class ResetStateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _path;

    public ResetStateStore(IOptions<LocalRagOptions> options)
    {
        var dataDirectory = Environment.ExpandEnvironmentVariables(options.Value.DataDirectory);
        _path = Path.Combine(dataDirectory, "reset-state.json");
    }

    public bool HasIncompleteReset => File.Exists(_path);

    public async Task<ResetState?> ReadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_path)) return null;
        try
        {
            var json = await File.ReadAllTextAsync(_path, cancellationToken);
            var state = JsonSerializer.Deserialize<ResetState>(json, JsonOptions);
            return state is { Version: 1 } &&
                Guid.TryParseExact(state.OwnershipId, "N", out _) &&
                !string.IsNullOrWhiteSpace(state.OperationId)
                ? state
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public async Task WriteAsync(ResetState state, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temporary = _path + ".tmp";
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(state, JsonOptions), cancellationToken);
        File.Move(temporary, _path, overwrite: true);
    }

    public void Complete()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }
}
