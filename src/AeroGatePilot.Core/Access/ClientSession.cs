using System.Net;
using AeroGatePilot.Core.Models;

namespace AeroGatePilot.Core.Access;

/// <summary>An authorized device. Byte counters are updated from packet threads without locking.</summary>
public sealed class ClientSession
{
    private long _download;
    private long _upload;
    private long _droppedPackets;
    private volatile bool _revoked;

    internal ClientSession(uint ipKey, IPAddress ip, string mac, UserAccount user, Plan plan, DateTime startedUtc)
    {
        IpKey = ipKey;
        Ip = ip;
        Mac = mac;
        User = user;
        StartedUtc = startedUtc;
        ApplyPlan(plan);
        Plan = plan;
    }

    public uint IpKey { get; }
    public IPAddress Ip { get; }
    public string Mac { get; }
    public DateTime StartedUtc { get; }
    public UserAccount User { get; internal set; }
    public Plan Plan { get; private set; }
    public TokenBucket DownloadLimiter { get; private set; } = null!;
    public TokenBucket UploadLimiter { get; private set; } = null!;

    public long DownloadBytes => Interlocked.Read(ref _download);
    public long UploadBytes => Interlocked.Read(ref _upload);
    public long DroppedPackets => Interlocked.Read(ref _droppedPackets);
    public bool Revoked => _revoked;

    public double DownloadRateBps { get; internal set; }
    public double UploadRateBps { get; internal set; }

    internal long FlushedDownload { get; set; }
    internal long FlushedUpload { get; set; }
    internal long FlushedSeconds { get; set; }
    internal long RateSampleDownload { get; set; }
    internal long RateSampleUpload { get; set; }
    internal DateTime RateSampleUtc { get; set; }
    internal DateTime LastFlushUtc { get; set; }

    public void AddDownload(int bytes) => Interlocked.Add(ref _download, bytes);
    public void AddUpload(int bytes) => Interlocked.Add(ref _upload, bytes);
    public void AddDropped() => Interlocked.Increment(ref _droppedPackets);

    internal void Revoke() => _revoked = true;

    internal void ApplyPlan(Plan plan)
    {
        if (Plan is null || Plan.DownloadKbps != plan.DownloadKbps)
            DownloadLimiter = TokenBucket.FromKbps(plan.DownloadKbps);
        if (Plan is null || Plan.UploadKbps != plan.UploadKbps)
            UploadLimiter = TokenBucket.FromKbps(plan.UploadKbps);
        Plan = plan;
    }
}
