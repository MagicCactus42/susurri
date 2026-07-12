using Shouldly;
using Susurri.Modules.DHT.Core.Onion.GroupChat;
using Susurri.Modules.DHT.Core.Services;

namespace Susurri.Tests.E2E;

[Collection("OnionE2E")]
public class GroupDeliveryTests
{
    [Fact]
    public async Task GroupMessage_Delivers_Through_Onion_And_Decrypts()
    {
        await using var bed = await OnionTestbed.StartAsync(count: 5);

        var captured = new EventCapture<EncryptedGroupMessage>();
        bed.Bob.OnGroupMessageReceived += e => captured.HandleAsync(e);

        var groupKey = GroupKey.Create();
        var message = new GroupMessage
        {
            GroupId = groupKey.GroupId,
            SenderPublicKey = bed.AliceKeys.EncryptionPublicKey,
            Content = "hello everyone in the group",
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        };

        var encrypted = message.EncryptUnpadded(groupKey);
        var body = encrypted.Serialize();
        var envelope = new byte[1 + body.Length];
        envelope[0] = MessageEnvelope.GroupMessage;
        System.Buffer.BlockCopy(body, 0, envelope, 1, body.Length);

        await bed.Alice.SendRawAsync(envelope, bed.BobKeys.EncryptionPublicKey, bed.RelayPath());

        var received = await captured.WaitFirstAsync();
        received.GroupId.ShouldBe(groupKey.GroupId);

        var decrypted = GroupMessage.DecryptUnpadded(received, groupKey);
        decrypted.Content.ShouldBe("hello everyone in the group");
        decrypted.SenderPublicKey.ShouldBe(bed.AliceKeys.EncryptionPublicKey);
    }

    [Fact]
    public async Task GroupMessage_Not_Decryptable_With_Wrong_Key()
    {
        await using var bed = await OnionTestbed.StartAsync(count: 3);

        var captured = new EventCapture<EncryptedGroupMessage>();
        bed.Bob.OnGroupMessageReceived += e => captured.HandleAsync(e);

        var groupKey = GroupKey.Create();
        var message = new GroupMessage
        {
            GroupId = groupKey.GroupId,
            SenderPublicKey = bed.AliceKeys.EncryptionPublicKey,
            Content = "secret",
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        };

        var encrypted = message.EncryptUnpadded(groupKey);
        var body = encrypted.Serialize();
        var envelope = new byte[1 + body.Length];
        envelope[0] = MessageEnvelope.GroupMessage;
        System.Buffer.BlockCopy(body, 0, envelope, 1, body.Length);

        await bed.Alice.SendRawAsync(envelope, bed.BobKeys.EncryptionPublicKey, bed.RelayPath());
        var received = await captured.WaitFirstAsync();

        var wrongKey = GroupKey.Create();
        Should.Throw<Exception>(() => GroupMessage.DecryptUnpadded(received, wrongKey));
    }
}
