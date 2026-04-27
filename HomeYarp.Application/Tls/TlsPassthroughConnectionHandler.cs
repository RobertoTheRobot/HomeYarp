using System.Buffers;
using System.Net.Sockets;
using HomeYarp.Application.Abstractions;
using HomeYarp.Domain;
using Microsoft.AspNetCore.Connections;
using Microsoft.Extensions.Logging;

namespace HomeYarp.Application.Tls;

public sealed class TlsPassthroughConnectionHandler : ConnectionHandler
{
    private readonly IApplicationRepository _applications;
    private readonly ILogger<TlsPassthroughConnectionHandler> _logger;

    public TlsPassthroughConnectionHandler(
        IApplicationRepository applications,
        ILogger<TlsPassthroughConnectionHandler> logger)
    {
        _applications = applications;
        _logger = logger;
    }

    public override async Task OnConnectedAsync(ConnectionContext connection)
    {
        var input = connection.Transport.Input;
        var output = connection.Transport.Output;
        var ct = connection.ConnectionClosed;
        var remote = connection.RemoteEndPoint?.ToString() ?? "unknown";

        _logger.LogDebug("Passthrough connection {ConnectionId} accepted from {Remote}", connection.ConnectionId, remote);

        try
        {
            var (sni, recordLength) = await PeekSniAsync(input, ct);
            if (sni is null)
            {
                _logger.LogWarning("Passthrough connection {ConnectionId} from {Remote} aborted: no SNI in ClientHello", connection.ConnectionId, remote);
                return;
            }

            var app = await ResolveAppAsync(sni, ct);
            if (app is null)
            {
                _logger.LogWarning(
                    "Passthrough connection {ConnectionId} from {Remote} aborted: no passthrough app matches SNI '{Sni}'",
                    connection.ConnectionId,
                    remote,
                    sni);
                return;
            }

            var destination = app.Cluster.Destinations.FirstOrDefault();
            if (destination is null)
            {
                _logger.LogWarning(
                    "Passthrough connection {ConnectionId} aborted: app '{AppName}' ({AppId}) has no destinations",
                    connection.ConnectionId,
                    app.Name,
                    app.Id);
                return;
            }

            var (host, port) = ParseTcpTarget(destination.Address);

            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            await socket.ConnectAsync(host, port, ct);
            await using var backendStream = new NetworkStream(socket, ownsSocket: false);

            _logger.LogInformation(
                "Passthrough {ConnectionId}: SNI '{Sni}' from {Remote} -> app '{AppName}' ({AppId}) backend {Host}:{Port}",
                connection.ConnectionId,
                sni,
                remote,
                app.Name,
                app.Id,
                host,
                port);

            // Replay the buffered ClientHello to the backend.
            var readResult = await input.ReadAsync(ct);
            var buffer = readResult.Buffer;
            var bufferedSlice = buffer.Slice(0, Math.Min(buffer.Length, recordLength));
            foreach (var segment in bufferedSlice)
            {
                await backendStream.WriteAsync(segment, ct);
            }
            await backendStream.FlushAsync(ct);
            input.AdvanceTo(bufferedSlice.End);

            var clientToBackend = PumpFromPipeAsync(input, backendStream, ct);
            var backendToClient = PumpFromStreamAsync(backendStream, output, ct);
            await Task.WhenAny(clientToBackend, backendToClient);

            _logger.LogDebug(
                "Passthrough {ConnectionId} for app '{AppName}' ({AppId}) closed normally",
                connection.ConnectionId,
                app.Name,
                app.Id);
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("Passthrough connection {ConnectionId} cancelled", connection.ConnectionId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Passthrough connection {ConnectionId} failed", connection.ConnectionId);
        }
        finally
        {
            connection.Abort();
        }
    }

    private async Task<(string? sni, int recordLength)> PeekSniAsync(System.IO.Pipelines.PipeReader input, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var result = await input.ReadAsync(ct);
            var buffer = result.Buffer;

            if (TlsClientHelloParser.TryParseSni(buffer, out var sni, out var recordLength, out var needMore))
            {
                input.AdvanceTo(buffer.Start);
                return (sni, recordLength);
            }

            if (!needMore)
            {
                input.AdvanceTo(buffer.Start);
                return (null, 0);
            }

            input.AdvanceTo(buffer.Start, buffer.End);
            if (result.IsCompleted)
            {
                return (null, 0);
            }
        }
        return (null, 0);
    }

    private async Task<HomeYarp.Domain.Application?> ResolveAppAsync(string sni, CancellationToken ct)
    {
        var apps = await _applications.GetAllAsync(ct);
        return apps.FirstOrDefault(a =>
            a.Enabled
            && a.Tls.Mode == TlsMode.Passthrough
            && a.Routes.Any(r => r.Hosts.Any(h => HostMatches(h, sni))));
    }

    private static bool HostMatches(string pattern, string sni)
    {
        if (string.Equals(pattern, sni, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (pattern.StartsWith("*.", StringComparison.Ordinal))
        {
            var suffix = pattern[1..];
            return sni.EndsWith(suffix, StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    private static (string Host, int Port) ParseTcpTarget(string address)
    {
        if (Uri.TryCreate(address, UriKind.Absolute, out var uri))
        {
            var port = uri.IsDefaultPort
                ? (string.Equals(uri.Scheme, "https", StringComparison.OrdinalIgnoreCase) ? 443 : 80)
                : uri.Port;
            return (uri.Host, port);
        }

        var colon = address.LastIndexOf(':');
        if (colon > 0 && int.TryParse(address[(colon + 1)..], out var explicitPort))
        {
            return (address[..colon], explicitPort);
        }
        return (address, 443);
    }

    private static async Task PumpFromPipeAsync(System.IO.Pipelines.PipeReader reader, NetworkStream destination, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var result = await reader.ReadAsync(ct);
            var buffer = result.Buffer;
            try
            {
                foreach (var segment in buffer)
                {
                    await destination.WriteAsync(segment, ct);
                }
            }
            finally
            {
                reader.AdvanceTo(buffer.End);
            }
            if (result.IsCompleted)
            {
                return;
            }
        }
    }

    private static async Task PumpFromStreamAsync(NetworkStream source, System.IO.Pipelines.PipeWriter destination, CancellationToken ct)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(8192);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var read = await source.ReadAsync(buffer, ct);
                if (read == 0)
                {
                    return;
                }
                await destination.WriteAsync(buffer.AsMemory(0, read), ct);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
