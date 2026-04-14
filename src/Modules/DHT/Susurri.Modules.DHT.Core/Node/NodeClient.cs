using System.Net.Sockets;
using System.Text;
using Susurri.Modules.DHT.Core.Abstractions;

namespace Susurri.Modules.DHT.Core.Node;

public class NodeClient : INodeClient
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(5);

    public async Task<string> SendMessage(string ip, int port, string message)
    {
        using var client = new TcpClient();

        using var connectCts = new CancellationTokenSource(ConnectTimeout);
        await client.ConnectAsync(ip, port, connectCts.Token).ConfigureAwait(false);

        client.ReceiveTimeout = (int)ReadTimeout.TotalMilliseconds;
        client.SendTimeout = (int)ReadTimeout.TotalMilliseconds;

        using var stream = client.GetStream();
        using var writer = new StreamWriter(stream, Encoding.UTF8) { AutoFlush = true };
        using var reader = new StreamReader(stream, Encoding.UTF8);

        await writer.WriteLineAsync(message).ConfigureAwait(false);

        using var readCts = new CancellationTokenSource(ReadTimeout);
        return await reader.ReadLineAsync(readCts.Token).ConfigureAwait(false) ?? string.Empty;
    }

    public async Task<bool> PingAsync(string ip, int port)
    {
        try
        {
            var response = await SendMessage(ip, port, "PING").ConfigureAwait(false);
            return response.StartsWith("PONG");
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}