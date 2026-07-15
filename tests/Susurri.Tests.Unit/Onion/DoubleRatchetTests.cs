using System.Security.Cryptography;
using System.Text;
using NSec.Cryptography;
using Shouldly;
using Susurri.Modules.DHT.Core.Onion.Ratchet;
using Xunit;

namespace Susurri.Tests.Unit.Onion;

public class DoubleRatchetTests
{
    private static (DoubleRatchet Alice, DoubleRatchet Bob) NewSession()
    {
        // Bob's static X25519 key is his initial ratchet key; both sides share an
        // X3DH secret (here just a random 32 bytes standing in for the handshake).
        var bobStatic = Key.Create(KeyAgreementAlgorithm.X25519);
        var bobStaticPub = bobStatic.PublicKey.Export(KeyBlobFormat.RawPublicKey);

        var sharedSecret = new byte[32];
        RandomNumberGenerator.Fill(sharedSecret);

        var alice = DoubleRatchet.CreateInitiator(sharedSecret, bobStaticPub);
        var bob = DoubleRatchet.CreateResponder(sharedSecret, bobStatic);
        return (alice, bob);
    }

    private static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s);
    private static string Str(byte[] b) => Encoding.UTF8.GetString(b);

    [Fact]
    public void First_Message_Decrypts()
    {
        var (alice, bob) = NewSession();
        var (header, ct) = alice.Encrypt(Utf8("hello bob"));
        Str(bob.Decrypt(header, ct)).ShouldBe("hello bob");
    }

    [Fact]
    public void Bidirectional_Conversation_All_Decrypts()
    {
        var (alice, bob) = NewSession();

        for (int i = 0; i < 10; i++)
        {
            var (ah, ac) = alice.Encrypt(Utf8($"alice-{i}"));
            Str(bob.Decrypt(ah, ac)).ShouldBe($"alice-{i}");

            var (bh, bc) = bob.Encrypt(Utf8($"bob-{i}"));
            Str(alice.Decrypt(bh, bc)).ShouldBe($"bob-{i}");
        }
    }

    [Fact]
    public void Multiple_Messages_Same_Direction_Then_Reply()
    {
        var (alice, bob) = NewSession();

        var msgs = Enumerable.Range(0, 5).Select(i => alice.Encrypt(Utf8($"m{i}"))).ToList();
        for (int i = 0; i < msgs.Count; i++)
            Str(bob.Decrypt(msgs[i].Header, msgs[i].Ciphertext)).ShouldBe($"m{i}");

        var (bh, bc) = bob.Encrypt(Utf8("got them"));
        Str(alice.Decrypt(bh, bc)).ShouldBe("got them");
    }

    [Fact]
    public void Out_Of_Order_Delivery_Decrypts_Via_Skipped_Keys()
    {
        var (alice, bob) = NewSession();

        var m0 = alice.Encrypt(Utf8("zero"));
        var m1 = alice.Encrypt(Utf8("one"));
        var m2 = alice.Encrypt(Utf8("two"));

        Str(bob.Decrypt(m2.Header, m2.Ciphertext)).ShouldBe("two");
        Str(bob.Decrypt(m0.Header, m0.Ciphertext)).ShouldBe("zero");
        Str(bob.Decrypt(m1.Header, m1.Ciphertext)).ShouldBe("one");
    }

    [Fact]
    public void Same_Plaintext_Produces_Different_Ciphertext_Each_Time()
    {
        var (alice, _) = NewSession();
        var (_, c1) = alice.Encrypt(Utf8("same"));
        var (_, c2) = alice.Encrypt(Utf8("same"));
        c1.ShouldNotBe(c2);
    }

    [Fact]
    public void Wrong_Session_Cannot_Decrypt()
    {
        var (alice, _) = NewSession();
        var (_, other) = NewSession();

        var (header, ct) = alice.Encrypt(Utf8("secret"));
        Should.Throw<CryptographicException>(() => other.Decrypt(header, ct));
    }

    [Fact]
    public void Tampered_Ciphertext_Is_Rejected()
    {
        var (alice, bob) = NewSession();
        var (header, ct) = alice.Encrypt(Utf8("integrity"));
        ct[^1] ^= 0xFF;
        Should.Throw<CryptographicException>(() => bob.Decrypt(header, ct));
    }

    [Fact]
    public void Header_RoundTrips()
    {
        var pub = new byte[32];
        RandomNumberGenerator.Fill(pub);
        var header = new RatchetHeader { DhPublicKey = pub, PreviousChainLength = 7, MessageNumber = 42 };
        var restored = RatchetHeader.Deserialize(header.Serialize());
        restored.DhPublicKey.ShouldBe(pub);
        restored.PreviousChainLength.ShouldBe(7);
        restored.MessageNumber.ShouldBe(42);
    }
}
