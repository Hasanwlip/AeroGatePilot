using System.Collections.Concurrent;
using AeroGatePilot.Core.Access;
using AeroGatePilot.Core.Filtering;
using AeroGatePilot.Core.Net;
using AeroGatePilot.Infrastructure.Proxy;

namespace AeroGatePilot.Infrastructure.Network;

public sealed record PacketFilterOptions(uint GatewayIp, Ipv4Subnet Subnet, int PortalPort, bool IsolateHost)
{
    /// <summary>Interface index of the hotspot adapter, used to inject packets towards the relay.</summary>
    public uint HotspotIfIndex { get; init; }

    /// <summary>Port of the transparent VPN relay on the gateway; 0 when no upstream VPN is configured.</summary>
    public int RelayPort { get; init; }

    /// <summary>Route every user through the VPN, not only plans that ask for it.</summary>
    public bool ProxyAllUsers { get; init; }
}

public sealed class PacketFilterStats
{
    internal long ForwardedValue;
    internal long BlockedUnauthorizedValue;
    internal long RateLimitedValue;
    internal long DnsRedirectedValue;
    internal long PortalRequestsValue;
    internal long HostBlockedValue;
    internal long SitesBlockedValue;
    internal long ProxiedValue;

    public long Forwarded => Interlocked.Read(ref ForwardedValue);
    public long BlockedUnauthorized => Interlocked.Read(ref BlockedUnauthorizedValue);
    public long RateLimited => Interlocked.Read(ref RateLimitedValue);
    public long DnsRedirected => Interlocked.Read(ref DnsRedirectedValue);
    public long PortalRequests => Interlocked.Read(ref PortalRequestsValue);
    public long HostBlocked => Interlocked.Read(ref HostBlockedValue);
    public long SitesBlocked => Interlocked.Read(ref SitesBlockedValue);
    public long Proxied => Interlocked.Read(ref ProxiedValue);
}

/// <summary>
/// Enforces the captive portal with WinDivert:
/// <list type="bullet">
/// <item>Forwarded traffic of the hotspot subnet passes only for authorized clients, policed by their token buckets and counted for quotas.</item>
/// <item>DNS queries of unauthorized clients are answered with the gateway address so every HTTP request reaches the portal.</item>
/// <item>HTTP to the gateway (port 80) is transparently mapped to the portal's internal port.</item>
/// <item>Optionally every other connection from Wi-Fi clients to this PC is blocked.</item>
/// <item>Plans with website rules: DNS names are answered NXDOMAIN and TCP connections are reset by TLS SNI / HTTP Host.</item>
/// <item>VPN-routed users: new TCP connections are redirected to the local relay, which carries them through the VPN port.</item>
/// </list>
/// </summary>
public sealed class PacketFilter : IDisposable
{
    private const int BufferSize = 0xFFFF + 64;
    private const int ForwardWorkers = 2;
    private const ushort HttpPort = 80;
    private const ushort HttpsPort = 443;
    private const ushort DnsPort = 53;
    private const ushort DnsOverTlsPort = 853;
    private const ushort DhcpServerPort = 67;
    private const int SniffLimit = 8 * 1024;
    private static readonly long FlowIdleMs = (long)TimeSpan.FromMinutes(10).TotalMilliseconds;
    private static readonly long PruneIntervalMs = (long)TimeSpan.FromSeconds(30).TotalMilliseconds;

    private readonly SessionManager _sessions;
    private readonly PacketFilterOptions _options;
    private readonly AppLog _log;
    private readonly HostAddressCache _names;
    private readonly RedirectTable? _redirects;
    private readonly ConcurrentDictionary<FlowKey, FlowState> _flows = new();
    private readonly ConcurrentDictionary<string, long> _blockLogged = new();
    private readonly List<Thread> _threads = [];
    private WinDivertHandle? _forward;
    private WinDivertHandle? _local;
    private WinDivertHandle? _inject;
    private volatile bool _running;
    private uint _hotspotIfIndex;
    private long _lastPrune = Environment.TickCount64;

