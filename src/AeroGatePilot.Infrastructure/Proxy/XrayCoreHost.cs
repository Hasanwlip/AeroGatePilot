using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using AeroGatePilot.Core.Models;
using AeroGatePilot.Core.Proxy;
using AeroGatePilot.Infrastructure.Network;

namespace AeroGatePilot.Infrastructure.Proxy;

/// <summary>
/// Runs <c>core\xray.exe</c> next to the app with a generated config for the selected share-link server.
/// Exposes a local SOCKS5 port that the transparent relay uses.
/// </summary>
public sealed class XrayCoreHost : IAsyncDisposable
{
    public const int DefaultSocksPort = 18680;
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    private readonly string _coreDirectory;
    private readonly string _dataDirectory;
    private readonly AppLog _log;
    private Process? _process;
    private ConcurrentQueue<string>? _processLog;
    private string? _configPath;
    private int _socksPort;

    public XrayCoreHost(string coreDirectory, string dataDirectory, AppLog log)
    {
        _coreDirectory = coreDirectory;
        _dataDirectory = dataDirectory;
        _log = log;
    }

    public bool IsRunning => _process is { HasExited: false };
    public int SocksPort => _socksPort;
    public string? ActiveServerName { get; private set; }

    public string ExecutablePath => Path.Combine(_coreDirectory, "xray.exe");
    public bool IsInstalled => File.Exists(ExecutablePath);

    public static string DefaultCoreDirectory => Path.Combine(AppContext.BaseDirectory, "core");

    public async Task StartAsync(VpnServerProfile server, int socksPort = DefaultSocksPort, CancellationToken cancellationToken = default)
    {
        if (!IsInstalled)
            throw new InvalidOperationException(
                "Xray core was not found next to AeroGate (folder \"core\\xray.exe\"). Reinstall AeroGate Pilot or copy Xray-windows-64 into the core folder.");

        await StopAsync();

        if (!NetworkDiagnostics.IsPortFree(socksPort))
            socksPort = FindFreePort();

        Directory.CreateDirectory(_dataDirectory);
        _configPath = Path.Combine(_dataDirectory, "xray-client.json");
        await File.WriteAllTextAsync(_configPath, XrayConfigBuilder.Build(server, socksPort), Utf8NoBom, cancellationToken);

        var log = new ConcurrentQueue<string>();
        _processLog = log;
        _process = StartProcess(_configPath, log);
        _socksPort = socksPort;
        ActiveServerName = server.Name;

        try
        {
            await WaitUntilListeningAsync(_process, log, socksPort, TimeSpan.FromSeconds(10), cancellationToken);
        }
        catch
        {
            await StopAsync();
            throw;
        }

        _log.Success($"Xray core connected via {server.ProtocolLabel} “{server.Name}” ({server.Endpoint}) — local SOCKS {_socksPort}.");
    }

    public async Task StopAsync()
    {
        var process = _process;
        _process = null;
        _processLog = null;
        ActiveServerName = null;
        if (process is null)
            return;
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
        }
        catch (Exception ex)
        {
            _log.Warn($"Stopping Xray core: {ex.Message}");
        }
        finally
        {
            process.Dispose();
        }
    }

    /// <summary>TCP ping to the server, then a short HTTP fetch through a temporary Xray instance.</summary>
    public async Task<(int TcpMs, int? ProxyMs)> TestAsync(VpnServerProfile server, CancellationToken cancellationToken = default)
    {
        int tcpMs;
        try
        {
            tcpMs = await ShareLinkParser.TcpPingAsync(server, cancellationToken);
        }
        catch
        {
            return (-2, null);
        }

        if (!IsInstalled)
            return (tcpMs, null);

        var port = FindFreePort();
        var tempDir = Path.Combine(_dataDirectory, "ping-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempDir);
        var configPath = Path.Combine(tempDir, "config.json");
        await File.WriteAllTextAsync(configPath, XrayConfigBuilder.Build(server, port), Utf8NoBom, cancellationToken);

        Process? proc = null;
        var log = new ConcurrentQueue<string>();
        try
        {
            proc = StartProcess(configPath, log);
            await WaitUntilListeningAsync(proc, log, port, TimeSpan.FromSeconds(10), cancellationToken);

            var settings = new UpstreamProxySettings
            {
                Type = UpstreamProxyType.Socks5,
                Host = "127.0.0.1",
                Port = port,
            };
            var elapsed = await UpstreamConnector.TestAsync(UpstreamConnector.Create(settings), cancellationToken);
            return (tcpMs, (int)elapsed.TotalMilliseconds);
        }
        finally
        {
            try
            {
                if (proc is { HasExited: false })
                    proc.Kill(entireProcessTree: true);
                proc?.Dispose();
            }
            catch
            {
            }
            try
            {
                Directory.Delete(tempDir, recursive: true);
            }
            catch
            {
            }
        }
    }

    public async ValueTask DisposeAsync() => await StopAsync();

    private Process StartProcess(string configPath, ConcurrentQueue<string> log)
    {
        var psi = new ProcessStartInfo
        {
            FileName = ExecutablePath,
            Arguments = $"run -c \"{configPath}\"",
            WorkingDirectory = _coreDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        psi.Environment["XRAY_LOCATION_ASSET"] = _coreDirectory;

        var process = Process.Start(psi) ?? throw new InvalidOperationException("Could not start Xray core.");
        process.OutputDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
                log.Enqueue(e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
                log.Enqueue(e.Data);
        };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return process;
    }

    private static async Task WaitUntilListeningAsync(
        Process process, ConcurrentQueue<string> log, int port, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (process.HasExited)
                throw new InvalidOperationException(FormatFailure("Xray core exited before opening its SOCKS port.", log, process.ExitCode));

            try
            {
                using var client = new TcpClient();
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(400);
                await client.ConnectAsync(IPAddress.Loopback, port, cts.Token);
                return;
            }
            catch
            {
                await Task.Delay(120, cancellationToken);
            }
        }

        if (process.HasExited)
            throw new InvalidOperationException(FormatFailure("Xray core exited before opening its SOCKS port.", log, process.ExitCode));
        throw new TimeoutException(FormatFailure("Xray core did not open its local SOCKS port in time.", log, null));
    }

    private static string FormatFailure(string headline, ConcurrentQueue<string> log, int? exitCode)
    {
        var lines = log.ToArray();
        var detail = lines.Length == 0
            ? "No output from Xray."
            : string.Join(" ", lines.TakeLast(6));
        if (detail.Length > 400)
            detail = detail[^400..];
        var code = exitCode is null ? "" : $" (exit {exitCode})";
        return $"{headline}{code} {detail}";
    }

    private static int FindFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
