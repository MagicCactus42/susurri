using Microsoft.Extensions.Logging.Abstractions;
using NSec.Cryptography;
using Shouldly;
using Susurri.Modules.DHT.Core.Kademlia;
using Susurri.Modules.DHT.Core.Network;
using Susurri.Modules.DHT.Core.Onion;
using Susurri.Modules.DHT.Core.Services;
using Xunit;

namespace Susurri.Tests.Integration;

[Collection("DhtIntegration")]
public class ChatServiceTests
{
    private static ChatService MakeChat()
    {
        var enc = Key.Create(KeyAgreementAlgorithm.X25519,
            new KeyCreationParameters { ExportPolicy = KeyExportPolicies.AllowPlaintextExport });
        var sign = Key.Create(SignatureAlgorithm.Ed25519,
            new KeyCreationParameters { ExportPolicy = KeyExportPolicies.AllowPlaintextExport });

        return new ChatService(
            enc,
            NullLogger<ChatService>.Instance,
            NullLogger<KademliaDhtNode>.Instance,
            NullLogger<OnionRouter>.Instance,
            NullLogger<RelayService>.Instance,
            NullLogger<ConnectionManager>.Instance,
            sign,
            new ChatNodeOptions());
    }

    [Fact]
    public async Task User_Publishes_Identity_And_Is_Discoverable_By_Username()
    {
        await using var alice = MakeChat();
        await using var bob = MakeChat();

        await alice.StartAsync(0, "alice");
        await bob.StartAsync(0, "bob", new[] { $"127.0.0.1:{alice.LocalPort}" });

        // Bob published his username->pubkey record on start; Alice (who Bob
        // bootstrapped against) can now resolve it from the DHT.
        UserPublicKeyRecord? record = null;
        for (int i = 0; i < 20 && record == null; i++)
        {
            record = await alice.GetPublicKeyAsync("bob");
            if (record == null) await Task.Delay(100);
        }

        record.ShouldNotBeNull();
        record.EncryptionPublicKey.ShouldBe(bob.LocalPublicKey);
        record.SigningPublicKey.ShouldBe(bob.LocalSigningPublicKey);
    }

    [Fact]
    public async Task Unknown_Username_Resolves_To_Null()
    {
        await using var alice = MakeChat();
        await alice.StartAsync(0, "alice");

        var record = await alice.GetPublicKeyAsync("nobody-here");
        record.ShouldBeNull();
    }
}