    public PacketFilter(SessionManager sessions, PacketFilterOptions options, AppLog log, HostAddressCache names, RedirectTable? redirects = null)
    {
        _sessions = sessions;
        _options = options;
        _log = log;
        _names = names;
        _redirects = options.RelayPort > 0 ? redirects : null;
        _hotspotIfIndex = options.HotspotIfIndex;
    }

    public PacketFilterStats Stats { get; } = new();

    public void Start()
    {
        if (!WinDivertNative.IsAvailable(out var error))
            throw new InvalidOperationException(error);

        var first = IpUtil.FromKey(_options.Subnet.First);
        var last = IpUtil.FromKey(_options.Subnet.Last);
        var gw = IpUtil.FromKey(_options.GatewayIp);
        var inSubnetSrc = $"ip.SrcAddr >= {first} and ip.SrcAddr <= {last}";
        var inSubnetDst = $"ip.DstAddr >= {first} and ip.DstAddr <= {last}";
        var relay = _redirects is null ? "" : $" or tcp.SrcPort == {_options.RelayPort}";
        // WinDivert cannot negate a group, and a tcp.* test is false for non-TCP packets, hence "not tcp or".
        var notRelayInbound = _redirects is null ? "" : $" and (not tcp or tcp.DstPort != {_options.RelayPort})";

        var forwardFilter = $"ip and (({inSubnetSrc}) or ({inSubnetDst}))";
        var localFilter =
            $"ip and not loopback and ((inbound and {inSubnetSrc} and ip.SrcAddr != {gw} and ip.DstAddr == {gw}{notRelayInbound}) or " +
            $"(outbound and ip.SrcAddr == {gw} and {inSubnetDst} and ip.DstAddr != {gw} and (tcp.SrcPort == {_options.PortalPort} or udp.SrcPort == {DnsPort}{relay})))";

        try
        {
            _inject = WinDivertHandle.Open("false", WinDivertLayer.Network, 0, WinDivertOpenFlags.SendOnly);
            _forward = WinDivertHandle.Open(forwardFilter, WinDivertLayer.NetworkForward);
            _local = WinDivertHandle.Open(localFilter, WinDivertLayer.Network);
        }
        catch
        {
            Dispose();
            throw;
        }

        _running = true;
        for (var i = 0; i < ForwardWorkers; i++)
            StartThread($"AeroGate forward {i + 1}", ForwardLoop);
        StartThread("AeroGate local", LocalLoop);
        _log.Info($"Packet filter active on {_options.Subnet} (gateway {gw}).");
    }

    public void Stop()
    {
        _running = false;
        _forward?.Shutdown();
        _local?.Shutdown();
        foreach (var thread in _threads)
            thread.Join(TimeSpan.FromSeconds(2));
        _threads.Clear();
        Dispose();
    }

    public void Dispose()
    {
        _forward?.Dispose();
        _local?.Dispose();
        _inject?.Dispose();
        _forward = null;
        _local = null;
        _inject = null;
    }

    private void StartThread(string name, Action body)
    {
        var thread = new Thread(() =>
        {
            try
            {
                body();
            }
            catch (Exception ex) when (_running)
            {
                _log.Error($"{name} stopped unexpectedly", ex);
            }
        })
        {
            IsBackground = true,
            Name = name,
            Priority = ThreadPriority.AboveNormal,
        };
        _threads.Add(thread);
        thread.Start();
    }

