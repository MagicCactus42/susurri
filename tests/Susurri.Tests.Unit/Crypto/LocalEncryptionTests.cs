using System.Security.Cryptography;
using Shouldly;
using Susurri.Shared.Abstractions.Security;
using Xunit;

namespace Susurri.Tests.Unit.Crypto;

public class LocalEncryptionTests
{
    [Fact]
    public void Encrypt_Decrypt_RoundTrips()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var plaintext = "susurri local store"u8.ToArray();

        var payload = LocalEncryption.Encrypt(key, plaintext);
        payload.ShouldNotBe(plaintext);

        LocalEncryption.Decrypt(key, payload).ShouldBe(plaintext);
    }

    [Fact]
    public void Tampered_Payload_Throws()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var payload = LocalEncryption.Encrypt(key, "data"u8.ToArray());

        payload[^1] ^= 0xFF;
        Should.Throw<CryptographicException>(() => LocalEncryption.Decrypt(key, payload));
    }

    [Fact]
    public void Wrong_Key_Throws()
    {
        var payload = LocalEncryption.Encrypt(RandomNumberGenerator.GetBytes(32), "data"u8.ToArray());
        Should.Throw<CryptographicException>(
            () => LocalEncryption.Decrypt(RandomNumberGenerator.GetBytes(32), payload));
    }

    [Fact]
    public void Subkeys_Are_Deterministic_And_Domain_Separated()
    {
        var master = RandomNumberGenerator.GetBytes(32);

        var history1 = LocalEncryption.DeriveSubkey(master, HkdfContexts.LocalHistory);
        var history2 = LocalEncryption.DeriveSubkey(master, HkdfContexts.LocalHistory);
        var contacts = LocalEncryption.DeriveSubkey(master, HkdfContexts.LocalContacts);

        history1.ShouldBe(history2);
        history1.ShouldNotBe(contacts);
    }
}
