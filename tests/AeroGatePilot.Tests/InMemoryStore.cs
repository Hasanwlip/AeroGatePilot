using AeroGatePilot.Core.Access;
using AeroGatePilot.Core.Models;
using AeroGatePilot.Core.Security;

namespace AeroGatePilot.Tests;

internal sealed class InMemoryStore : IAccountStore
{
    private long _nextId = 1;

    public Dictionary<long, UserAccount> Users { get; } = [];
    public Dictionary<long, Plan> Plans { get; } = [];
    public List<SessionHistoryEntry> History { get; } = [];

    public Plan AddPlan(Plan plan)
    {
        plan.Id = _nextId++;
        Plans[plan.Id] = plan;
        return plan;
    }

    public UserAccount AddUser(string username, string password, Plan plan)
    {
        var user = new UserAccount
        {
            Id = _nextId++,
            Username = username,
            PasswordHash = PasswordHasher.Hash(password),
            PlanId = plan.Id,
        };
        Users[user.Id] = user;
        return user;
    }

    public UserAccount? FindUserByName(string username) =>
        Users.Values.FirstOrDefault(u => u.Username.Equals(username, StringComparison.OrdinalIgnoreCase))?.Clone();

    public UserAccount? GetUser(long id) => Users.TryGetValue(id, out var u) ? u.Clone() : null;

    public Plan? GetPlan(long id) => Plans.TryGetValue(id, out var p) ? p.Clone() : null;

    public void AddUsage(long userId, long downloadBytes, long uploadBytes, long seconds)
    {
        var u = Users[userId];
        u.UsedDownloadBytes += downloadBytes;
        u.UsedUploadBytes += uploadBytes;
        u.UsedSeconds += seconds;
    }

    public void MarkFirstLogin(long userId, DateTime utc) => Users[userId].FirstLoginUtc ??= utc;

    public UserAccount GetOrCreateGuest(string deviceKey, long planId)
    {
        var name = "guest:" + deviceKey;
        var existing = Users.Values.FirstOrDefault(u => u.Username == name);
        if (existing is not null)
            return existing.Clone();
        var guest = new UserAccount { Id = _nextId++, Username = name, PlanId = planId, IsGuest = true, PasswordHash = "!" };
        Users[guest.Id] = guest;
        return guest.Clone();
    }

    public void AddHistory(SessionHistoryEntry entry) => History.Add(entry);
}
