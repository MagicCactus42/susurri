using Microsoft.Extensions.Logging.Abstractions;
using NSec.Cryptography;
using Shouldly;
using Susurri.Modules.DHT.Core.Kademlia;
using Susurri.Modules.DHT.Core.Network;
using Susurri.Modules.DHT.Core.Onion;
using Susurri.Modules.DHT.Core.Onion.GroupChat;
using Susurri.Modules.DHT.Core.Services;
using Xunit;
using Xunit.Abstractions;

namespace Susurri.Tests.Integration;

[Collection("DhtIntegration")]
public class LocalChatDeliveryTests
{
    private readonly ITestOutputHelper _output;

    public LocalChatDeliveryTests(ITestOutputHelper output)
    {
        _output = output;
    }

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
            new ChatNodeOptions(AllowLoopback: true));
    }

    private static async Task<T?> PollAsync<T>(Func<T?> probe, int attempts = 80, int delayMs = 150)
        where T : class
    {
        for (var i = 0; i < attempts; i++)
        {
            var value = probe();
            if (value != null)
                return value;
            await Task.Delay(delayMs);
        }
        return null;
    }

    /// <summary>
    /// Five real ChatService nodes on loopback, full UDP transport, no test
    /// overrides. Reproduces the CLI scenario: alice looks bob up in the DHT
    /// and sends a double-ratchet DM that must survive a 3-hop onion route.
    /// </summary>
    [Fact]
    public async Task Direct_Message_Delivers_Across_A_Local_Onion_Route()
    {
        var nodes = Enumerable.Range(0, 5).Select(_ => MakeChat()).ToArray();
        try
        {
            // Start the seed, then everyone joins through it — same shape as
            // `--bootstrap` + N logins against one seed.
            await nodes[0].StartAsync(0, $"user0");
            var seed = $"127.0.0.1:{nodes[0].LocalPort}";
            for (var i = 1; i < nodes.Length; i++)
                await nodes[i].StartAsync(0, $"user{i}", new[] { seed });

            var alice = nodes[1];
            var bob = nodes[4];

            foreach (var n in nodes)
            {
                var peers = await PollAsync(() => n.PeerCount >= 3 ? "ok" : null);
                peers.ShouldNotBeNull($"node on port {n.LocalPort} never reached 3 peers (had {n.PeerCount})");
            }

            var bobKey = await PollAsync(() => alice.GetPublicKeyAsync("user4").GetAwaiter().GetResult());
            bobKey.ShouldNotBeNull("alice could not resolve bob's key from the DHT");
            bobKey!.EncryptionPublicKey.ShouldBe(bob.LocalPublicKey);

            var send = await alice.SendMessageAsync("user4", "czesc bob, to alice");
            _output.WriteLine($"send success={send.Success} error={send.Error}");
            send.Success.ShouldBeTrue(send.Error);

            var received = await PollAsync(() =>
            {
                var msgs = bob.GetMessages();
                return msgs.Count > 0 ? msgs : null;
            });

            received.ShouldNotBeNull("bob never received the direct message");
            received!.ShouldContain(m => m.Content == "czesc bob, to alice");
        }
        finally
        {
            foreach (var n in nodes)
                await n.DisposeAsync();
        }
    }

    /// <summary>
    /// Group chat: alice creates a group, invites bob, bob joins from the invite
    /// code, alice sends a forward-secret (sender-key) group message that must
    /// reach bob over the onion route and decrypt.
    /// </summary>
    [Fact]
    public async Task Group_Message_Delivers_And_Decrypts_On_A_Local_Cluster()
    {
        var nodes = Enumerable.Range(0, 5).Select(_ => MakeChat()).ToArray();
        try
        {
            await nodes[0].StartAsync(0, "g0");
            var seed = $"127.0.0.1:{nodes[0].LocalPort}";
            for (var i = 1; i < nodes.Length; i++)
                await nodes[i].StartAsync(0, $"g{i}", new[] { seed });

            var alice = nodes[1];
            var bob = nodes[4];

            foreach (var n in nodes)
                (await PollAsync(() => n.PeerCount >= 3 ? "ok" : null))
                    .ShouldNotBeNull($"node on port {n.LocalPort} never reached 3 peers");

            var group = alice.CreateGroup("book-club");
            var wrapped = alice.InviteMember(group.GroupId, bob.LocalPublicKey);
            var code = GroupInvite.Encode("book-club", wrapped, group.OwnerSigningPublicKey);

            var (name, key, owner) = GroupInvite.Decode(code);
            var joined = bob.JoinGroup(key, name, owner);
            joined.ShouldNotBeNull("bob could not join the group from the invite");
            joined!.GroupId.ShouldBe(group.GroupId);

            var delivered = await alice.SendGroupMessageAsync(group.GroupId, "witajcie w grupie");
            _output.WriteLine($"group delivered to {delivered} member(s)");
            delivered.ShouldBeGreaterThan(0);

            var received = await PollAsync(() =>
            {
                var msgs = bob.GetGroupMessages(group.GroupId);
                return msgs.Count > 0 ? msgs : null;
            });

            received.ShouldNotBeNull("bob never received the group message");
            received!.ShouldContain(m => m.Content == "witajcie w grupie");
        }
        finally
        {
            foreach (var n in nodes)
                await n.DisposeAsync();
        }
    }
}
