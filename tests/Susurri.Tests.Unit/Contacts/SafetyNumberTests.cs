using System.Security.Cryptography;
using Shouldly;
using Susurri.Modules.DHT.Core.Contacts;
using Xunit;

namespace Susurri.Tests.Unit.Contacts;

public class SafetyNumberTests
{
    [Fact]
    public void Both_Sides_See_The_Same_Number()
    {
        var aliceEnc = RandomNumberGenerator.GetBytes(32);
        var aliceSig = RandomNumberGenerator.GetBytes(32);
        var bobEnc = RandomNumberGenerator.GetBytes(32);
        var bobSig = RandomNumberGenerator.GetBytes(32);

        var fromAlice = SafetyNumber.Compute(aliceEnc, aliceSig, bobEnc, bobSig);
        var fromBob = SafetyNumber.Compute(bobEnc, bobSig, aliceEnc, aliceSig);

        fromAlice.ShouldBe(fromBob);
    }

    [Fact]
    public void Number_Has_Twelve_FiveDigit_Groups()
    {
        var number = SafetyNumber.Compute(
            RandomNumberGenerator.GetBytes(32), RandomNumberGenerator.GetBytes(32),
            RandomNumberGenerator.GetBytes(32), RandomNumberGenerator.GetBytes(32));

        var groups = number.Split(' ');
        groups.Length.ShouldBe(12);
        groups.ShouldAllBe(g => g.Length == 5 && g.All(char.IsDigit));
    }

    [Fact]
    public void Different_Keys_Produce_Different_Numbers()
    {
        var aliceEnc = RandomNumberGenerator.GetBytes(32);
        var aliceSig = RandomNumberGenerator.GetBytes(32);

        var withBob = SafetyNumber.Compute(aliceEnc, aliceSig,
            RandomNumberGenerator.GetBytes(32), RandomNumberGenerator.GetBytes(32));
        var withMallory = SafetyNumber.Compute(aliceEnc, aliceSig,
            RandomNumberGenerator.GetBytes(32), RandomNumberGenerator.GetBytes(32));

        withBob.ShouldNotBe(withMallory);
    }
}
