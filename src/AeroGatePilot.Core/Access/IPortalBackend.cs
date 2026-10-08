using System.Net;
using AeroGatePilot.Core.Models;

namespace AeroGatePilot.Core.Access;

/// <summary>What the captive portal web server needs from the gateway.</summary>
public interface IPortalBackend
{
    AppSettings Settings { get; }

    /// <summary>Folder holding uploaded logo/background and template overrides.</summary>
    string BrandingDirectory { get; }

    /// <summary>Folder whose files (login.html, status.html, portal.css, portal.js) override the built-in template.</summary>
    string TemplateDirectory { get; }

    PortalClientStatus? GetStatus(IPAddress client);
    LoginResult Login(string username, string password, IPAddress client);
    LoginResult GuestLogin(IPAddress client);
    void Logout(IPAddress client);
}
