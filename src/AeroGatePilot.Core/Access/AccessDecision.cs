namespace AeroGatePilot.Core.Access;

public enum AccessDenyReason
{
    None,
    InvalidCredentials,
    AccountDisabled,
    AccountExpired,
    DataExhausted,
    TimeExhausted,
    SessionTimeLimit,
    DeviceLimit,
    PlanMissing,
    GuestDisabled,
    TooManyAttempts,
    GatewayNotRunning,
    LoggedOut,
    KickedByAdmin,
    GatewayStopped,
}

public sealed record LoginResult(bool Success, AccessDenyReason Reason)
{
    public static LoginResult Ok { get; } = new(true, AccessDenyReason.None);
    public static LoginResult Fail(AccessDenyReason reason) => new(false, reason);
}
