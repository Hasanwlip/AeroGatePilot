using System.Collections.Concurrent;
using AeroGatePilot.Core.Access;
using AeroGatePilot.Core.Models;

namespace AeroGatePilot.Infrastructure.Network;

/// <summary>Remembers which access point a MAC was last seen on, from DHCP option 82.</summary>
public sealed class ApDirectory
{
    private readonly ConcurrentDictionary<string, string> _labels = new(StringComparer.OrdinalIgnoreCase);

    public void Observe(string mac, string label)
    {
        if (string.IsNullOrWhiteSpace(mac) || string.IsNullOrWhiteSpace(label))
            return;
        _labels[MacResolver.Normalize(mac)] = label.Trim();
    }

    public string? LabelFor(string mac) =>
        string.IsNullOrWhiteSpace(mac) ? null : _labels.TryGetValue(MacResolver.Normalize(mac), out var label) ? label : null;

    public ApZoneMatch? Match(string mac, IReadOnlyList<ApZone> zones)
    {
        var label = LabelFor(mac);
        if (label is null)
            return null;
        foreach (var zone in zones)
        {
            if (string.IsNullOrWhiteSpace(zone.Match))
                continue;
            if (label.Contains(zone.Match.Trim(), StringComparison.OrdinalIgnoreCase))
                return new ApZoneMatch(string.IsNullOrWhiteSpace(zone.Name) ? zone.Match : zone.Name, zone.PlanId);
        }
        return new ApZoneMatch(label, 0);
    }
}
