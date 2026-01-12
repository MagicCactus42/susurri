namespace Susurri.Modules.DHT.Core.Abstractions;

public interface INodeClient
{
    Task<string> SendMessage(string ip, int port, string message);
}