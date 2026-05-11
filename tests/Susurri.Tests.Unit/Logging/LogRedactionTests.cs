using Shouldly;
using Susurri.Shared.Abstractions.Logging;

namespace Susurri.Tests.Unit.Logging;

public class LogRedactionTests
{
    [Fact]
    public void KeyFingerprint_EmptyInput_ReturnsEmptyString()
    {
        LogRedaction.KeyFingerprint(ReadOnlySpan<byte>.Empty).ShouldBe(string.Empty);
        LogRedaction.KeyFingerprint((byte[]?)null).ShouldBe(string.Empty);
        LogRedaction.KeyFingerprint(Array.Empty<byte>()).ShouldBe(string.Empty);
    }

    [Fact]
    public void KeyFingerprint_AlwaysReturns16HexChars_ForNonEmptyInput()
    {
        LogRedaction.KeyFingerprint(new byte[] { 0x01 }).Length.ShouldBe(16);
        LogRedaction.KeyFingerprint(new byte[32]).Length.ShouldBe(16);
        LogRedaction.KeyFingerprint(new byte[1024]).Length.ShouldBe(16);
    }

    [Fact]
    public void KeyFingerprint_IsDeterministic()
    {
        var input = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF };
        var a = LogRedaction.KeyFingerprint(input);
        var b = LogRedaction.KeyFingerprint(input);
        a.ShouldBe(b);
    }

    [Fact]
    public void KeyFingerprint_DifferentInputs_ProduceDifferentFingerprints()
    {
        var f1 = LogRedaction.KeyFingerprint(new byte[] { 0x01, 0x02, 0x03 });
        var f2 = LogRedaction.KeyFingerprint(new byte[] { 0x01, 0x02, 0x04 });
        f1.ShouldNotBe(f2);
    }

    [Fact]
    public void KeyFingerprint_PinnedSnapshotForKnownInput()
    {
        // Snapshot vector — pinning the SHAKE256 output guards against an
        // accidental switch to a different hash, output length, or byte order.
        // Input: 32 zero bytes (a likely common test fixture) → fixed digest.
        var input = new byte[32]; // all zeros
        LogRedaction.KeyFingerprint(input).ShouldBe("F5977C8283546A63");
    }

    [Fact]
    public void KeyFingerprint_PinnedSnapshotForBytePattern()
    {
        // Second snapshot — input bytes 0x00..0x1F (the same test vector
        // pattern used elsewhere in the suite). Locks output for non-zero
        // distinctness.
        var input = new byte[32];
        for (int i = 0; i < input.Length; i++) input[i] = (byte)i;
        LogRedaction.KeyFingerprint(input).ShouldBe("69F07C8840CE8002");
    }

    [Fact]
    public void KeyFingerprint_OutputIsUppercaseHex()
    {
        var fp = LogRedaction.KeyFingerprint(new byte[] { 0xff });
        fp.ShouldMatch("^[0-9A-F]{16}$");
    }
}
