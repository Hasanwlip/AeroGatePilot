using System.Net;
using AeroGatePilot.Core.Access;
using AeroGatePilot.Core.Models;
using AeroGatePilot.Core.Security;

namespace AeroGatePilot.Tests;

public class PasswordHasherTests
{
    [Fact]
    public void Verifies_correct_password_only()
    {
        var hash = PasswordHasher.Hash("s3cret!");
        Assert.True(PasswordHasher.Verify("s3cret!", hash));
        Assert.False(PasswordHasher.Verify("S3cret!", hash));
        Assert.False(PasswordHasher.Verify("s3cret!", "garbage"));
    }

    [Fact]
    public void Same_password_gets_different_salts()
    {
        Assert.NotEqual(PasswordHasher.Hash("abc"), PasswordHasher.Hash("abc"));
    }
}

public class QuotaEvaluatorTests
{
    private static readonly DateTime Now = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Unlimited_plan_allows_access()
    {
        var status = QuotaEvaluator.Evaluate(new UserAccount(), new Plan(), Now);
        Assert.Equal(AccessDenyReason.None, status.Reason);
        Assert.Null(status.RemainingBytes);
        Assert.Null(status.RemainingTime);
    }

    [Fact]
    public void Data_limit_counts_stored_and_pending_bytes()
    {
        var plan = new Plan { DataLimitMb = 10 };
        var user = new UserAccount { UsedDownloadBytes = 6 * QuotaEvaluator.BytesPerMb };

        var ok = QuotaEvaluator.Evaluate(user, plan, Now, pendingBytes: 3 * QuotaEvaluator.BytesPerMb);
        Assert.Equal(AccessDenyReason.None, ok.Reason);
        Assert.Equal(1 * QuotaEvaluator.BytesPerMb, ok.RemainingBytes);

        var exhausted = QuotaEvaluator.Evaluate(user, plan, Now, pendingBytes: 4 * QuotaEvaluator.BytesPerMb);
        Assert.Equal(AccessDenyReason.DataExhausted, exhausted.Reason);
        Assert.Equal(0, exhausted.RemainingBytes);
    }

    [Fact]
    public void Total_time_limit()
    {
        var plan = new Plan { TimeLimitMinutes = 60 };
        var user = new UserAccount { UsedSeconds = 50 * 60 };
        Assert.Equal(AccessDenyReason.None, QuotaEvaluator.Evaluate(user, plan, Now, pendingSeconds: 9 * 60).Reason);
        Assert.Equal(AccessDenyReason.TimeExhausted, QuotaEvaluator.Evaluate(user, plan, Now, pendingSeconds: 10 * 60).Reason);
    }

    [Fact]
    public void Session_limit_ends_session_but_not_account()
    {
        var plan = new Plan { SessionLimitMinutes = 30 };
        var user = new UserAccount();
        Assert.Equal(AccessDenyReason.SessionTimeLimit, QuotaEvaluator.Evaluate(user, plan, Now, sessionDuration: TimeSpan.FromMinutes(30)).Reason);
        Assert.Equal(AccessDenyReason.None, QuotaEvaluator.Evaluate(user, plan, Now).Reason);
    }

    [Fact]
    public void Validity_counts_from_first_login()
    {
        var plan = new Plan { ValidityDays = 1 };
        var fresh = new UserAccount();
        Assert.Equal(AccessDenyReason.None, QuotaEvaluator.Evaluate(fresh, plan, Now).Reason);

        var started = new UserAccount { FirstLoginUtc = Now.AddHours(-23) };
        var status = QuotaEvaluator.Evaluate(started, plan, Now);
        Assert.Equal(AccessDenyReason.None, status.Reason);
        Assert.Equal(TimeSpan.FromHours(1), status.RemainingTime);

        var expired = new UserAccount { FirstLoginUtc = Now.AddDays(-2) };
        Assert.Equal(AccessDenyReason.AccountExpired, QuotaEvaluator.Evaluate(expired, plan, Now).Reason);
    }

