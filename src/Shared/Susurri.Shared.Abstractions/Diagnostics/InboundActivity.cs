using System.Diagnostics;
using System.Net;

namespace Susurri.Shared.Abstractions.Diagnostics;

/// <summary>
/// Starts a System.Diagnostics.Activity at an inbound entry point so that
/// every log line emitted while handling the request is correlated to a
/// single TraceId/SpanId pair.
///
/// We use raw Activity (not ActivitySource) so the Activity is always
/// created, regardless of whether any listener is attached — Serilog reads
/// <see cref="Activity.Current"/> via AsyncLocal and stamps every log event
/// inside the using-scope. When OpenTelemetry is wired up in Phase 4.2 it
/// can attach an <see cref="ActivityListener"/> to capture the same spans.
/// </summary>
public static class InboundActivity
{
    public static Activity Begin(string operation, IPEndPoint? remoteEndpoint = null)
    {
        var activity = new Activity(operation).Start();
        if (remoteEndpoint is not null)
        {
            activity.AddTag("net.peer.ip", remoteEndpoint.Address.ToString());
            activity.AddTag("net.peer.port", remoteEndpoint.Port.ToString());
        }
        return activity;
    }
}