    private void ForwardLoop()
    {
        var handle = _forward!;
        var buffer = new byte[BufferSize];
        var reply = new byte[1500];
        var addr = new WinDivertAddress();

        while (_running)
        {
            if (!handle.Receive(buffer, out var length, ref addr))
            {
                if (!_running)
                    return;
                continue;
            }
            PruneIfDue();
            if (!Ipv4Packet.TryParse(buffer.AsSpan(0, length), out var packet))
            {
                handle.Send(buffer, length, ref addr);
                continue;
            }

            var src = packet.Source;
            var dst = packet.Destination;
            var fromClient = _options.Subnet.Contains(src) && src != _options.GatewayIp;
            var toClient = _options.Subnet.Contains(dst) && dst != _options.GatewayIp;

            if (fromClient)
            {
                var session = _sessions.TryGet(src);
                if (session is null || session.Revoked)
                {
                    if (packet.Protocol == IpProtocol.Udp && packet.HasPorts && packet.DestinationPort == DnsPort)
                        SendDnsRedirect(buffer, length, reply);
                    else
                        Interlocked.Increment(ref Stats.BlockedUnauthorizedValue);
                    continue;
                }
                if (packet.HasPorts && !AllowClientPacket(session, packet, buffer, length, reply))
                    continue;
                if (!session.UploadLimiter.TryConsume(length))
                {
                    session.AddDropped();
                    Interlocked.Increment(ref Stats.RateLimitedValue);
                    continue;
                }
                session.AddUpload(length);

                if (packet.Protocol == IpProtocol.Tcp && packet.HasPorts && ShouldRedirect(session, packet))
                {
                    RedirectToRelay(buffer, length);
                    continue;
                }
            }
            else if (toClient)
            {
                var session = _sessions.TryGet(dst);
                if (session is null || session.Revoked)
                {
                    Interlocked.Increment(ref Stats.BlockedUnauthorizedValue);
                    continue;
                }
                if (packet.HasPorts)
                {
                    if (packet.Protocol == IpProtocol.Udp && packet.SourcePort == DnsPort)
                        LearnDnsAnswers(packet);
                    else if (packet.Protocol == IpProtocol.Tcp && SitePolicy.IsFiltered(session.Plan)
                             && _flows.TryGetValue(new FlowKey(dst, packet.DestinationPort, src, packet.SourcePort), out var flow)
                             && flow.Verdict == FlowVerdict.Blocked)
                        continue;
                }
                if (!session.DownloadLimiter.TryConsume(length))
                {
                    session.AddDropped();
                    Interlocked.Increment(ref Stats.RateLimitedValue);
                    continue;
                }
                session.AddDownload(length);
            }

            Interlocked.Increment(ref Stats.ForwardedValue);
            handle.Send(buffer, length, ref addr);
        }
    }

    private void LocalLoop()
    {
        var handle = _local!;
        var buffer = new byte[BufferSize];
        var reply = new byte[1500];
        var addr = new WinDivertAddress();
        var portalPort = (ushort)_options.PortalPort;
        var relayPort = (ushort)_options.RelayPort;

        while (_running)
        {
            if (!handle.Receive(buffer, out var length, ref addr))
            {
                if (!_running)
                    return;
                continue;
            }
            if (!Ipv4Packet.TryParse(buffer.AsSpan(0, length), out var packet))
            {
                handle.Send(buffer, length, ref addr);
                continue;
            }

            if (addr.Outbound)
            {
                if (packet.Protocol == IpProtocol.Tcp && packet.HasPorts && packet.SourcePort == portalPort)
                {
                    // Portal reply: present it to the client as coming from port 80.
                    packet.SetSourcePort(HttpPort);
                    handle.Send(buffer, length, ref addr, recalcChecksums: true);
                }
                else if (_redirects is not null && packet.Protocol == IpProtocol.Tcp && packet.HasPorts && packet.SourcePort == relayPort)
                {
                    SendRelayReply(handle, buffer, length, addr);
                }
                else
                {
                    if (packet.Protocol == IpProtocol.Udp && packet.HasPorts && packet.SourcePort == DnsPort)
                        LearnDnsAnswers(packet);
                    handle.Send(buffer, length, ref addr);
                }
                continue;
            }

            _hotspotIfIndex = addr.IfIdx;
            var session = _sessions.TryGet(packet.Source);
            var authorized = session is { Revoked: false };
            var protocol = packet.Protocol;
            var port = packet.HasPorts ? packet.DestinationPort : (ushort)0;

            if (protocol == IpProtocol.Icmp || protocol == IpProtocol.Udp && port == DhcpServerPort)
            {
                handle.Send(buffer, length, ref addr);
                continue;
            }

            if (port == DnsPort)
            {
                if (authorized)
                {
                    if (protocol != IpProtocol.Udp || AllowDnsQuery(session!, packet, buffer, length, reply))
                        handle.Send(buffer, length, ref addr);
                }
                else if (protocol == IpProtocol.Udp)
                {
                    SendDnsRedirect(buffer, length, reply);
                }
                continue;
            }

            if (protocol == IpProtocol.Tcp && port == HttpPort)
            {
                packet.SetDestinationPort(portalPort);
                Interlocked.Increment(ref Stats.PortalRequestsValue);
                handle.Send(buffer, length, ref addr, recalcChecksums: true);
                continue;
            }

            if (protocol == IpProtocol.Tcp && port == portalPort)
            {
                // Direct access to the internal port would confuse the port mapping above.
                Interlocked.Increment(ref Stats.HostBlockedValue);
                continue;
            }

            if (_options.IsolateHost)
            {
                Interlocked.Increment(ref Stats.HostBlockedValue);
                continue;
            }

            handle.Send(buffer, length, ref addr);
        }
    }

