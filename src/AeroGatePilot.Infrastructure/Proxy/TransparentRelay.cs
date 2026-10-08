using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using AeroGatePilot.Core.Filtering;
using AeroGatePilot.Core.Net;
using AeroGatePilot.Core.Proxy;

namespace AeroGatePilot.Infrastructure.Proxy;

/// <summary>
/// Original destinations of client connections the packet filter redirected to the relay,
/// keyed by the client's address and port.
/// </summary>
public sealed class RedirectTable
{
    private static readonly long IdleTimeoutMs = (long)TimeSpan.FromMinutes(10).TotalMilliseconds;

    private readonly ConcurrentDictionary<ulong, Entry> _entries = new();

    public int Count => _entries.Count;

    public void Set(uint client, ushort clientPort, uint server, ushort serverPort)
    {
        var key = Key(client, clientPort);
        var now = Environment.TickCount64;
        if (_entries.TryGetValue(key, out var existing) && existing.Server == server && existing.ServerPort == serverPort)
        {
            existing.LastSeen = now;
            return;
        }
        _entries[key] = new Entry(server, serverPort) { LastSeen = now };
    }

    public bool TryGet(uint client, ushort clientPort, out uint server, out ushort serverPort)
    {
        if (_entries.TryGetValue(Key(client, clientPort), out var entry))
        {
            entry.LastSeen = Environment.TickCount64;
            server = entry.Server;
            serverPort = entry.ServerPort;
            return true;
        }
        server = 0;
        serverPort = 0;
        return false;
    }

    public void Remove(uint client, ushort clientPort) => _entries.TryRemove(Key(client, clientPort), out _);

    public void Prune()
    {
        var cutoff = Environment.TickCount64 - IdleTimeoutMs;
        foreach (var (key, entry) in _entries)
        {
            if (entry.LastSeen < cutoff)
                _entries.TryRemove(key, out _);
        }
    }

    private static ulong Key(uint client, ushort port) => (ulong)client << 16 | port;

    private sealed class Entry(uint server, ushort serverPort)
    {
        public uint Server { get; } = server;
        public ushort ServerPort { get; } = serverPort;
        public long LastSeen { get; set; }
    }
}

/// <summary>
/// Accepts the client connections redirected by the packet filter and carries them to their original destination
/// through the upstream connector (the VPN port). The site name is taken from the connection's TLS SNI or HTTP Host
/// when present, so the VPN resolves it itself and local DNS tampering does not matter.
/// </summary>
public sealed class TransparentRelay : IAsyncDisposable
{
    private const int SniffLimit = 8 * 1024;
    private const int PumpBufferSize = 256 * 1024;
    private static readonly TimeSpan FirstDataWait = TimeSpan.FromMilliseconds(400);
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(12);

    private readonly RedirectTable _redirects;
    private readonly HostAddressCache _names;
    private readonly IUpstreamConnector _upstream;
    private readonly bool _fallbackDirect;
    private readonly AppLog _log;
    private readonly IPAddress _gateway;
    private readonly CancellationTokenSource _stop = new();
    private TcpListener? _listener;
    private Task? _acceptLoop;
    private long _active;
    private long _total;
    private long _failed;
    private long _lastErrorLog;

    public TransparentRelay(RedirectTable redirects, HostAddressCache names, IUpstreamConnector upstream, bool fallbackDirect, IPAddress gateway, AppLog log)
    {
        _redirects = redirects;
        _names = names;
        _upstream = upstream;
        _fallbackDirect = fallbackDirect;
        _gateway = gateway;
        _log = log;
    }

    public int Port { get; private set; }
    public long ActiveConnections => Interlocked.Read(ref _active);
    public long TotalConnections => Interlocked.Read(ref _total);
    public long FailedConnections => Interlocked.Read(ref _failed);

