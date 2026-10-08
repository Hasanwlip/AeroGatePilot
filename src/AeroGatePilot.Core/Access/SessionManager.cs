using System.Collections.Concurrent;
using System.Net;
using AeroGatePilot.Core.Models;
using AeroGatePilot.Core.Net;
using AeroGatePilot.Core.Security;

namespace AeroGatePilot.Core.Access;

public sealed record PortalClientStatus(
    string Username,
    string DisplayName,
    string PlanName,
    bool IsGuest,
    DateTime StartedUtc,
    long SessionDownloadBytes,
    long SessionUploadBytes,
    long UsedTotalBytes,
    long? DataLimitBytes,
    long? RemainingBytes,
    TimeSpan? RemainingTime,
    DateTime? ExpiresUtc,
    int DownloadKbps,
    int UploadKbps);

public sealed class SessionManager
{
    private const int MaxFailedAttempts = 5;
    private static readonly TimeSpan FailureWindow = TimeSpan.FromMinutes(1);

    private readonly IAccountStore _store;
    private readonly Func<DateTime> _clock;
    private readonly ConcurrentDictionary<uint, ClientSession> _sessions = new();
    private readonly ConcurrentDictionary<uint, List<DateTime>> _failures = new();
    private readonly object _gate = new();

    public SessionManager(IAccountStore store, Func<DateTime>? clock = null)
    {
        _store = store;
        _clock = clock ?? (() => DateTime.UtcNow);
    }

    /// <summary>How often usage is written to the database and account/plan edits are picked up.</summary>
    public TimeSpan FlushInterval { get; set; } = TimeSpan.FromSeconds(10);

    public event Action<ClientSession>? SessionStarted;
    public event Action<ClientSession, AccessDenyReason>? SessionEnded;

    public IReadOnlyList<ClientSession> Sessions => _sessions.Values.ToArray();
    public int Count => _sessions.Count;

    public ClientSession? TryGet(uint ipKey) => _sessions.TryGetValue(ipKey, out var s) ? s : null;
    public ClientSession? TryGet(IPAddress ip) => TryGet(IpUtil.ToKey(ip));

    public LoginResult Login(string username, string password, IPAddress ip, string mac)
    {
        var key = IpUtil.ToKey(ip);
        if (IsThrottled(key))
            return LoginResult.Fail(AccessDenyReason.TooManyAttempts);

        var user = _store.FindUserByName(username.Trim());
        if (user is null || user.IsGuest || !PasswordHasher.Verify(password, user.PasswordHash))
        {
            RegisterFailure(key);
            return LoginResult.Fail(AccessDenyReason.InvalidCredentials);
        }

        _failures.TryRemove(key, out _);
        return Authorize(user, ip, mac);
    }

    public LoginResult GuestLogin(IPAddress ip, string mac, long guestPlanId)
    {
        if (_store.GetPlan(guestPlanId) is null)
            return LoginResult.Fail(AccessDenyReason.PlanMissing);
        var deviceKey = string.IsNullOrWhiteSpace(mac) ? IpUtil.Format(ip) : mac.ToUpperInvariant();
        var user = _store.GetOrCreateGuest(deviceKey, guestPlanId);
        return Authorize(user, ip, mac);
    }

    public void Logout(IPAddress ip, AccessDenyReason reason = AccessDenyReason.LoggedOut)
    {
        lock (_gate)
        {
            if (_sessions.TryGetValue(IpUtil.ToKey(ip), out var session))
                End(session, reason);
        }
    }

    public void EndUserSessions(long userId, AccessDenyReason reason)
    {
        lock (_gate)
        {
            foreach (var session in _sessions.Values.Where(s => s.User.Id == userId).ToList())
                End(session, reason);
        }
    }

    public void EndAll(AccessDenyReason reason)
    {
        lock (_gate)
        {
            foreach (var session in _sessions.Values.ToList())
                End(session, reason);
        }
    }

    public PortalClientStatus? GetStatus(IPAddress ip)
    {
        var session = TryGet(ip);
        if (session is null)
            return null;

        var now = _clock();
        var (pendingBytes, pendingSeconds) = Pending(session, now);
        var quota = QuotaEvaluator.Evaluate(session.User, session.Plan, now, pendingBytes, pendingSeconds, now - session.StartedUtc);
        var plan = session.Plan;
        return new PortalClientStatus(
            session.User.Username,
            string.IsNullOrWhiteSpace(session.User.DisplayName) ? session.User.Username : session.User.DisplayName,
            plan.Name,
            session.User.IsGuest,
            session.StartedUtc,
            session.DownloadBytes,
            session.UploadBytes,
            session.User.UsedTotalBytes + pendingBytes,
            plan.DataLimitMb > 0 ? plan.DataLimitMb * QuotaEvaluator.BytesPerMb : null,
            quota.RemainingBytes,
            quota.RemainingTime,
            quota.ExpiresUtc,
            plan.DownloadKbps,
            plan.UploadKbps);
    }

