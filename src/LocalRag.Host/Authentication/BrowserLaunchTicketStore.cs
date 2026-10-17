using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace LocalRag.Authentication;

/// <summary>One-use, short-lived handoff. Only opaque tickets, never local bearer credentials, enter browser URLs.</summary>
public sealed class BrowserLaunchTicketStore(TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly ConcurrentDictionary<string, (DateTimeOffset Expires, bool Management)> _tickets = new(StringComparer.Ordinal);
    public string Issue(bool management)
    {
        foreach (var item in _tickets.Where(x => x.Value.Expires < _time.GetUtcNow())) _tickets.TryRemove(item.Key, out _);
        if (_tickets.Count >= 256) throw new InvalidOperationException("Too many pending browser launches. Retry shortly.");
        var ticket = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        _tickets[ticket] = (_time.GetUtcNow().AddSeconds(60), management);
        return ticket;
    }
    public bool Consume(string ticket, out bool management)
    {
        management = false;
        if (string.IsNullOrWhiteSpace(ticket) || !_tickets.TryRemove(ticket, out var entry) || entry.Expires < _time.GetUtcNow()) return false;
        management = entry.Management;
        return true;
    }
}
