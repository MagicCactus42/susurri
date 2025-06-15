using System.Net.Sockets;
using Susurri.Modules.DHT.Core.Abstractions;

namespace Susurri.Modules.DHT.Core.Node;

public class NodeClient : INodeClient
{
    public async Task<string> SendMessage(string ip, int port, string message)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(ip, port);

        using var stream = client.GetStream();
        var writer = new StreamWriter(stream) { AutoFlush = true };
        var reader = new StreamReader(stream);

        await writer.WriteLineAsync(message);
        return await reader.ReadLineAsync();
    }
}