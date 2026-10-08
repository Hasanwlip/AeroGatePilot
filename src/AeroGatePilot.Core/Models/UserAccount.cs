namespace AeroGatePilot.Core.Models;

public sealed class UserAccount
{
    public long Id { get; set; }
    public string Username { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public long PlanId { get; set; }
    public bool Enabled { get; set; } = true;
    public bool IsGuest { get; set; }
    public long UsedDownloadBytes { get; set; }
    public long UsedUploadBytes { get; set; }
    public long UsedSeconds { get; set; }
    public DateTime? FirstLoginUtc { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public string Notes { get; set; } = "";

    public long UsedTotalBytes => UsedDownloadBytes + UsedUploadBytes;

    public UserAccount Clone() => (UserAccount)MemberwiseClone();
}
