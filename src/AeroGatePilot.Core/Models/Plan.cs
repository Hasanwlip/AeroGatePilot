using AeroGatePilot.Core.Filtering;

namespace AeroGatePilot.Core.Models;

/// <summary>
/// An access plan. Every limit uses 0 to mean "unlimited".
/// </summary>
public sealed class Plan
{
    public long Id { get; set; }
    public string Name { get; set; } = "";

    /// <summary>Total data volume (download + upload) in megabytes.</summary>
    public long DataLimitMb { get; set; }

    /// <summary>Total online time across all sessions, in minutes.</summary>
    public int TimeLimitMinutes { get; set; }

    /// <summary>Maximum length of a single session, in minutes.</summary>
    public int SessionLimitMinutes { get; set; }

    /// <summary>Days the account stays valid, counted from the first login.</summary>
    public int ValidityDays { get; set; }

    public int DownloadKbps { get; set; }
    public int UploadKbps { get; set; }

    /// <summary>Simultaneous devices allowed for one account.</summary>
    public int MaxDevices { get; set; } = 1;

    /// <summary>How <see cref="SiteList"/> is applied.</summary>
    public SiteFilterMode SiteFilter { get; set; }

    /// <summary>Sites for the filter, one per line. <c>*.example.com</c> covers the domain and all its subdomains.</summary>
    public string SiteList { get; set; } = "";

    /// <summary>Send this plan's web traffic through the upstream VPN/proxy port (when one is configured).</summary>
    public bool UseUpstreamProxy { get; set; }

    public Plan Clone() => (Plan)MemberwiseClone();
}
