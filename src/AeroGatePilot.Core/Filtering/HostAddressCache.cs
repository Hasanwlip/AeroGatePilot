using System.Collections.Concurrent;

namespace AeroGatePilot.Core.Filtering;

/// <summary>Remembers which host names resolved to which IPv4 address, learned from DNS answers seen on the hotspot.</summary>
public sealed class HostAddressCache
{
    private const int MaxNamesPerAddress = 8;
    private const int MaxEntries = 200_000;
    private static readonly TimeSpan MinTtl = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan MaxTtl = TimeSpan.FromHours(2);

    private readonly ConcurrentDictionary<uint, Entry> _entries = new();
    private readonly Func<DateTime> _clock;

    public HostAddressCache(Func<DateTime>? clock = null) => _clock = clock ?? (() => DateTime.UtcNow);

    public int Count => _entries.Count;

    public void Add(uint address, string name, uint ttlSeconds)
    {
        var host = DomainRules.NormalizeHost(name);
        if (host is null || address == 0)
            return;
        var ttl = TimeSpan.FromSeconds(ttlSeconds);
        var expires = _clock() + (ttl < MinTtl ? MinTtl : ttl > MaxTtl ? MaxTtl : ttl);

        if (_entries.Count >= MaxEntries)
            Prune();

        _entries.AddOrUpdate(
            address,
            _ => new Entry([host], expires),
            (_, existing) =>
            {
                if (existing.Names.Contains(host))
                    return existing with { Expires = expires > existing.Expires ? expires : existing.Expires };
                var names = existing.Names.Length >= MaxNamesPerAddress ? existing.Names[1..] : existing.Names;
                return new Entry([.. names, host], expires > existing.Expires ? expires : existing.Expires);
            });
    }

    public IReadOnlyList<string> NamesFor(uint address) =>
        _entries.TryGetValue(address, out var entry) && entry.Expires > _clock() ? entry.Names : [];

    public void Prune()
    {
        var now = _clock();
        foreach (var (address, entry) in _entries)
        {
            if (entry.Expires <= now)
                _entries.TryRemove(address, out _);
        }
        if (_entries.Count >= MaxEntries)
            _entries.Clear();
    }

    private sealed record Entry(string[] Names, DateTime Expires);
}
