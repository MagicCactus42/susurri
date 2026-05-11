using System.Diagnostics;
using Serilog.Core;
using Serilog.Events;

namespace Susurri.CLI.Logging;

/// <summary>
/// Stamps every Serilog event with the current Activity's TraceId, SpanId,
/// and operation name (when an Activity is in scope). The Activity is set
/// at inbound entry points via <c>InboundActivity.Begin</c> and flows through
/// AsyncLocal for the lifetime of the request, so all downstream log lines
/// share a single TraceId for correlation.
/// </summary>
internal sealed class ActivityEnricher : ILogEventEnricher
{
    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        var activity = Activity.Current;
        if (activity is null)
            return;

        logEvent.AddPropertyIfAbsent(propertyFactory.CreateProperty("TraceId", activity.TraceId.ToString()));
        logEvent.AddPropertyIfAbsent(propertyFactory.CreateProperty("SpanId", activity.SpanId.ToString()));
        if (!string.IsNullOrEmpty(activity.OperationName))
            logEvent.AddPropertyIfAbsent(propertyFactory.CreateProperty("Operation", activity.OperationName));
    }
}