    public void Start(int port)
    {
        // Same as the portal: binding to 192.168.137.1 itself fails while Windows still marks it
        // tentative ("address not valid in its context"). Listen on all addresses; only clients
        // present in the redirect table (Wi-Fi users we rewrote) are served.
        _listener = new TcpListener(IPAddress.Any, port);
        _listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ExclusiveAddressUse, true);
        _listener.Start(512);
        Port = port;
        _acceptLoop = Task.Run(AcceptLoopAsync);
        _log.Info($"Traffic of VPN-routed users goes through {_upstream.Description}{(_fallbackDirect ? " (direct when it is down)" : "")}.");
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        try
        {
            _listener?.Stop();
        }
        catch
        {
            // Listener already closed.
        }
        if (_acceptLoop is not null)
        {
            try
            {
                await _acceptLoop;
            }
            catch
            {
                // Listener shut down.
            }
        }
        _stop.Dispose();
    }

    private async Task AcceptLoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            Socket socket;
            try
            {
                socket = await _listener!.AcceptSocketAsync(_stop.Token);
            }
            catch when (_stop.IsCancellationRequested)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (SocketException)
            {
                continue;
            }
            _ = HandleSafeAsync(socket);
        }
    }

    private async Task HandleSafeAsync(Socket socket)
    {
        try
        {
            await HandleAsync(socket);
        }
        catch (Exception ex) when (!_stop.IsCancellationRequested)
        {
            Interlocked.Increment(ref _failed);
            LogFailure($"VPN relay: {ex.Message}");
        }
    }

    private async Task HandleAsync(Socket socket)
    {
        Interlocked.Increment(ref _active);
        Interlocked.Increment(ref _total);
        Stream? upstream = null;
        uint clientIp = 0;
        ushort clientPort = 0;
        try
        {
            socket.NoDelay = true;
            try
            {
                socket.ReceiveBufferSize = PumpBufferSize;
                socket.SendBufferSize = PumpBufferSize;
            }
            catch (SocketException)
            {
            }
            if (socket.RemoteEndPoint is not IPEndPoint remote
                || !_redirects.TryGet(IpUtil.ToKey(remote.Address), (ushort)remote.Port, out var server, out var serverPort))
                return;

            clientIp = IpUtil.ToKey(remote.Address);
            clientPort = (ushort)remote.Port;

            var clientStream = new NetworkStream(socket, ownsSocket: false);
            var first = await ReadFirstBytesAsync(clientStream);
            if (first.ClientClosed && first.Length == 0)
                return;
            var firstLength = first.Length;

            string? host = null;
            if (firstLength > 0 && StreamSniffer.TryGetHost(first.Buffer.AsSpan(0, firstLength), out var sniffed) == SniffStatus.Found)
                host = sniffed;
            host ??= _names.NamesFor(server).LastOrDefault();
            var target = host ?? IpUtil.Format(server);

            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token))
            {
                timeout.CancelAfter(ConnectTimeout);
                try
                {
                    upstream = await _upstream.ConnectAsync(target, serverPort, timeout.Token);
                }
                catch (Exception ex) when (_fallbackDirect && !_stop.IsCancellationRequested)
                {
                    LogFailure($"VPN port failed for {target}:{serverPort} ({ex.Message}) — connecting directly.");
                    upstream = await new DirectConnector().ConnectAsync(IpUtil.Format(server), serverPort, timeout.Token);
                }
            }

            if (firstLength > 0)
                await upstream.WriteAsync(first.Buffer.AsMemory(0, firstLength), _stop.Token);

            using var pipeStop = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
            var connected = upstream;
            var up = first.ClientClosed ? Task.CompletedTask : UpAsync();
            var down = PumpAsync(connected, clientStream, pipeStop.Token);

            async Task UpAsync()
            {
                if (first.Pending is not null)
                {
                    var read = await first.Pending;
                    if (read == 0)
                        return;
                    await connected.WriteAsync(first.Buffer.AsMemory(firstLength, read), pipeStop.Token);
                }
                await PumpAsync(clientStream, connected, pipeStop.Token);
            }

            if (await Task.WhenAny(up, down) == up && up.IsCompletedSuccessfully)
                await Task.WhenAny(down, Task.Delay(TimeSpan.FromSeconds(30), _stop.Token));
            pipeStop.Cancel();
            try
            {
                socket.Shutdown(SocketShutdown.Both);
            }
            catch (SocketException)
            {
            }
            await Task.WhenAll(Observe(up), Observe(down));
        }
        finally
        {
            if (clientPort != 0)
                _redirects.Remove(clientIp, clientPort);
            if (upstream is not null)
            {
                try
                {
                    await upstream.DisposeAsync();
                }
                catch
                {
                    // Already closed by the VPN or the peer.
                }
            }
            try
            {
                socket.Dispose();
            }
            catch
            {
            }
            Interlocked.Decrement(ref _active);
        }
    }

    private static Task Observe(Task task) => task.ContinueWith(static t =>
    {
        _ = t.Exception;
    }, TaskContinuationOptions.ExecuteSynchronously);

    private sealed record FirstBytes(byte[] Buffer, int Length, Task<int>? Pending, bool ClientClosed);

    /// <summary>
    /// Waits briefly for the client's first bytes (TLS ClientHello / HTTP request) to learn the site name.
    /// A read still running when the wait ends (the server speaks first) is returned instead of being cancelled,
    /// so no data is lost; its bytes land right after <see cref="FirstBytes.Length"/>.
    /// </summary>
    private async Task<FirstBytes> ReadFirstBytesAsync(NetworkStream stream)
    {
        var buffer = new byte[SniffLimit * 2];
        var length = 0;
        var deadline = Task.Delay(FirstDataWait, _stop.Token);
        while (length < SniffLimit)
        {
            var pending = stream.ReadAsync(buffer.AsMemory(length, buffer.Length - length)).AsTask();
            if (await Task.WhenAny(pending, deadline) != pending)
                return new FirstBytes(buffer, length, pending, false);
            var read = await pending;
            if (read == 0)
                return new FirstBytes(buffer, length, null, true);
            length += read;
            if (StreamSniffer.TryGetHost(buffer.AsSpan(0, length), out _) != SniffStatus.NeedMore)
                break;
        }
        return new FirstBytes(buffer, length, null, false);
    }

    private static async Task PumpAsync(Stream from, Stream to, CancellationToken cancellationToken)
    {
        var buffer = new byte[PumpBufferSize];
        while (true)
        {
            var read = await from.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                try
                {
                    if (to is NetworkStream ns)
                        ns.Socket.Shutdown(SocketShutdown.Send);
                }
                catch
                {
                }
                return;
            }
            await to.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
    }

    private void LogFailure(string message)
    {
        var now = Environment.TickCount64;
        var last = Interlocked.Read(ref _lastErrorLog);
        if (now - last < 30_000 || Interlocked.CompareExchange(ref _lastErrorLog, now, last) != last)
            return;
        _log.Warn(message);
    }
}
