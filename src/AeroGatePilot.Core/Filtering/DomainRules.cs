using System.Collections.Concurrent;

namespace AeroGatePilot.Core.Filtering;

public enum SiteFilterMode
{
    /// <summary>Every site is reachable.</summary>
    Off,

    /// <summary>Listed sites are blocked, everything else is reachable.</summary>
    BlockListed,

    /// <summary>Only listed sites are reachable.</summary>
    AllowListed,
}

/// <summary>
/// A list of host rules. <c>example.com</c> matches exactly that host; <c>*.example.com</c> matches
/// example.com and every subdomain (www.example.com, cdn.eu.example.com...).
/// Entries may be separated by new lines, commas or spaces; pasted URLs are reduced to their host and <c>#</c> starts a comment.
/// </summary>
public sealed class DomainRules
{
    private static readonly ConcurrentDictionary<string, DomainRules> Cache = new();

    private readonly HashSet<string> _exact;
    private readonly string[] _branches;

    private DomainRules(HashSet<string> exact, string[] branches)
    {
        _exact = exact;
        _branches = branches;
    }

    public static DomainRules Empty { get; } = new([], []);

    public bool IsEmpty => _exact.Count == 0 && _branches.Length == 0;
    public int Count => _exact.Count + _branches.Length;

    /// <summary>Parses (and caches) a rule list. Invalid entries are ignored.</summary>
    public static DomainRules Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return Empty;
        if (Cache.TryGetValue(text, out var cached))
            return cached;

        var exact = new HashSet<string>(StringComparer.Ordinal);
        var branches = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in Entries(text))
        {
            var wildcard = entry.StartsWith("*.", StringComparison.Ordinal);
            var host = NormalizeHost(wildcard ? entry[2..] : entry);
            if (host is null)
                continue;
            (wildcard ? branches : exact).Add(host);
        }

        var rules = new DomainRules(exact, [.. branches]);
        if (Cache.Count > 512)
            Cache.Clear();
        Cache[text] = rules;
        return rules;
    }

    /// <summary>Entries of <paramref name="text"/> that are not valid rules (for showing to the admin).</summary>
    public static IReadOnlyList<string> InvalidEntries(string? text) =>
        string.IsNullOrWhiteSpace(text)
            ? []
            : Entries(text).Where(e => NormalizeHost(e.StartsWith("*.", StringComparison.Ordinal) ? e[2..] : e) is null).ToList();

    public bool Matches(string? host)
    {
        host = NormalizeHost(host);
        if (host is null)
            return false;
        if (_exact.Contains(host))
            return true;
        foreach (var branch in _branches)
        {
            if (host.Length == branch.Length
                    ? host == branch
                    : host.Length > branch.Length && host[^(branch.Length + 1)] == '.' && host.EndsWith(branch, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    public bool MatchesAny(IEnumerable<string> hosts) => hosts.Any(Matches);

    /// <summary>Lower-cases a host and strips scheme, path, port and trailing dot. Returns null when it is not a host name.</summary>
    public static string? NormalizeHost(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var host = value.Trim().ToLowerInvariant();
        var scheme = host.IndexOf("://", StringComparison.Ordinal);
        if (scheme >= 0)
            host = host[(scheme + 3)..];
        var slash = host.IndexOfAny(['/', '?', '#']);
        if (slash >= 0)
            host = host[..slash];
        var at = host.LastIndexOf('@');
        if (at >= 0)
            host = host[(at + 1)..];
        var colon = host.IndexOf(':');
        if (colon >= 0)
            host = host[..colon];
        host = host.TrimEnd('.');

        if (host.Length is 0 or > 253 || host.StartsWith('.') || host.Contains(".."))
            return null;
        foreach (var c in host)
        {
            if (!(c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '.' or '_'))
                return null;
        }
        return host;
    }

    private static IEnumerable<string> Entries(string text)
    {
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine;
            var comment = line.IndexOf('#');
            if (comment >= 0)
                line = line[..comment];
            foreach (var entry in line.Split([',', ' ', '\t', '\r', ';'], StringSplitOptions.RemoveEmptyEntries))
                yield return entry;
        }
    }
}
