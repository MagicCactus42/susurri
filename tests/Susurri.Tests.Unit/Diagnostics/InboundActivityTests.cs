using System.Diagnostics;
using System.Net;
using Shouldly;
using Susurri.Shared.Abstractions.Diagnostics;

namespace Susurri.Tests.Unit.Diagnostics;

public class InboundActivityTests
{
    [Fact]
    public void Begin_SetsActivityAsCurrent()
    {
        Activity.Current.ShouldBeNull();

        using (var activity = InboundActivity.Begin("test.op"))
        {
            Activity.Current.ShouldBe(activity);
            activity.OperationName.ShouldBe("test.op");
        }

        Activity.Current.ShouldBeNull();
    }

    [Fact]
    public void Begin_WithRemoteEndpoint_TagsActivityWithPeerInfo()
    {
        var endpoint = new IPEndPoint(IPAddress.Parse("203.0.113.7"), 7070);
        using var activity = InboundActivity.Begin("inbound.test", endpoint);

        activity.GetTagItem("net.peer.ip").ShouldBe("203.0.113.7");
        activity.GetTagItem("net.peer.port").ShouldBe("7070");
    }

    [Fact]
    public void Begin_GeneratesNonZeroTraceAndSpanIds()
    {
        using var activity = InboundActivity.Begin("trace.gen.test");

        activity.TraceId.ToString().ShouldNotBe("00000000000000000000000000000000");
        activity.SpanId.ToString().ShouldNotBe("0000000000000000");
    }

    [Fact]
    public async Task Begin_FlowsThroughAsyncLocal()
    {
        // Activity flows through the async-local context — every continuation
        // inside the using-scope sees the same Activity.Current. This is what
        // makes correlation work across `await` boundaries.
        using var activity = InboundActivity.Begin("asynclocal.test");
        var captured = await CapturedActivityAfterAwait();
        captured.ShouldBe(activity);
    }

    private static async Task<Activity?> CapturedActivityAfterAwait()
    {
        await Task.Yield();
        return Activity.Current;
    }
}
