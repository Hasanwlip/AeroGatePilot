using AeroGatePilot.Core.Access;
using AeroGatePilot.Core.Filtering;
using AeroGatePilot.Core.Models;
using AeroGatePilot.Core.Net;

namespace AeroGatePilot.Tests;

public class VpnBypassTests
{
    [Fact]
    public void Listed_domain_and_subdomain_skip_the_vpn()
    {
        const string rules = "*.zarinpal.com\npaypal.com";
        Assert.True(VpnBypass.Matches(rules, ["www.zarinpal.com"]));
        Assert.True(VpnBypass.Matches(rules, ["paypal.com"]));
        Assert.False(VpnBypass.Matches(rules, ["example.com"]));
        Assert.False(VpnBypass.Matches("", ["www.zarinpal.com"]));
    }
}

public class DhcpOption82Tests
{
    [Fact]
    public void Reads_client_mac_and_circuit_id()
    {
        var packet = new byte[251];
        packet[0] = 1;
        packet[2] = 6;
        packet[28] = 0xAA;
        packet[29] = 0xBB;
        packet[30] = 0xCC;
        packet[31] = 0xDD;
        packet[32] = 0xEE;
        packet[33] = 0xFF;
        packet[236] = 99;
        packet[237] = 130;
        packet[238] = 83;
        packet[239] = 99;
        packet[240] = 82;
        packet[241] = 8;
        packet[242] = 1;
        packet[243] = 6;
        "ether2"u8.CopyTo(packet.AsSpan(244));
        packet[250] = 255;

        Assert.True(DhcpMessage.TryParse(packet, out var info));
        Assert.Equal("AA:BB:CC:DD:EE:FF", info.Mac);
        Assert.Equal("ether2", info.CircuitId);
        Assert.Contains("ether2", DhcpMessage.Label(info));
    }
}

public class PauseTests
{
    [Fact]
    public void Paused_account_keeps_its_usage_and_is_refused()
    {
        var now = new DateTime(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);
        var user = new UserAccount { UsedDownloadBytes = 1234, PausedUntilUtc = now.AddHours(6) };
        var status = QuotaEvaluator.Evaluate(user, new Plan(), now);
        Assert.Equal(AccessDenyReason.Paused, status.Reason);
        Assert.Equal(1234, user.UsedDownloadBytes);

        var later = QuotaEvaluator.Evaluate(user, new Plan(), now.AddHours(7));
        Assert.Equal(AccessDenyReason.None, later.Reason);
    }
}
