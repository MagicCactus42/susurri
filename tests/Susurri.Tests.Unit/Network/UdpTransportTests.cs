using System.Net;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Susurri.Modules.DHT.Core.Network;
using Xunit;

namespace Susurri.Tests.Unit.Network;

/// <summary>
/// Unit tests for UdpTransport.
/// </summary>
public class UdpTransportTests
{
    private readonly ILogger<UdpTransport> _logger;

    public UdpTransportTests()
    {
        _logger = Substitute.For<ILogger<UdpTransport>>();
    }

    [Fact]
    public async Task StartAsync_SetsLocalEndpoint()
    {
        // Arrange
        await using var transport = new UdpTransport(_logger);
        var port = GetRandomPort();

        // Act
        await transport.StartAsync(port);

        // Assert
        Assert.NotNull(transport.LocalEndPoint);
        Assert.Equal(port, transport.LocalEndPoint.Port);
    }

    [Fact]
    public async Task StartAsync_SetsIsRunningTrue()
    {
        // Arrange
        await using var transport = new UdpTransport(_logger);

        // Act
        await transport.StartAsync(GetRandomPort());

        // Assert
        Assert.True(transport.IsRunning);
    }

    [Fact]
    public async Task StopAsync_SetsIsRunningFalse()
    {
        // Arrange
        await using var transport = new UdpTransport(_logger);
        await transport.StartAsync(GetRandomPort());

        // Act
        await transport.StopAsync();

        // Assert
        Assert.False(transport.IsRunning);
    }

    [Fact]
    public async Task SendAsync_ThrowsWhenNotStarted()
    {
        // Arrange
        await using var transport = new UdpTransport(_logger);
        var endpoint = new IPEndPoint(IPAddress.Loopback, 8080);

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => transport.SendAsync(endpoint, new byte[] { 0x01 }));
    }

    [Fact]
    public async Task SendAsync_TwoTransports_CanCommunicate()
    {
        // Arrange
        await using var transport1 = new UdpTransport(_logger);
        await using var transport2 = new UdpTransport(_logger);

        var port1 = GetRandomPort();
        var port2 = GetRandomPort();

        await transport1.StartAsync(port1);
        await transport2.StartAsync(port2);

        var receivedData = new TaskCompletionSource<byte[]>();
        transport2.OnDatagramReceived += (sender, data) =>
        {
            receivedData.TrySetResult(data);
            return Task.CompletedTask;
        };

        var testData = new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05 };

        // Act
        await transport1.SendAsync(new IPEndPoint(IPAddress.Loopback, port2), testData);

        // Assert
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        cts.Token.Register(() => receivedData.TrySetCanceled());

        var received = await receivedData.Task;
        Assert.Equal(testData, received);
    }

    [Fact]
    public async Task OnDatagramReceived_IncludesSenderEndpoint()
    {
        // Arrange
        await using var transport1 = new UdpTransport(_logger);
        await using var transport2 = new UdpTransport(_logger);

        var port1 = GetRandomPort();
        var port2 = GetRandomPort();

        await transport1.StartAsync(port1);
        await transport2.StartAsync(port2);

        var receivedEndpoint = new TaskCompletionSource<IPEndPoint>();
        transport2.OnDatagramReceived += (sender, data) =>
        {
            receivedEndpoint.TrySetResult(sender);
            return Task.CompletedTask;
        };

        // Act
        await transport1.SendAsync(new IPEndPoint(IPAddress.Loopback, port2), new byte[] { 0x01 });

        // Assert
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        cts.Token.Register(() => receivedEndpoint.TrySetCanceled());

        var sender = await receivedEndpoint.Task;
        Assert.Equal(port1, sender.Port);
    }

    [Fact]
    public async Task CompleteRequest_CompletesTaskCompletionSource()
    {
        // Arrange
        await using var transport1 = new UdpTransport(_logger);
        await using var transport2 = new UdpTransport(_logger);

        var port1 = GetRandomPort();
        var port2 = GetRandomPort();

        await transport1.StartAsync(port1);
        await transport2.StartAsync(port2);

        // Set up transport2 to echo back with a request ID
        transport2.OnDatagramReceived += async (sender, data) =>
        {
            // Echo back
            await transport2.SendAsync(sender, data);
        };

        var requestId = Guid.NewGuid();
        var testData = new byte[] { 0xAA, 0xBB, 0xCC };

        // Transport1 completes the request when it receives a response
        transport1.OnDatagramReceived += (sender, data) =>
        {
            transport1.CompleteRequest(requestId, data);
            return Task.CompletedTask;
        };

        // Act
        var response = await transport1.SendRequestAsync(
            new IPEndPoint(IPAddress.Loopback, port2),
            testData,
            requestId,
            TimeSpan.FromSeconds(5));

        // Assert
        Assert.NotNull(response);
        Assert.Equal(testData, response);
    }

    [Fact]
    public async Task SendRequestAsync_TimesOut_ReturnsNull()
    {
        // Arrange
        await using var transport = new UdpTransport(_logger);
        await transport.StartAsync(GetRandomPort());

        var requestId = Guid.NewGuid();

        // Act - send to a port that won't respond
        var response = await transport.SendRequestAsync(
            new IPEndPoint(IPAddress.Loopback, GetRandomPort()),
            new byte[] { 0x01 },
            requestId,
            TimeSpan.FromMilliseconds(100));

        // Assert
        Assert.Null(response);
    }

    [Fact]
    public async Task DisposeAsync_StopsTransport()
    {
        // Arrange
        var transport = new UdpTransport(_logger);
        await transport.StartAsync(GetRandomPort());
        Assert.True(transport.IsRunning);

        // Act
        await transport.DisposeAsync();

        // Assert
        Assert.False(transport.IsRunning);
    }

    [Fact]
    public async Task MultipleDatagrams_AllReceived()
    {
        // Arrange
        await using var transport1 = new UdpTransport(_logger);
        await using var transport2 = new UdpTransport(_logger);

        var port1 = GetRandomPort();
        var port2 = GetRandomPort();

        await transport1.StartAsync(port1);
        await transport2.StartAsync(port2);

        var receivedCount = 0;
        var allReceived = new TaskCompletionSource();
        const int expectedCount = 10;

        transport2.OnDatagramReceived += (sender, data) =>
        {
            if (Interlocked.Increment(ref receivedCount) >= expectedCount)
            {
                allReceived.TrySetResult();
            }
            return Task.CompletedTask;
        };

        // Act
        for (int i = 0; i < expectedCount; i++)
        {
            await transport1.SendAsync(new IPEndPoint(IPAddress.Loopback, port2), new byte[] { (byte)i });
        }

        // Assert
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        cts.Token.Register(() => allReceived.TrySetCanceled());

        await allReceived.Task;
        Assert.Equal(expectedCount, receivedCount);
    }

    private static int GetRandomPort()
    {
        // Get a random port in the dynamic/private range
        return Random.Shared.Next(49152, 65535);
    }
}