    // ---------- Website rules ----------

    /// <summary>Applies website rules and VPN restrictions to a client packet. Returns false when it must not be forwarded.</summary>
    private bool AllowClientPacket(ClientSession session, Ipv4Packet packet, byte[] buffer, int length, byte[] reply)
    {
        var filtered = SitePolicy.IsFiltered(session.Plan);
        var proxied = UsesProxy(session);
        if (!filtered && !proxied)
            return true;

        var port = packet.DestinationPort;
        if (packet.Protocol == IpProtocol.Udp)
        {
            if (port == DnsPort)
                return !filtered || AllowDnsQuery(session, packet, buffer, length, reply);
            // QUIC / DNS-over-QUIC: browsers fall back to TCP, where the site name can be checked and the VPN applies.
            return port is not (HttpsPort or DnsOverTlsPort);
        }
        if (packet.Protocol != IpProtocol.Tcp || !filtered)
            return true;
        if (port == DnsOverTlsPort)
        {
            SendReset(buffer, length, reply);
            return false;
        }
        return AllowTcpFlow(session, packet, buffer, length, reply);
    }

    private bool AllowDnsQuery(ClientSession session, Ipv4Packet packet, byte[] buffer, int length, byte[] reply)
    {
        var plan = session.Plan;
        if (!SitePolicy.IsFiltered(plan) || !packet.HasPorts)
            return true;
        var dns = packet.UdpData;
        if (DnsMessage.IsResponse(dns) || !DnsMessage.TryReadQuestion(dns, out var name, out var type))
            return true;

        if (!SitePolicy.AllowsHost(plan, name))
        {
            Inject(reply, DnsRedirect.BuildNxDomain(buffer.AsSpan(0, length), reply));
            NoteBlocked(session, name);
            return false;
        }
        if (type == DnsMessage.TypeHttps)
        {
            // An empty answer keeps browsers from using Encrypted Client Hello, which would hide the site name.
            Inject(reply, DnsRedirect.BuildEmpty(buffer.AsSpan(0, length), reply));
            return false;
        }
        return true;
    }

    private bool AllowTcpFlow(ClientSession session, Ipv4Packet packet, byte[] buffer, int length, byte[] reply)
    {
        var key = new FlowKey(packet.Source, packet.SourcePort, packet.Destination, packet.DestinationPort);
        var flow = _flows.GetOrAdd(key, static _ => new FlowState());
        flow.LastSeen = Environment.TickCount64;

        if (flow.Verdict == FlowVerdict.Pending)
        {
            var data = packet.TcpData;
            if (!data.IsEmpty)
            {
                lock (flow)
                {
                    if (flow.Verdict == FlowVerdict.Pending)
                        Decide(session, flow, data, packet.Destination);
                }
            }
        }

        if (flow.Verdict != FlowVerdict.Blocked)
        {
            if ((packet.TcpFlags & TcpFlag.Rst) != 0)
                _flows.TryRemove(key, out _);
            return true;
        }

        if ((packet.TcpFlags & TcpFlag.Rst) == 0)
            SendReset(buffer, length, reply);
        if (!flow.Reported)
        {
            flow.Reported = true;
            NoteBlocked(session, flow.Host ?? _names.NamesFor(packet.Destination).LastOrDefault() ?? IpUtil.Format(packet.Destination));
        }
        return false;
    }

