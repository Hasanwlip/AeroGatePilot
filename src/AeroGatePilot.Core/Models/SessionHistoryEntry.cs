namespace AeroGatePilot.Core.Models;

public sealed class SessionHistoryEntry
{
    public long Id { get; set; }
    public long UserId { get; set; }
    public string Username { get; set; } = "";
    public string Ip { get; set; } = "";
    public string Mac { get; set; } = "";
    public DateTime StartedUtc { get; set; }
    public DateTime EndedUtc { get; set; }
    public long DownloadBytes { get; set; }
    public long UploadBytes { get; set; }
    public string EndReason { get; set; } = "";
}
