using AeroGatePilot.Core.Models;

namespace AeroGatePilot.Core.Access;

public sealed record QuotaStatus(
    AccessDenyReason Reason,
    long? RemainingBytes,
    TimeSpan? RemainingTime,
    DateTime? ExpiresUtc);

public static class QuotaEvaluator
{
    public const long BytesPerMb = 1024L * 1024L;

    public static DateTime? ExpiryUtc(UserAccount user, Plan plan) =>
        plan.ValidityDays > 0 && user.FirstLoginUtc is { } first ? first.AddDays(plan.ValidityDays) : null;

    /// <summary>
    /// Evaluates whether the account may (still) use the network.
    /// <paramref name="pendingBytes"/> and <paramref name="pendingSeconds"/> are usage not yet stored on the account,
    /// <paramref name="sessionDuration"/> is the length of the current session (zero when logging in).
    /// </summary>
    public static QuotaStatus Evaluate(
        UserAccount user,
        Plan plan,
        DateTime nowUtc,
        long pendingBytes = 0,
        long pendingSeconds = 0,
        TimeSpan sessionDuration = default)
    {
        var expires = ExpiryUtc(user, plan);

        long? remainingBytes = plan.DataLimitMb > 0
            ? Math.Max(0, plan.DataLimitMb * BytesPerMb - user.UsedTotalBytes - pendingBytes)
            : null;

        TimeSpan? remainingTime = null;
        if (plan.TimeLimitMinutes > 0)
            remainingTime = Max(TimeSpan.Zero, TimeSpan.FromMinutes(plan.TimeLimitMinutes) - TimeSpan.FromSeconds(user.UsedSeconds + pendingSeconds));
        if (plan.SessionLimitMinutes > 0)
        {
            var sessionLeft = Max(TimeSpan.Zero, TimeSpan.FromMinutes(plan.SessionLimitMinutes) - sessionDuration);
            remainingTime = remainingTime is { } r ? Min(r, sessionLeft) : sessionLeft;
        }
        if (expires is { } exp)
        {
            var validityLeft = Max(TimeSpan.Zero, exp - nowUtc);
            remainingTime = remainingTime is { } r ? Min(r, validityLeft) : validityLeft;
        }

        var reason = AccessDenyReason.None;
        if (!user.Enabled)
            reason = AccessDenyReason.AccountDisabled;
        else if (expires is { } e && nowUtc >= e)
            reason = AccessDenyReason.AccountExpired;
        else if (remainingBytes == 0)
            reason = AccessDenyReason.DataExhausted;
        else if (plan.TimeLimitMinutes > 0 && user.UsedSeconds + pendingSeconds >= plan.TimeLimitMinutes * 60L)
            reason = AccessDenyReason.TimeExhausted;
        else if (plan.SessionLimitMinutes > 0 && sessionDuration >= TimeSpan.FromMinutes(plan.SessionLimitMinutes))
            reason = AccessDenyReason.SessionTimeLimit;

        return new QuotaStatus(reason, remainingBytes, remainingTime, expires);
    }

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;
    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;
}
