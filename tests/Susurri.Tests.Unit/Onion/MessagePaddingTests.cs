using Susurri.Modules.DHT.Core.Onion;

namespace Susurri.Tests.Unit.Onion;

public class MessagePaddingTests
{
    [Fact]
    public void Pad_ShouldCreateFixedSizeOutput()
    {
        var data = "Hello, World!"u8.ToArray();

        var padded = MessagePadding.Pad(data);

        Assert.Equal(MessagePadding.DefaultBlockSize, padded.Length);
    }

    [Fact]
    public void Unpad_ShouldRecoverOriginalData()
    {
        var original = "Hello, World!"u8.ToArray();

        var padded = MessagePadding.Pad(original);
        var recovered = MessagePadding.Unpad(padded);

        Assert.Equal(original, recovered);
    }

    [Fact]
    public void Pad_ShouldWorkWithEmptyData()
    {
        var data = Array.Empty<byte>();

        var padded = MessagePadding.Pad(data);
        var recovered = MessagePadding.Unpad(padded);

        Assert.Equal(MessagePadding.DefaultBlockSize, padded.Length);
        Assert.Empty(recovered);
    }

    [Fact]
    public void Pad_ShouldThrowForDataTooLarge()
    {
        var maxPayload = MessagePadding.GetMaxPayloadSize();
        var tooLargeData = new byte[maxPayload + 1];

        Assert.Throws<ArgumentException>(() => MessagePadding.Pad(tooLargeData));
    }

    [Fact]
    public void Pad_ShouldWorkWithMaxSizeData()
    {
        var maxPayload = MessagePadding.GetMaxPayloadSize();
        var maxSizeData = new byte[maxPayload];
        new Random(42).NextBytes(maxSizeData);

        var padded = MessagePadding.Pad(maxSizeData);
        var recovered = MessagePadding.Unpad(padded);

        Assert.Equal(MessagePadding.DefaultBlockSize, padded.Length);
        Assert.Equal(maxSizeData, recovered);
    }

    [Fact]
    public void Pad_ShouldUseRandomPadding()
    {
        var data = "Test"u8.ToArray();

        var padded1 = MessagePadding.Pad(data);
        var padded2 = MessagePadding.Pad(data);

        // First 4 bytes (length) + data should be same
        // But padding bytes should be different (with very high probability)
        Assert.Equal(padded1[..8], padded2[..8]);
        Assert.NotEqual(padded1, padded2); // Random padding makes them different
    }

    [Fact]
    public void Pad_ShouldWorkWithCustomBlockSize()
    {
        var data = "Test"u8.ToArray();
        const int customBlockSize = 1024;

        var padded = MessagePadding.Pad(data, customBlockSize);
        var recovered = MessagePadding.Unpad(padded);

        Assert.Equal(customBlockSize, padded.Length);
        Assert.Equal(data, recovered);
    }

    [Fact]
    public void Unpad_ShouldThrowForTruncatedData()
    {
        var data = "Test"u8.ToArray();
        var padded = MessagePadding.Pad(data);

        // Truncate to less than length prefix
        var truncated = padded[..2];

        Assert.Throws<ArgumentException>(() => MessagePadding.Unpad(truncated));
    }

    [Fact]
    public void Unpad_ShouldThrowForCorruptedLengthPrefix()
    {
        var data = "Test"u8.ToArray();
        var padded = MessagePadding.Pad(data);

        // Corrupt length prefix to claim more data than available
        padded[0] = 0xFF;
        padded[1] = 0xFF;
        padded[2] = 0xFF;
        padded[3] = 0xFF;

        Assert.Throws<ArgumentException>(() => MessagePadding.Unpad(padded));
    }

    [Fact]
    public void GetMaxPayloadSize_ShouldReturnCorrectValue()
    {
        var maxSize = MessagePadding.GetMaxPayloadSize();

        // Should be block size minus 4 bytes for length prefix
        Assert.Equal(MessagePadding.DefaultBlockSize - 4, maxSize);
    }

    [Fact]
    public void Pad_ShouldThrowForNullData()
    {
        Assert.Throws<ArgumentNullException>(() => MessagePadding.Pad(null!));
    }

    [Fact]
    public void Unpad_ShouldThrowForNullData()
    {
        Assert.Throws<ArgumentNullException>(() => MessagePadding.Unpad(null!));
    }
}
