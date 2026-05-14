using System.Diagnostics.Metrics;
using Shouldly;
using Susurri.Modules.IAM.Core.Crypto;
using Susurri.Shared.Abstractions.Diagnostics;

namespace Susurri.Tests.Unit.Diagnostics;

// End-to-end checks that production code actually emits via SusurriMetrics
// rather than just that the instruments themselves work. Pairs with
// SusurriMetricsTests (which proves the instruments record correctly).
public class MetricCallSiteTests
{
    [Fact]
    public void CryptoKeyGenerator_RecordsPbkdf2DeriveDuration()
    {
        var recorded = new List<double>();
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
        listener.SetMeasurementEventCallback<double>((_, value, _, _) => recorded.Add(value));
        listener.Start();

        var salt = new byte[CryptoKeyGenerator.SaltSize];
        for (int i = 0; i < salt.Length; i++) salt[i] = (byte)i;

        using var keyPair = new CryptoKeyGenerator().GenerateKeyPair("metric-test", salt);

        // Parallel test classes may also touch the meter (e.g. the
        // AllExpectedInstruments_AreDiscoverable smoke records 0), so we
        // filter to non-zero measurements — production PBKDF2 with 600k
        // iterations always takes >0 ms. The wiring is proven by ≥1 such
        // observation arriving within our scope.
        var realMeasurements = recorded.Where(v => v > 0).ToList();
        realMeasurements.Count.ShouldBeGreaterThanOrEqualTo(1);
    }
}
