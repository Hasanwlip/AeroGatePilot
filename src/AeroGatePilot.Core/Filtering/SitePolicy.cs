using AeroGatePilot.Core.Models;

namespace AeroGatePilot.Core.Filtering;

/// <summary>Applies a plan's website rules to host names and to addresses learned from DNS.</summary>
public static class SitePolicy
{
    /// <summary>
    /// Connectivity checks of phones and computers. They stay reachable in allow-list mode so devices
    /// do not report "no internet" and drop the Wi-Fi while the allowed sites work.
    /// </summary>
    private static readonly DomainRules ConnectivityChecks = DomainRules.Parse("""
        captive.apple.com *.captive.apple.com
        connectivitycheck.gstatic.com connectivitycheck.android.com clients3.google.com clients1.google.com
        www.msftconnecttest.com www.msftncsi.com dns.msftncsi.com
        detectportal.firefox.com cp.cloudflare.com nmcheck.gnome.org
        connect.rom.miui.com conn1.oppomobile.com connectivitycheck.platform.hicloud.com
        """);

    /// <summary>
    /// Encrypted DNS resolvers and iCloud Private Relay. With website rules active they are refused,
    /// so devices fall back to the hotspot's plain DNS where names can be checked.
    /// </summary>
    private static readonly DomainRules FilterBypass = DomainRules.Parse("""
        *.dns.google dns.google.com *.cloudflare-dns.com one.one.one.one 1dot1dot1dot1.cloudflare-dns.com
        *.quad9.net doh.opendns.com dns.opendns.com *.dns.adguard.com *.adguard-dns.com *.nextdns.io
        doh.cleanbrowsing.org doh.dns.sb dns.alidns.com doh.pub dns.controld.com freedns.controld.com
        mask.icloud.com mask-h2.icloud.com mask-api.icloud.com
        """);

    public static bool IsFiltered(Plan plan) => plan.SiteFilter != SiteFilterMode.Off;

    public static bool AllowsHost(Plan plan, string host) => plan.SiteFilter switch
    {
        SiteFilterMode.BlockListed => !FilterBypass.Matches(host) && !DomainRules.Parse(plan.SiteList).Matches(host),
        SiteFilterMode.AllowListed => DomainRules.Parse(plan.SiteList).Matches(host)
                                      || ConnectivityChecks.Matches(host) && !FilterBypass.Matches(host),
        _ => true,
    };

    /// <summary>
    /// Decision for a connection whose host name is unknown (no SNI/Host), based on the names that resolved to the
    /// destination address. Allow-list mode denies unknown addresses; block-list mode allows them.
    /// </summary>
    public static bool AllowsAddress(Plan plan, IReadOnlyList<string> resolvedNames) => plan.SiteFilter switch
    {
        SiteFilterMode.BlockListed => !resolvedNames.Any(n => !AllowsHost(plan, n)),
        SiteFilterMode.AllowListed => resolvedNames.Any(n => AllowsHost(plan, n)),
        _ => true,
    };
}
