using System.Collections.Concurrent;
using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Susurri.Modules.DHT.Core.Network;
using Xunit;

namespace Susurri.Tests.Unit.Network;

public class UdpEndpointTests
{
    private static UdpEndpoint Start()
    {
        var ep = new UdpEndpoint(NullLogger.Instance);
        ep.Start(0);
        return ep;
    }

    private static IPEndPoint LoopbackOf(UdpEndpoint ep) =>
        new(IPAddress.Loopback, ep.LocalPort);

    [Fact]
    public async Task SmallMessage_RoundTrips_And_Acks()
    {
        await using var a = Start();
        await using var b = Start();

        var received = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        b.OnMessage += (_, data) => { received.TrySetResult(data); return Task.CompletedTask; };

        var payload = new byte[] { 1, 2, 3, 4, 5 };
        var acked = await a.SendReliableAsync(LoopbackOf(b), payload);

        acked.ShouldBeTrue();
        var got = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        got.ShouldBe(payload);
    }

    [Fact]
    public async Task LargeMessage_Fragments_And_Reassembles()
    {
        await using var a = Start();
        await using var b = Start();

        var received = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        b.OnMessage += (_, data) => { received.TrySetResult(data); return Task.CompletedTask; };

        var payload = new byte[5000];
        new Random(7).NextBytes(payload);

        var acked = await a.SendReliableAsync(LoopbackOf(b), payload);

        acked.ShouldBeTrue();
        var got = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        got.ShouldBe(payload);
    }

    [Fact]
    public async Task MaxSizeMessage_RoundTrips()
    {
        await using var a = Start();
        await using var b = Start();

        var received = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        b.OnMessage += (_, data) => { received.TrySetResult(data); return Task.CompletedTask; };

        var payload = new byte[64 * 1024];
        new Random(11).NextBytes(payload);

        var acked = await a.SendReliableAsync(LoopbackOf(b), payload);

        acked.ShouldBeTrue();
        var got = await received.Task.WaitAsync(TimeSpan.FromSeconds(10));
        got.Length.ShouldBe(payload.Length);
        got.ShouldBe(payload);
    }

    [Fact]
    public async Task EmptyMessage_RoundTrips()
    {
        await using var a = Start();
        await using var b = Start();

        var received = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        b.OnMessage += (_, data) => { received.TrySetResult(data); return Task.CompletedTask; };

        var acked = await a.SendReliableAsync(LoopbackOf(b), Array.Empty<byte>());

        acked.ShouldBeTrue();
        var got = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        got.ShouldBeEmpty();
    }

    [Fact]
    public async Task ConcurrentMessages_AllDelivered_Once()
    {
        await using var a = Start();
        await using var b = Start();

        var seen = new ConcurrentBag<int>();
        var count = 25;
        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        b.OnMessage += (_, data) =>
        {
            seen.Add(BitConverter.ToInt32(data, 0));
            if (seen.Count >= count) done.TrySetResult(true);
            return Task.CompletedTask;
        };

        var dest = LoopbackOf(b);
        await Task.WhenAll(Enumerable.Range(0, count)
            .Select(i => a.SendReliableAsync(dest, BitConverter.GetBytes(i))));

        await done.Task.WaitAsync(TimeSpan.FromSeconds(10));
        seen.OrderBy(x => x).ShouldBe(Enumerable.Range(0, count));
    }

    [Fact]
    public async Task HolePunch_Rendezvous_Succeeds_Both_Sides()
    {
        await using var a = Start();
        await using var b = Start();

        var punchId = Guid.NewGuid();
        var aTask = a.HolePunchAsync(punchId, LoopbackOf(b));
        var bTask = b.HolePunchAsync(punchId, LoopbackOf(a));

        var results = await Task.WhenAll(aTask, bTask).WaitAsync(TimeSpan.FromSeconds(12));
        results[0].ShouldBeTrue();
        results[1].ShouldBeTrue();
    }

    [Fact]
    public async Task OversizePayload_Throws()
    {
        await using var a = Start();
        var tooBig = new byte[64 * 1024 + 1];
        await Should.ThrowAsync<ArgumentException>(
            async () => await a.SendReliableAsync(LoopbackOf(a), tooBig));
    }

    [Fact]
    public async Task MalformedDatagram_Is_Ignored_No_Delivery()
    {
        await using var a = Start();
        await using var b = Start();

        var delivered = false;
        b.OnMessage += (_, _) => { delivered = true; return Task.CompletedTask; };

        // Raw garbage that matches no known frame magic.
        using var raw = new System.Net.Sockets.UdpClient(0);
        var junk = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF, 0x00, 0x01, 0x02 };
        await raw.SendAsync(junk, junk.Length, LoopbackOf(b));

        await Task.Delay(300);
        delivered.ShouldBeFalse();
    }
}
