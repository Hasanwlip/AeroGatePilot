using System.Security.AccessControl;
using System.Security.Principal;

namespace AeroGatePilot.Infrastructure;

/// <summary>All runtime data lives under %ProgramData%\AeroGatePilot so it is independent of where the app is installed.</summary>
public sealed class AppPaths
{
    public AppPaths(string? root = null)
    {
        Root = root ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "AeroGatePilot");
        Directory.CreateDirectory(Root);
        RestrictToAdministrators(Root);
        Directory.CreateDirectory(BrandingDirectory);
        Directory.CreateDirectory(TemplateDirectory);
        Directory.CreateDirectory(LogDirectory);
        Directory.CreateDirectory(CoreDataDirectory);
    }

    public string Root { get; }
    public string DatabaseFile => Path.Combine(Root, "aerogate.db");
    public string SettingsFile => Path.Combine(Root, "settings.json");
    public string SnapshotFile => Path.Combine(Root, "network-snapshot.json");
    public string BrandingDirectory => Path.Combine(Root, "branding");
    public string TemplateDirectory => Path.Combine(Root, "portal-template");
    public string LogDirectory => Path.Combine(Root, "logs");

    /// <summary>Generated Xray configs (the binary itself lives next to the EXE in <c>core\</c>).</summary>
    public string CoreDataDirectory => Path.Combine(Root, "xray");

    /// <summary>The folder holds the Wi-Fi password and account database, so only administrators and SYSTEM may read it.</summary>
    private static void RestrictToAdministrators(string path)
    {
        using (var identity = WindowsIdentity.GetCurrent())
        {
            if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
                return;
        }

        try
        {
            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            const InheritanceFlags inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
            foreach (var sid in new[] { WellKnownSidType.BuiltinAdministratorsSid, WellKnownSidType.LocalSystemSid })
            {
                security.AddAccessRule(new FileSystemAccessRule(
                    new SecurityIdentifier(sid, null), FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
            }
            new DirectoryInfo(path).SetAccessControl(security);
        }
        catch (Exception)
        {
            // ACLs unsupported on this volume: keep default permissions.
        }
    }
}
