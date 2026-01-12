using System.Security.Cryptography;

namespace Susurri.Modules.DHT.Core.Onion;

public static class MessagePadding
{
    public const int DefaultBlockSize = 16 * 1024; // 16KB
    private const int LengthPrefixSize = 4;

    public static byte[] Pad(byte[] data, int blockSize = DefaultBlockSize)
    {
        ArgumentNullException.ThrowIfNull(data);

        if (blockSize < LengthPrefixSize + 1)
            throw new ArgumentException("Block size too small", nameof(blockSize));

        var totalSize = LengthPrefixSize + data.Length;

        if (totalSize > blockSize)
            throw new ArgumentException(
                $"Data too large for block size. Max payload: {blockSize - LengthPrefixSize} bytes, got: {data.Length} bytes",
                nameof(data));

        var padded = new byte[blockSize];

        // Write length prefix (big-endian for consistency)
        padded[0] = (byte)(data.Length >> 24);
        padded[1] = (byte)(data.Length >> 16);
        padded[2] = (byte)(data.Length >> 8);
        padded[3] = (byte)data.Length;

        // Copy original data
        Buffer.BlockCopy(data, 0, padded, LengthPrefixSize, data.Length);

        // Fill remaining space with random bytes (not zeros to avoid compression attacks)
        var paddingStart = LengthPrefixSize + data.Length;
        var paddingLength = blockSize - paddingStart;

        if (paddingLength > 0)
        {
            var padding = padded.AsSpan(paddingStart, paddingLength);
            RandomNumberGenerator.Fill(padding);
        }

        return padded;
    }

    public static byte[] Unpad(byte[] paddedData)
    {
        ArgumentNullException.ThrowIfNull(paddedData);

        if (paddedData.Length < LengthPrefixSize)
            throw new ArgumentException("Padded data too short", nameof(paddedData));

        // Read length prefix (big-endian)
        var length = (paddedData[0] << 24) | (paddedData[1] << 16) | (paddedData[2] << 8) | paddedData[3];

        if (length < 0)
            throw new ArgumentException("Invalid length prefix (negative)", nameof(paddedData));

        if (length > paddedData.Length - LengthPrefixSize)
            throw new ArgumentException(
                $"Invalid length prefix: {length} exceeds available data {paddedData.Length - LengthPrefixSize}",
                nameof(paddedData));

        var data = new byte[length];
        Buffer.BlockCopy(paddedData, LengthPrefixSize, data, 0, length);

        return data;
    }

    public static int GetMaxPayloadSize(int blockSize = DefaultBlockSize)
        => blockSize - LengthPrefixSize;
}