    private void Decide(ClientSession session, FlowState flow, ReadOnlySpan<byte> data, uint server)
    {
        var plan = session.Plan;
        var sniffed = flow.Append(data, SniffLimit);
        var status = StreamSniffer.TryGetHost(sniffed, out var host);
        if (status == SniffStatus.NeedMore && sniffed.Length < SniffLimit)
            return;

        if (status == SniffStatus.Found)
        {
            flow.Host = host;
            flow.Verdict = SitePolicy.AllowsHost(plan, host!) ? FlowVerdict.Allowed : FlowVerdict.Blocked;
        }
        else
        {
            flow.Verdict = SitePolicy.AllowsAddress(plan, _names.NamesFor(server)) ? FlowVerdict.Allowed : FlowVerdict.Blocked;
        }
        flow.Release();
    }

    private void LearnDnsAnswers(Ipv4Packet packet)
    {
        var dns = packet.UdpData;
        if (!DnsMessage.IsResponse(dns))
            return;
        foreach (var record in DnsMessage.ReadAddresses(dns))
            _names.Add(record.Address, record.Name, record.Ttl);
    }

    private void NoteBlocked(ClientSession session, string site)
    {
        Interlocked.Increment(ref Stats.SitesBlockedValue);
        var key = $"{session.User.Id}|{site}";
        var now = Environment.TickCount64;
        if (_blockLogged.TryGetValue(key, out var last) && now - last < 60_000)
            return;
        _blockLogged[key] = now;
        _log.Info($"Blocked {site} for {session.User.Username} ({IpUtil.Format(session.Ip)}) — plan \"{session.Plan.Name}\".");
    }

    // ---------- VPN relay ----------

    private bool UsesProxy(ClientSession session) =>
        _redirects is not null && (_options.ProxyAllUsers || session.Plan.UseUpstreamProxy);

    private bool ShouldRedirect(ClientSession session, Ipv4Packet packet)
    {
        if (!UsesProxy(session))
            return false;
        // Already redirected: keep sending every segment of this client port to the relay
        // (do not require the destination IP to still match — some apps retry other addresses).
        if (_redirects!.TryGet(packet.Source, packet.SourcePort, out _, out _))
            return true;
        // Only new connections move to the VPN; ones opened before it was enabled finish directly.
        if ((packet.TcpFlags & (TcpFlag.Syn | TcpFlag.Ack)) != TcpFlag.Syn)
            return false;
        var server = packet.Destination;
        var port = packet.DestinationPort;
        if (port is DnsPort or DnsOverTlsPort)
            return false;
        // Local networks behind this PC stay direct, unless DNS pointed a site name there (typical of DNS hijacking).
        return !IpUtil.IsPrivate(server) || _names.NamesFor(server).Count > 0;
    }

    /// <summary>Delivers a client packet to the local relay, as if the client had connected to gateway:relayPort.</summary>
    private void RedirectToRelay(byte[] buffer, int length)
    {
        Ipv4Packet.TryParse(buffer.AsSpan(0, length), out var packet);
        if (!_redirects!.TryGet(packet.Source, packet.SourcePort, out _, out _))
            _redirects.Set(packet.Source, packet.SourcePort, packet.Destination, packet.DestinationPort);
        if ((packet.TcpFlags & TcpFlag.Syn) != 0)
            Interlocked.Increment(ref Stats.ProxiedValue);
        packet.SetDestination(_options.GatewayIp);
        packet.SetDestinationPort((ushort)_options.RelayPort);

        var addr = new WinDivertAddress
        {
            Layer = WinDivertLayer.Network,
            Outbound = false,
            IfIdx = _hotspotIfIndex,
            SubIfIdx = 0,
        };
        if (_inject is not null && _inject.Send(buffer, length, ref addr, recalcChecksums: true))
            Interlocked.Increment(ref Stats.ForwardedValue);
    }

