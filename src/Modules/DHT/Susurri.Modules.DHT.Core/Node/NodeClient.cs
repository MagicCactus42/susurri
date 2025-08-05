using System.Net.Sockets;
using System.Text;
using Susurri.Modules.DHT.Core.Abstractions;

namespace Susurri.Modules.DHT.Core.Node;

public class NodeClient : INodeClient
{
    public async Task<string> SendMessage(string ip, int port, string message)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(ip, port);

        using var stream = client.GetStream();
        using var writer = new StreamWriter(stream, Encoding.UTF8) { AutoFlush = true };
        using var reader = new StreamReader(stream, Encoding.UTF8);

        await writer.WriteLineAsync(message);
        return await reader.ReadLineAsync() ?? string.Empty;
    }

    public async Task<bool> PingAsync(string ip, int port)
    {
        var response = await SendMessage(ip, port, "PING");
        return response.StartsWith("PONG");
    }
}