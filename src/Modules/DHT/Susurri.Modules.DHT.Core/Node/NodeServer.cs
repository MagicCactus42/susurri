using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Susurri.Modules.DHT.Core.Node;

public class NodeServer
{
    private readonly int _port;
    private readonly ILogger<NodeServer> _logger;
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;

    public string NodeId { get; }

    public NodeServer(int port, ILogger<NodeServer> logger)
    {
        _port = port;
        _logger = logger;
        NodeId = GenerateNodeId(IPAddress.Loopback.ToString(), port);
    }

    public async Task StartAsync()
    {
        _cts = new CancellationTokenSource();
        _listener = new TcpListener(IPAddress.Any, _port);
        _listener.Start();

        _logger.LogInformation("DHT Node {NodeId} listening on port {Port}", NodeId, _port);

        try
        {
            while (!_cts.Token.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_cts.Token);
                _ = HandleClientAsync(client);
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Node server stopping...");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error in DHT Node");
        }
        finally
        {
            _listener.Stop();
        }
    }

    public void Stop()
    {
        _cts?.Cancel();
        _listener?.Stop();
        _logger.LogInformation("Node stopped.");
    }

    private async Task HandleClientAsync(TcpClient client)
    {
        try
        {
            client.ReceiveTimeout = 5_000; // 5 second read timeout (anti-slowloris)
            client.SendTimeout = 5_000;

            using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.UTF8);
            using var writer = new StreamWriter(stream, Encoding.UTF8) { AutoFlush = true };

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var message = await reader.ReadLineAsync(cts.Token);
            if (string.IsNullOrWhiteSpace(message))
                return;

            // Limit input length to prevent abuse
            if (message.Length > 1024)
            {
                _logger.LogWarning("Rejected oversized message ({Length} chars)", message.Length);
                return;
            }

            _logger.LogInformation("Received: {Message}", message);

            string response = message switch
            {
                "PING" => $"PONG from {NodeId}",
                var msg when msg.StartsWith("HELLO") => $"HELLO_ACK from {NodeId}",
                _ => "UNKNOWN_COMMAND"
            };

            await writer.WriteLineAsync(response);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Client timed out");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error while handling client");
        }
        finally
        {
            client.Dispose();
        }
    }

    private static string GenerateNodeId(string ip, int port)
    {
        using var sha1 = SHA1.Create();
        var bytes = Encoding.UTF8.GetBytes($"{ip}:{port}");
        return BitConverter.ToString(sha1.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
    }
}
