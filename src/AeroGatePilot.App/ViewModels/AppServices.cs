using AeroGatePilot.Infrastructure;
using AeroGatePilot.Infrastructure.Data;

namespace AeroGatePilot.App.ViewModels;

public sealed record AppServices(
    AppPaths Paths,
    SettingsStore Settings,
    SqliteStore Store,
    AppLog Log,
    GatewayEngine Engine);
