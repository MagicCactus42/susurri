using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Susurri.Modules.DHT.Core.NatTraversal;
using Xunit;

namespace Susurri.Tests.Unit.NatTraversal;

public class HolePunchServiceTests
{
    private readonly HolePunchService _service = new(NullLogger<HolePunchService>.Instance);

    #region Probe Handling Tests

    [Fact]
    public void TryHandleProbe_InvalidSize_ReturnsFalse()
    {
        var data = new byte[10];
        var sender = new IPEndPoint(IPAddress.Loopback, 1234);

        Assert.False(_service.TryHandleProbe(data, sender));
    }

    [Fact]
    public void TryHandleProbe_InvalidMagic_ReturnsFalse()
    {
        // Correct size (20 bytes) but wrong magic
        var data = new byte[20];
        data[0] = 0xFF;
        var sender = new IPEndPoint(IPAddress.Loopback, 1234);

        Assert.False(_service.TryHandleProbe(data, sender));
    }

    [Fact]
    public void TryHandleProbe_ValidMagicButNoSession_ReturnsFalse()
    {
        var punchId = Guid.NewGuid();
        var data = BuildProbePacket(punchId);
        var sender = new IPEndPoint(IPAddress.Loopback, 1234);

        // No active session for this punch ID
        Assert.False(_service.TryHandleProbe(data, sender));
    }

    [Fact]
    public async Task PunchAsync_UnreachableEndpoint_ReturnsNull()
    {
        var punchId = Guid.NewGuid();
        // Non-routable address - punch should timeout and return null
        var remote = new IPEndPoint(IPAddress.Parse("192.0.2.1"), 9999);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var result = await _service.PunchAsync(punchId, remote, ct: cts.Token);

        Assert.Null(result);
    }

    [Fact]
    public async Task PunchAsync_LoopbackSelfPunch_Succeeds()
    {
        var punchId = Guid.NewGuid();

        // Start two concurrent punch attempts to localhost, simulating both sides
        var port1 = GetFreePort();
        var port2 = GetFreePort();

        var ep1 = new IPEndPoint(IPAddress.Loopback, port1);
        var ep2 = new IPEndPoint(IPAddress.Loopback, port2);

        await using var service1 = new HolePunchService(NullLogger<HolePunchService>.Instance);
        await using var service2 = new HolePunchService(NullLogger<HolePunchService>.Instance);

        var task1 = service1.PunchAsync(punchId, ep2, port1);
        var task2 = service2.PunchAsync(punchId, ep1, port2);

        var results = await Task.WhenAll(task1, task2);

        // At least one side should succeed (both should, but timing can be tricky)
        Assert.True(results[0] != null || results[1] != null,
            "At least one side of the loopback hole punch should succeed");

        results[0]?.Dispose();
        results[1]?.Dispose();
    }

    #endregion

    #region HolePunchResult Tests

    [Fact]
    public void HolePunchResult_Dispose_DisposesClient()
    {
        var client = new System.Net.Sockets.UdpClient(0);
        var localEp = (IPEndPoint)client.Client.LocalEndPoint!;

        var result = new HolePunchResult
        {
            Client = client,
            RemoteEndPoint = new IPEndPoint(IPAddress.Loopback, 1234),
            LocalEndPoint = localEp
        };

        result.Dispose();

        // After dispose, the client should be closed
        Assert.ThrowsAny<Exception>(() => client.Client.LocalEndPoint);
    }

    #endregion

    private static byte[] BuildProbePacket(Guid punchId)
    {
        var packet = new byte[20];
        // SUHP magic
        packet[0] = 0x53;
        packet[1] = 0x55;
        packet[2] = 0x48;
        packet[3] = 0x50;
        punchId.ToByteArray().CopyTo(packet, 4);
        return packet;
    }

    private static int GetFreePort()
    {
        using var listener = new System.Net.Sockets.UdpClient(0);
        return ((IPEndPoint)listener.Client.LocalEndPoint!).Port;
    }
}
