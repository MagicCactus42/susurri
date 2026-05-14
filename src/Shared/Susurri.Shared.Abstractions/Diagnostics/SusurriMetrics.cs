using System.Diagnostics.Metrics;

namespace Susurri.Shared.Abstractions.Diagnostics;

/// <summary>
/// Phase 4.2 metric instruments — all OpenTelemetry-compatible via
/// <see cref="System.Diagnostics.Metrics.Meter"/>. Add new instruments here
/// rather than scattering meters across modules: one well-known meter name
/// keeps the export configuration uniform and lets every emitting site share
/// the same naming convention.
///
/// The Meter is always live; if no exporter is attached (default), increments
/// are recorded to the in-process meter listeners and discarded. Tests use a
/// <see cref="MeterListener"/> to observe values without a full SDK setup.
/// </summary>
public static class SusurriMetrics
{
    /// <summary>
    /// Public meter name. Stable — exporters call <c>AddMeter("Susurri")</c>
    /// to subscribe. Changing this would orphan any external dashboards.
    /// </summary>
    public const string MeterName = "Susurri";

    private static readonly Meter Meter = new(MeterName, "1.0.0");

    /// <summary>Inbound DHT messages after a successful deserialize, tagged by message type.</summary>
    public static readonly Counter<long> DhtMessagesIn =
        Meter.CreateCounter<long>("dht.messages.in", "messages",
            "Inbound DHT protocol messages after a successful deserialize. Tag: type.");

    /// <summary>Onion-routed messages relayed to the next hop.</summary>
    public static readonly Counter<long> OnionRelayed =
        Meter.CreateCounter<long>("onion.messages.relayed", "messages",
            "Onion-routed messages forwarded to the next hop.");

    /// <summary>Onion-routed messages delivered to a local recipient (final hop).</summary>
    public static readonly Counter<long> OnionDelivered =
        Meter.CreateCounter<long>("onion.messages.delivered", "messages",
            "Onion-routed messages delivered to a local recipient at the final hop.");

    /// <summary>Replay-cache rejections — message-id already seen.</summary>
    public static readonly Counter<long> ReplaysDropped =
        Meter.CreateCounter<long>("replays.dropped", "messages",
            "Messages dropped because their MessageId was already in the replay cache. Tag: scope.");

    /// <summary>Authentication failures: invalid signature, stale timestamp, missing key, etc.</summary>
    public static readonly Counter<long> AuthFailures =
        Meter.CreateCounter<long>("auth.failures", "events",
            "Authentication failures. Tag: kind ∈ {signature, timestamp, missing-key, identity-mismatch}.");

    /// <summary>Onion-layer decrypt failures — wrong key, tampered ciphertext, malformed AEAD.</summary>
    public static readonly Counter<long> OnionDecryptFailures =
        Meter.CreateCounter<long>("onion.decrypt.failures", "events",
            "Onion-layer decryption failures.");

    /// <summary>PBKDF2-SHA256 derivation duration in milliseconds.</summary>
    public static readonly Histogram<double> Pbkdf2DeriveMs =
        Meter.CreateHistogram<double>("crypto.pbkdf2.derive_ms", "ms",
            "PBKDF2-SHA256 key derivation duration.");
}
