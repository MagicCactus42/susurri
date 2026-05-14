using System.Diagnostics.Metrics;
using Shouldly;
using Susurri.Shared.Abstractions.Diagnostics;

namespace Susurri.Tests.Unit.Diagnostics;

public class SusurriMetricsTests
{
    [Fact]
    public void MeterName_IsStable()
    {
        // Snapshot. Exporters subscribe by meter name; changing this breaks
        // every external dashboard pinned to "Susurri".
        SusurriMetrics.MeterName.ShouldBe("Susurri");
    }

    [Fact]
    public void Counter_RecordsIncrement_ViaListener()
    {
        var recorded = ListenForLongInstrument("dht.messages.in", () =>
        {
            SusurriMetrics.DhtMessagesIn.Add(1, new KeyValuePair<string, object?>("type", "PingMessage"));
            SusurriMetrics.DhtMessagesIn.Add(2, new KeyValuePair<string, object?>("type", "PingMessage"));
        });

        recorded.Sum.ShouldBe(3);
        recorded.LastTags["type"].ShouldBe("PingMessage");
    }

    [Fact]
    public void AuthFailures_PropagatesKindTag()
    {
        var recorded = ListenForLongInstrument("auth.failures", () =>
        {
            SusurriMetrics.AuthFailures.Add(1, new KeyValuePair<string, object?>("kind", "signature"));
            SusurriMetrics.AuthFailures.Add(1, new KeyValuePair<string, object?>("kind", "timestamp"));
        });

        recorded.Sum.ShouldBe(2);
        // Tag values from at least one observation should be either kind.
        // (LastTags is the most recent — we just verify the tag key was carried.)
        recorded.LastTags.ShouldContainKey("kind");
    }

    [Fact]
    public void Histogram_RecordsValue_ViaListener()
    {
        var values = new List<double>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == SusurriMetrics.MeterName &&
                    instrument.Name == "crypto.pbkdf2.derive_ms")
                {
                    l.EnableMeasurementEvents(instrument);
                }
            },
        };
        listener.SetMeasurementEventCallback<double>((_, value, _, _) => values.Add(value));
        listener.Start();

        SusurriMetrics.Pbkdf2DeriveMs.Record(42.5);
        SusurriMetrics.Pbkdf2DeriveMs.Record(98.7);

        values.Count.ShouldBe(2);
        values.ShouldContain(42.5);
        values.ShouldContain(98.7);
    }

    [Fact]
    public void AllExpectedInstruments_AreDiscoverable()
    {
        // Locks the inventory: regression guard against a future PR that
        // silently removes an instrument exporters depend on.
        var seen = new HashSet<string>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, _) =>
            {
                if (instrument.Meter.Name == SusurriMetrics.MeterName)
                    seen.Add(instrument.Name);
            },
        };
        listener.Start();

        // Touch every instrument so MeterListener observes them.
        SusurriMetrics.DhtMessagesIn.Add(0);
        SusurriMetrics.OnionRelayed.Add(0);
        SusurriMetrics.OnionDelivered.Add(0);
        SusurriMetrics.ReplaysDropped.Add(0);
        SusurriMetrics.AuthFailures.Add(0);
        SusurriMetrics.OnionDecryptFailures.Add(0);
        SusurriMetrics.Pbkdf2DeriveMs.Record(0);

        seen.ShouldContain("dht.messages.in");
        seen.ShouldContain("onion.messages.relayed");
        seen.ShouldContain("onion.messages.delivered");
        seen.ShouldContain("replays.dropped");
        seen.ShouldContain("auth.failures");
        seen.ShouldContain("onion.decrypt.failures");
        seen.ShouldContain("crypto.pbkdf2.derive_ms");
    }

    private sealed record LongRecording(long Sum, Dictionary<string, object?> LastTags);

    private static LongRecording ListenForLongInstrument(string instrumentName, Action act)
    {
        long sum = 0;
        var lastTags = new Dictionary<string, object?>();

        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == SusurriMetrics.MeterName &&
                    instrument.Name == instrumentName)
                {
                    l.EnableMeasurementEvents(instrument);
                }
            },
        };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            Interlocked.Add(ref sum, value);
            lock (lastTags)
            {
                lastTags.Clear();
                foreach (var t in tags)
                    lastTags[t.Key] = t.Value;
            }
        });
        listener.Start();

        act();

        return new LongRecording(sum, lastTags);
    }
}
