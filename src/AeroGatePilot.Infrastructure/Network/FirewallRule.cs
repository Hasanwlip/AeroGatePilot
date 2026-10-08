using System.Diagnostics;

namespace AeroGatePilot.Infrastructure.Network;

/// <summary>
/// Inbound Windows Firewall rule for the portal port. The hotspot network is usually classified as Public,
/// where unknown listeners are blocked, so Wi-Fi clients could not reach the login page without it.
/// </summary>
public static class FirewallRule
{
    private const string RuleName = "AeroGate Pilot Portal";

    public static void Allow(params int[] ports)
    {
        Remove();
        Netsh($"advfirewall firewall add rule name=\"{RuleName}\" dir=in action=allow protocol=TCP localport={string.Join(',', ports)} profile=any enable=yes");
    }

    public static void Remove() => Netsh($"advfirewall firewall delete rule name=\"{RuleName}\"", ignoreExitCode: true);

    private static void Netsh(string arguments, bool ignoreExitCode = false)
    {
        var psi = new ProcessStartInfo("netsh.exe", arguments)
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Could not run netsh.");
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit(15_000);
        if (!ignoreExitCode && process.ExitCode != 0)
            throw new InvalidOperationException($"Could not add the Windows Firewall rule for the portal: {output.Trim()}");
    }
}