    [Fact]
    public void Disabled_account_is_rejected()
    {
        Assert.Equal(AccessDenyReason.AccountDisabled, QuotaEvaluator.Evaluate(new UserAccount { Enabled = false }, new Plan(), Now).Reason);
    }
}

public class TokenBucketTests
{
    [Fact]
    public void Zero_rate_is_unlimited()
    {
        var bucket = TokenBucket.FromKbps(0);
        Assert.True(bucket.Unlimited);
        Assert.True(bucket.TryConsume(10_000_000));
    }

    [Fact]
    public void Enforces_rate_over_time()
    {
        long ticks = 0;
        // 800 Kbps = 100,000 bytes/s, clock in milliseconds.
        var bucket = TokenBucket.FromKbps(800, () => ticks, ticksPerSecond: 1000);
        Assert.Equal(100_000, bucket.BytesPerSecond);

        var sent = 0;
        while (bucket.TryConsume(1500))
            sent += 1500;
        Assert.InRange(sent, bucket.Capacity - 1500, bucket.Capacity);

        ticks += 1000;
        var afterOneSecond = 0;
        while (bucket.TryConsume(1500))
            afterOneSecond += 1500;
        Assert.InRange(afterOneSecond, bucket.Capacity - 1500, bucket.Capacity);

        ticks += 100;
        var afterTenth = 0;
        while (bucket.TryConsume(1500))
            afterTenth += 1500;
        // 0.1 s refills 10,000 bytes on top of the < 1,500 bytes left over.
        Assert.InRange(afterTenth, 10_000 - 1500, 10_000 + 1500);
    }
}

public class SessionManagerTests
{
    private static readonly IPAddress Client1 = IPAddress.Parse("192.168.137.10");
    private static readonly IPAddress Client2 = IPAddress.Parse("192.168.137.11");
    private static readonly IPAddress Client3 = IPAddress.Parse("192.168.137.12");

    private DateTime _now = new(2026, 1, 1, 8, 0, 0, DateTimeKind.Utc);

    private (InMemoryStore Store, SessionManager Manager) Create(Plan plan, out UserAccount user)
    {
        var store = new InMemoryStore();
        store.AddPlan(plan);
        user = store.AddUser("ali", "pass1234", plan);
        var manager = new SessionManager(store, () => _now) { FlushInterval = TimeSpan.FromSeconds(10) };
        return (store, manager);
    }

    [Fact]
    public void Login_with_valid_credentials_creates_session()
    {
        var (store, manager) = Create(new Plan { Name = "p" }, out var user);
        var result = manager.Login("ALI", "pass1234", Client1, "AA:BB:CC:DD:EE:01");
        Assert.True(result.Success);
        Assert.NotNull(manager.TryGet(Client1));
        Assert.Equal(_now, store.Users[user.Id].FirstLoginUtc);
    }

    [Fact]
    public void Wrong_password_is_rejected_and_throttled()
    {
        var (_, manager) = Create(new Plan(), out _);
        for (var i = 0; i < 5; i++)
            Assert.Equal(AccessDenyReason.InvalidCredentials, manager.Login("ali", "nope", Client1, "").Reason);
        Assert.Equal(AccessDenyReason.TooManyAttempts, manager.Login("ali", "pass1234", Client1, "").Reason);

        _now = _now.AddMinutes(2);
        Assert.True(manager.Login("ali", "pass1234", Client1, "").Success);
    }

    [Fact]
    public void Device_limit_is_enforced_but_same_device_can_reconnect()
    {
        var (_, manager) = Create(new Plan { MaxDevices = 2 }, out _);
        Assert.True(manager.Login("ali", "pass1234", Client1, "MAC-1").Success);
        Assert.True(manager.Login("ali", "pass1234", Client2, "MAC-2").Success);
        Assert.Equal(AccessDenyReason.DeviceLimit, manager.Login("ali", "pass1234", Client3, "MAC-3").Reason);

        // MAC-1 moved to a new IP: replaces its old session instead of counting twice.
        Assert.True(manager.Login("ali", "pass1234", Client3, "MAC-1").Success);
        Assert.Null(manager.TryGet(Client1));
        Assert.Equal(2, manager.Count);
    }

