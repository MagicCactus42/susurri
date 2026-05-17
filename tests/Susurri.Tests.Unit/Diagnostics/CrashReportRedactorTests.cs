using Shouldly;
using Susurri.Shared.Abstractions.Logging;
using Susurri.Shared.Infrastructure.Diagnostics;

namespace Susurri.Tests.Unit.Diagnostics;

public class CrashReportRedactorTests
{
    [Fact]
    public void Redact_NullInput_ReturnsEmpty()
    {
        CrashReportRedactor.Redact(null).ShouldBe(string.Empty);
    }

    [Fact]
    public void Redact_EmptyInput_ReturnsEmpty()
    {
        CrashReportRedactor.Redact(string.Empty).ShouldBe(string.Empty);
    }

    [Fact]
    public void Redact_StringWithoutSecrets_ReturnsUnchanged()
    {
        const string input = "Something bad happened at 12:34:56";
        CrashReportRedactor.Redact(input).ShouldBe(input);
    }

    [Fact]
    public void Redact_64CharHex_ReplacedWithKeyFingerprint()
    {
        var keyBytes = new byte[32];
        for (int i = 0; i < keyBytes.Length; i++) keyBytes[i] = (byte)i;
        var hex = Convert.ToHexString(keyBytes);
        var fingerprint = LogRedaction.KeyFingerprint(keyBytes);

        var redacted = CrashReportRedactor.Redact($"Failed for key {hex} retry?");

        redacted.ShouldBe($"Failed for key <key:{fingerprint}> retry?");
    }

    [Fact]
    public void Redact_MultipleHexKeys_AllReplaced()
    {
        var k1 = new byte[32];
        var k2 = new byte[32];
        for (int i = 0; i < 32; i++) { k1[i] = (byte)i; k2[i] = (byte)(255 - i); }
        var input = $"{Convert.ToHexString(k1)} vs {Convert.ToHexString(k2)}";

        var redacted = CrashReportRedactor.Redact(input);

        redacted.ShouldNotContain(Convert.ToHexString(k1));
        redacted.ShouldNotContain(Convert.ToHexString(k2));
        redacted.ShouldContain($"<key:{LogRedaction.KeyFingerprint(k1)}>");
        redacted.ShouldContain($"<key:{LogRedaction.KeyFingerprint(k2)}>");
    }

    [Fact]
    public void Redact_HomeDirectoryPath_ReplacedWithTilde()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(home))
            return;

        var input = $"File not found: {home}/secrets/key.pem";
        var redacted = CrashReportRedactor.Redact(input);

        redacted.ShouldNotContain(home);
        redacted.ShouldContain("~/secrets/key.pem");
    }

    [Fact]
    public void Redact_ShortHexStringsNotReplaced()
    {
        const string input = "Error code 0xDEADBEEF at offset CAFEBABE";
        CrashReportRedactor.Redact(input).ShouldBe(input);
    }
}