    /// <summary>A relay packet to a client: restore the original server as its source and account it as download.</summary>
    private void SendRelayReply(WinDivertHandle handle, byte[] buffer, int length, WinDivertAddress addr)
    {
        Ipv4Packet.TryParse(buffer.AsSpan(0, length), out var packet);
        if (!_redirects!.TryGet(packet.Destination, packet.DestinationPort, out var server, out var serverPort))
            return;

        // Always deliver the reply (including the SYN-ACK). Quotas apply when a session is present.
        var session = _sessions.TryGet(packet.Destination);
        if (session is { Revoked: false })
        {
            if (!session.DownloadLimiter.TryConsume(length))
            {
                session.AddDropped();
                Interlocked.Increment(ref Stats.RateLimitedValue);
                return;
            }
            session.AddDownload(length);
        }

        packet.SetSource(server);
        packet.SetSourcePort(serverPort);
        handle.Send(buffer, length, ref addr, recalcChecksums: true);
    }

    // ---------- Injection ----------

    private void SendDnsRedirect(byte[] query, int length, byte[] reply)
    {
        if (Inject(reply, DnsRedirect.BuildReply(query.AsSpan(0, length), _options.GatewayIp, reply)))
            Interlocked.Increment(ref Stats.DnsRedirectedValue);
    }

    private void SendReset(byte[] packet, int length, byte[] reply) =>
        Inject(reply, TcpReset.BuildToClient(packet.AsSpan(0, length), reply));

    /// <summary>Sends a packet addressed to a hotspot client (built by this filter) out of the stack.</summary>
    private bool Inject(byte[] packet, int length)
    {
        if (length == 0 || _inject is null)
            return false;
        var addr = new WinDivertAddress
        {
            Layer = WinDivertLayer.Network,
            Outbound = true,
            IfIdx = _hotspotIfIndex,
            SubIfIdx = 0,
        };
        return _inject.Send(packet, length, ref addr, recalcChecksums: true);
    }

    private void PruneIfDue()
    {
        var now = Environment.TickCount64;
        var last = Interlocked.Read(ref _lastPrune);
        if (now - last < PruneIntervalMs || Interlocked.CompareExchange(ref _lastPrune, now, last) != last)
            return;

        var cutoff = now - FlowIdleMs;
        foreach (var (key, flow) in _flows)
        {
            if (flow.LastSeen < cutoff)
                _flows.TryRemove(key, out _);
        }
        foreach (var (key, time) in _blockLogged)
        {
            if (now - time > 120_000)
                _blockLogged.TryRemove(key, out _);
        }
        _redirects?.Prune();
        _names.Prune();
    }

    private readonly record struct FlowKey(uint Client, ushort ClientPort, uint Server, ushort ServerPort);

    private enum FlowVerdict
    {
        Pending,
        Allowed,
        Blocked,
    }

    private sealed class FlowState
    {
        private byte[]? _buffer;
        private int _length;

        public volatile FlowVerdict Verdict;
        public long LastSeen;
        public string? Host;
        public bool Reported;

        /// <summary>Appends the first bytes of the stream (up to <paramref name="limit"/>) and returns everything seen so far.</summary>
        public ReadOnlySpan<byte> Append(ReadOnlySpan<byte> data, int limit)
        {
            _buffer ??= new byte[limit];
            var take = Math.Min(data.Length, limit - _length);
            data[..take].CopyTo(_buffer.AsSpan(_length));
            _length += take;
            return _buffer.AsSpan(0, _length);
        }

        public void Release()
        {
            _buffer = null;
            _length = 0;
        }
    }
}