    /// <summary>Called once per second: updates rates, flushes usage and ends sessions that ran out of quota.</summary>
    public void Tick()
    {
        var now = _clock();
        lock (_gate)
        {
            foreach (var session in _sessions.Values.ToList())
            {
                UpdateRates(session, now);

                if (now - session.LastFlushUtc >= FlushInterval)
                {
                    Flush(session, now);
                    var fresh = _store.GetUser(session.User.Id);
                    var plan = fresh is null ? null : _store.GetPlan(fresh.PlanId);
                    if (fresh is null)
                    {
                        End(session, AccessDenyReason.AccountDisabled);
                        continue;
                    }
                    if (plan is null)
                    {
                        End(session, AccessDenyReason.PlanMissing);
                        continue;
                    }
                    session.User = fresh;
                    session.ApplyPlan(plan);
                }

                var (pendingBytes, pendingSeconds) = Pending(session, now);
                var quota = QuotaEvaluator.Evaluate(session.User, session.Plan, now, pendingBytes, pendingSeconds, now - session.StartedUtc);
                if (quota.Reason != AccessDenyReason.None)
                    End(session, quota.Reason);
            }

            foreach (var (key, list) in _failures)
            {
                lock (list)
                {
                    list.RemoveAll(t => now - t > FailureWindow);
                    if (list.Count == 0)
                        _failures.TryRemove(key, out _);
                }
            }
        }
    }

    private LoginResult Authorize(UserAccount user, IPAddress ip, string mac)
    {
        lock (_gate)
        {
            var plan = _store.GetPlan(user.PlanId);
            if (plan is null)
                return LoginResult.Fail(AccessDenyReason.PlanMissing);

            var now = _clock();
            var key = IpUtil.ToKey(ip);

            if (_sessions.TryGetValue(key, out var existing))
            {
                End(existing, AccessDenyReason.LoggedOut);
                user = _store.GetUser(user.Id) ?? user;
            }

            var quota = QuotaEvaluator.Evaluate(user, plan, now);
            if (quota.Reason != AccessDenyReason.None)
                return LoginResult.Fail(quota.Reason);

            if (!string.IsNullOrWhiteSpace(mac))
            {
                foreach (var sameDevice in _sessions.Values.Where(s => s.User.Id == user.Id && string.Equals(s.Mac, mac, StringComparison.OrdinalIgnoreCase)).ToList())
                    End(sameDevice, AccessDenyReason.LoggedOut);
            }

            var activeDevices = _sessions.Values.Count(s => s.User.Id == user.Id);
            if (plan.MaxDevices > 0 && activeDevices >= plan.MaxDevices)
                return LoginResult.Fail(AccessDenyReason.DeviceLimit);

            if (user.FirstLoginUtc is null)
            {
                _store.MarkFirstLogin(user.Id, now);
                user.FirstLoginUtc = now;
            }

            var session = new ClientSession(key, IpUtil.FromKey(key), mac, user, plan, now)
            {
                LastFlushUtc = now,
                RateSampleUtc = now,
            };
            _sessions[key] = session;
            SessionStarted?.Invoke(session);
            return LoginResult.Ok;
        }
    }

    private void End(ClientSession session, AccessDenyReason reason)
    {
        if (!_sessions.TryRemove(new KeyValuePair<uint, ClientSession>(session.IpKey, session)))
            return;

        session.Revoke();
        var now = _clock();
        Flush(session, now);
        _store.AddHistory(new SessionHistoryEntry
        {
            UserId = session.User.Id,
            Username = session.User.Username,
            Ip = IpUtil.Format(session.Ip),
            Mac = session.Mac,
            StartedUtc = session.StartedUtc,
            EndedUtc = now,
            DownloadBytes = session.DownloadBytes,
            UploadBytes = session.UploadBytes,
            EndReason = reason.ToString(),
        });
        SessionEnded?.Invoke(session, reason);
    }

    private (long Bytes, long Seconds) Pending(ClientSession session, DateTime now)
    {
        var bytes = session.DownloadBytes - session.FlushedDownload + session.UploadBytes - session.FlushedUpload;
        var seconds = ElapsedSeconds(session, now) - session.FlushedSeconds;
        return (bytes, Math.Max(0, seconds));
    }

    private void Flush(ClientSession session, DateTime now)
    {
        var download = session.DownloadBytes;
        var upload = session.UploadBytes;
        var seconds = ElapsedSeconds(session, now);
        var dDown = download - session.FlushedDownload;
        var dUp = upload - session.FlushedUpload;
        var dSec = seconds - session.FlushedSeconds;
        session.LastFlushUtc = now;
        if (dDown <= 0 && dUp <= 0 && dSec <= 0)
            return;

        _store.AddUsage(session.User.Id, Math.Max(0, dDown), Math.Max(0, dUp), Math.Max(0, dSec));
        session.User.UsedDownloadBytes += Math.Max(0, dDown);
        session.User.UsedUploadBytes += Math.Max(0, dUp);
        session.User.UsedSeconds += Math.Max(0, dSec);
        session.FlushedDownload = download;
        session.FlushedUpload = upload;
        session.FlushedSeconds = seconds;
    }

    private static long ElapsedSeconds(ClientSession session, DateTime now) =>
        (long)Math.Max(0, (now - session.StartedUtc).TotalSeconds);

    private static void UpdateRates(ClientSession session, DateTime now)
    {
        var elapsed = (now - session.RateSampleUtc).TotalSeconds;
        if (elapsed < 0.5)
            return;
        var down = session.DownloadBytes;
        var up = session.UploadBytes;
        session.DownloadRateBps = (down - session.RateSampleDownload) / elapsed;
        session.UploadRateBps = (up - session.RateSampleUpload) / elapsed;
        session.RateSampleDownload = down;
        session.RateSampleUpload = up;
        session.RateSampleUtc = now;
    }

    private bool IsThrottled(uint key)
    {
        if (!_failures.TryGetValue(key, out var list))
            return false;
        var now = _clock();
        lock (list)
            return list.Count(t => now - t <= FailureWindow) >= MaxFailedAttempts;
    }

    private void RegisterFailure(uint key)
    {
        var list = _failures.GetOrAdd(key, _ => new List<DateTime>());
        lock (list)
            list.Add(_clock());
    }
}