    [Fact]
    public void Session_ends_when_data_runs_out_and_usage_is_persisted()
    {
        var (store, manager) = Create(new Plan { DataLimitMb = 1 }, out var user);
        AccessDenyReason? ended = null;
        manager.SessionEnded += (_, reason) => ended = reason;

        Assert.True(manager.Login("ali", "pass1234", Client1, "").Success);
        var session = manager.TryGet(Client1)!;
        session.AddDownload(700 * 1024);
        session.AddUpload(200 * 1024);
        _now = _now.AddSeconds(1);
        manager.Tick();
        Assert.Null(ended);

        session.AddDownload(200 * 1024);
        _now = _now.AddSeconds(1);
        manager.Tick();

        Assert.Equal(AccessDenyReason.DataExhausted, ended);
        Assert.True(session.Revoked);
        Assert.Null(manager.TryGet(Client1));
        Assert.Equal(900 * 1024, store.Users[user.Id].UsedDownloadBytes);
        Assert.Equal(200 * 1024, store.Users[user.Id].UsedUploadBytes);
        Assert.Single(store.History);

        Assert.Equal(AccessDenyReason.DataExhausted, manager.Login("ali", "pass1234", Client1, "").Reason);
    }

    [Fact]
    public void Session_time_limit_and_total_time_are_tracked()
    {
        var (store, manager) = Create(new Plan { SessionLimitMinutes = 1, TimeLimitMinutes = 2 }, out var user);
        Assert.True(manager.Login("ali", "pass1234", Client1, "").Success);
        _now = _now.AddSeconds(61);
        manager.Tick();
        Assert.Null(manager.TryGet(Client1));
        Assert.Equal(61, store.Users[user.Id].UsedSeconds);

        Assert.True(manager.Login("ali", "pass1234", Client1, "").Success);
        _now = _now.AddSeconds(60);
        manager.Tick();
        Assert.Null(manager.TryGet(Client1));
        Assert.Equal(AccessDenyReason.TimeExhausted, manager.Login("ali", "pass1234", Client1, "").Reason);
    }

    [Fact]
    public void Disabling_account_kicks_on_next_flush()
    {
        var (store, manager) = Create(new Plan(), out var user);
        Assert.True(manager.Login("ali", "pass1234", Client1, "").Success);
        store.Users[user.Id].Enabled = false;
        _now = _now.AddSeconds(11);
        manager.Tick();
        Assert.Null(manager.TryGet(Client1));
    }

    [Fact]
    public void Guest_login_creates_one_account_per_device()
    {
        var store = new InMemoryStore();
        var guestPlan = store.AddPlan(new Plan { Name = "guest", SessionLimitMinutes = 30 });
        var manager = new SessionManager(store, () => _now);

        Assert.True(manager.GuestLogin(Client1, "aa:bb", guestPlan.Id).Success);
        manager.Logout(Client1);
        Assert.True(manager.GuestLogin(Client1, "AA:BB", guestPlan.Id).Success);
        Assert.Single(store.Users.Values, u => u.IsGuest);
        Assert.Equal(AccessDenyReason.PlanMissing, manager.GuestLogin(Client2, "cc", 999).Reason);
    }

    [Fact]
    public void Plan_speed_change_rebuilds_limiters()
    {
        var (store, manager) = Create(new Plan { DownloadKbps = 1000 }, out var user);
        Assert.True(manager.Login("ali", "pass1234", Client1, "").Success);
        var session = manager.TryGet(Client1)!;
        Assert.Equal(125_000, session.DownloadLimiter.BytesPerSecond);

        store.Plans[user.PlanId].DownloadKbps = 2000;
        _now = _now.AddSeconds(11);
        manager.Tick();
        Assert.Equal(250_000, session.DownloadLimiter.BytesPerSecond);
    }
}
