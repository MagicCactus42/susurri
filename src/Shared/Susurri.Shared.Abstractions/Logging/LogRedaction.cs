using System.Security.Cryptography;

namespace Susurri.Shared.Abstractions.Logging;

public static class LogRedaction
{
    private const int FingerprintBytes = 8;

    public static string KeyFingerprint(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty)
            return string.Empty;

        Span<byte> sha = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(bytes, sha);
        return Convert.ToHexString(sha[..FingerprintBytes]);
    }

    public static string KeyFingerprint(byte[]? bytes)
        => bytes is null ? string.Empty : KeyFingerprint((ReadOnlySpan<byte>)bytes);
}
