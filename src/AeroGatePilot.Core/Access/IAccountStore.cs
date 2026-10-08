using AeroGatePilot.Core.Models;

namespace AeroGatePilot.Core.Access;

public interface IAccountStore
{
    UserAccount? FindUserByName(string username);
    UserAccount? GetUser(long id);
    Plan? GetPlan(long id);
    void AddUsage(long userId, long downloadBytes, long uploadBytes, long seconds);
    void MarkFirstLogin(long userId, DateTime utc);
    UserAccount GetOrCreateGuest(string deviceKey, long planId);
    void AddHistory(SessionHistoryEntry entry);
}
