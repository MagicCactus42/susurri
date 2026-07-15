using System.Text;
using NSec.Cryptography;
using Shouldly;
using Susurri.Modules.DHT.Core.Onion.Ratchet;
using Xunit;

namespace Susurri.Tests.Unit.Onion;

public class RatchetSessionManagerTests
{
    private static (RatchetSessionManager Mgr, byte[] Pub) NewPeer()
    {
        var key = Key.Create(KeyAgreementAlgorithm.X25519);
        return (new RatchetSessionManager(key), key.PublicKey.Export(KeyBlobFormat.RawPublicKey));
    }

    private static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s);
    private static string Str(byte[] b) => Encoding.UTF8.GetString(b);

    [Fact]
    public void First_Message_Bootstraps_And_Decrypts()
    {
        var (alice, alicePub) = NewPeer();
        var (bob, bobPub) = NewPeer();

        var env = alice.Seal(bobPub, Utf8("hello bob"));
        Str(bob.Open(alicePub, env)).ShouldBe("hello bob");
    }

    [Fact]
    public void Bidirectional_Conversation_Round_Trips()
    {
        var (alice, alicePub) = NewPeer();
        var (bob, bobPub) = NewPeer();

        for (int i = 0; i < 8; i++)
        {
            var a = alice.Seal(bobPub, Utf8($"alice-{i}"));
            Str(bob.Open(alicePub, a)).ShouldBe($"alice-{i}");

            var b = bob.Seal(alicePub, Utf8($"bob-{i}"));
            Str(alice.Open(bobPub, b)).ShouldBe($"bob-{i}");
        }
    }

    [Fact]
    public void Out_Of_Order_Messages_Decrypt()
    {
        var (alice, alicePub) = NewPeer();
        var (bob, bobPub) = NewPeer();

        var m0 = alice.Seal(bobPub, Utf8("zero"));
        var m1 = alice.Seal(bobPub, Utf8("one"));
        var m2 = alice.Seal(bobPub, Utf8("two"));

        Str(bob.Open(alicePub, m2)).ShouldBe("two");
        Str(bob.Open(alicePub, m0)).ShouldBe("zero");
        Str(bob.Open(alicePub, m1)).ShouldBe("one");
    }

    [Fact]
    public void Every_Envelope_Is_Distinct_For_Same_Plaintext()
    {
        var (alice, _) = NewPeer();
        var (_, bobPub) = NewPeer();
        alice.Seal(bobPub, Utf8("same")).ShouldNotBe(alice.Seal(bobPub, Utf8("same")));
    }
}
