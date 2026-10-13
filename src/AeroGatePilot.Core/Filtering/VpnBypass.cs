namespace AeroGatePilot.Core.Filtering;

/// <summary>Decides whether a destination learned from DNS should skip the VPN relay.</summary>
public static class VpnBypass
{
    public static bool Matches(string? rules, IEnumerable<string>? names)
    {
        var parsed = DomainRules.Parse(rules);
        if (parsed.IsEmpty || names is null)
            return false;
        foreach (var name in names)
        {
            if (parsed.Matches(name))
                return true;
        }
        return false;
    }
}
