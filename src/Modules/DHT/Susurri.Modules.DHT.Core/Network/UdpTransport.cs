using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;

namespace Susurri.Modules.DHT.Core.Network;

public sealed class UdpTransport : IAsyncDisposable
{
    private readonly ILogger<UdpTransport> _logger;
    private UdpClient? _client;
    private CancellationTokenSource? _cts;
    private Task? _receiveTask;

    private readonly ConcurrentDictionary<Guid, TaskCompletionSource<byte[]>> _pendingRequests = new();

    public event Func<IPEndPoint, byte[], Task>? OnDatagramReceived;
    public IPEndPoint? LocalEndPoint { get; private set; }
    public bool IsRunning => _client != null && _cts != null && !_cts.IsCancellationRequested;

    public UdpTransport(ILogger<UdpTransport> logger)
    {
        _logger = logger;
    }

    public Task StartAsync(int port)
    {
        _cts = new CancellationTokenSource();
        _client = new UdpClient(port);
        LocalEndPoint = (IPEndPoint)_client.Client.LocalEndPoint!;

        _logger.LogInformation("UDP transport started on port {Port}", port);

        _receiveTask = ReceiveLoopAsync(_cts.Token);

        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        _cts?.Cancel();
        _client?.Close();

        if (_receiveTask != null)
        {
            try
            {
                await _receiveTask;
            }
            catch (OperationCanceledException) { }
        }

        _logger.LogInformation("UDP transport stopped");
    }

    public async Task SendAsync(IPEndPoint endpoint, byte[] data)
    {
        if (_client == null)
            throw new InvalidOperationException("Transport not started");

        await _client.SendAsync(data, data.Length, endpoint);
    }

    public async Task<byte[]?> SendRequestAsync(IPEndPoint endpoint, byte[] data, Guid requestId, TimeSpan timeout)
    {
        var tcs = new TaskCompletionSource<byte[]>();
        _pendingRequests[requestId] = tcs;

        try
        {
            await SendAsync(endpoint, data);

            using var cts = new CancellationTokenSource(timeout);
            cts.Token.Register(() => tcs.TrySetCanceled());

            return await tcs.Task;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        finally
        {
            _pendingRequests.TryRemove(requestId, out _);
        }
    }

    public void CompleteRequest(Guid requestId, byte[] response)
    {
        if (_pendingRequests.TryRemove(requestId, out var tcs))
        {
            tcs.TrySetResult(response);
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var result = await _client!.ReceiveAsync(ct);
                _ = HandleDatagramAsync(result.RemoteEndPoint, result.Buffer);
            }
            catch (OperationCanceledException) { break; }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.OperationAborted)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error receiving UDP datagram");
            }
        }
    }

    private async Task HandleDatagramAsync(IPEndPoint sender, byte[] data)
    {
        try
        {
            if (OnDatagramReceived != null)
            {
                await OnDatagramReceived(sender, data);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling UDP datagram from {Sender}", sender);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
    }
}
