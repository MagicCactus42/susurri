using System.Security.Cryptography;

namespace Susurri.Shared.Abstractions.Logging;

/// <summary>
/// Helpers for keeping sensitive byte material (public keys, signatures,
/// ciphertexts) out of structured-log sinks.
///
/// Replaces the ad-hoc "first 16 hex chars of <code>Convert.ToHexString(key)</code>"
/// idiom that was scattered across the codebase. Truncation is fine for
/// uniformly-distributed keys but does not generalize to other byte arrays;
/// SHAKE256 produces a uniformly-distributed fingerprint regardless of the
/// input distribution and is collision-resistant in 8 bytes for the small
/// populations of keys a single node sees.
/// </summary>
public static class LogRedaction
{
    private const int FingerprintBytes = 8;

    /// <summary>
    /// Returns an uppercase 16-character hex fingerprint of the supplied
    /// bytes (8 bytes from SHAKE256). Returns the empty string for an empty
    /// input. Output length is always 16 chars for non-empty inputs.
    /// </summary>
    public static string KeyFingerprint(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty)
            return string.Empty;

        Span<byte> digest = stackalloc byte[FingerprintBytes];
        Shake256.HashData(bytes, digest);
        return Convert.ToHexString(digest);
    }

    public static string KeyFingerprint(byte[]? bytes)
        => bytes is null ? string.Empty : KeyFingerprint((ReadOnlySpan<byte>)bytes);
}
