using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;

namespace Susurri.Modules.DHT.Core.Node;

public class NodeServer
{
    private readonly int _port;
    private readonly ILogger<NodeServer> _logger;

    public NodeServer(int port, ILogger<NodeServer> logger)
    {
        _port = port;
        _logger = logger;
    }

    public async Task StartAsync()
    {
        var listener = new TcpListener(IPAddress.Any, _port);
        listener.Start();

        while (true)
        {
            var client = await listener.AcceptTcpClientAsync();
            _ = HandleClientAsync(client);
        }
    }

    private async Task HandleClientAsync(TcpClient client)
    {
        using var stream = client.GetStream();
        using var reader = new StreamReader(stream);
        using var writer = new StreamWriter(stream) { AutoFlush = true };
        
        var message = await reader.ReadLineAsync();
        _logger.LogInformation($"Recieved: {message}");
        
        await  writer.WriteLineAsync("ACK");
    }
}