using System.Text;
using Shouldly;
using Susurri.Modules.DHT.Core.Onion;
using Xunit;

namespace Susurri.Tests.Unit.Onion;

public class OnionLayerTests
{
    [Fact]
    public void OnionLayer_Serialization_RoundTrip()
    {
        // Arrange
        var layer = new OnionLayer
        {
            EphemeralPublicKey = new byte[32],
            Nonce = new byte[12],
            Ciphertext = Encoding.UTF8.GetBytes("encrypted content")
        };
        new Random(42).NextBytes(layer.EphemeralPublicKey);
        new Random(43).NextBytes(layer.Nonce);

        // Act
        var bytes = layer.Serialize();
        var restored = OnionLayer.Deserialize(bytes);

        // Assert
        restored.EphemeralPublicKey.ShouldBe(layer.EphemeralPublicKey);
        restored.Nonce.ShouldBe(layer.Nonce);
        restored.Ciphertext.ShouldBe(layer.Ciphertext);
    }

    [Fact]
    public void OnionLayerContent_Relay_Serialization_RoundTrip()
    {
        // Arrange
        var content = new OnionLayerContent
        {
            Type = OnionLayerType.Relay,
            NextHopAddress = "192.168.1.100",
            NextHopPort = 8080,
            ReplyToken = new byte[] { 1, 2, 3, 4, 5 },
            InnerPayload = Encoding.UTF8.GetBytes("inner payload")
        };

        // Act
        var bytes = content.Serialize();
        var restored = OnionLayerContent.Deserialize(bytes);

        // Assert
        restored.Type.ShouldBe(OnionLayerType.Relay);
        restored.NextHopAddress.ShouldBe("192.168.1.100");
        restored.NextHopPort.ShouldBe(8080);
        restored.ReplyToken.ShouldBe(content.ReplyToken);
        restored.InnerPayload.ShouldBe(content.InnerPayload);
    }

    [Fact]
    public void OnionLayerContent_FinalHop_Serialization_RoundTrip()
    {
        // Arrange
        var content = new OnionLayerContent
        {
            Type = OnionLayerType.FinalHop,
            ReplyToken = new byte[] { 10, 20, 30 },
            InnerPayload = Encoding.UTF8.GetBytes("final payload")
        };

        // Act
        var bytes = content.Serialize();
        var restored = OnionLayerContent.Deserialize(bytes);

        // Assert
        restored.Type.ShouldBe(OnionLayerType.FinalHop);
        restored.NextHopAddress.ShouldBeNull();
        restored.ReplyToken.ShouldBe(content.ReplyToken);
        restored.InnerPayload.ShouldBe(content.InnerPayload);
    }

    [Fact]
    public void ChatMessage_Serialization_RoundTrip()
    {
        // Arrange
        var senderPubKey = new byte[32];
        new Random(42).NextBytes(senderPubKey);

        var message = new ChatMessage
        {
            SenderPublicKey = senderPubKey,
            Content = "Hello, World!",
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            MessageId = Guid.NewGuid()
        };

        // Act
        var bytes = message.Serialize();
        var restored = ChatMessage.Deserialize(bytes);

        // Assert
        restored.SenderPublicKey.ShouldBe(message.SenderPublicKey);
        restored.Content.ShouldBe(message.Content);
        restored.Timestamp.ShouldBe(message.Timestamp);
        restored.MessageId.ShouldBe(message.MessageId);
    }

    [Fact]
    public void ReplyPath_Serialization_RoundTrip()
    {
        // Arrange
        var senderPubKey = new byte[32];
        new Random(42).NextBytes(senderPubKey);

        var replyPath = new ReplyPath
        {
            SenderPublicKey = senderPubKey,
            Tokens = new List<byte[]>
            {
                new byte[] { 1, 2, 3 },
                new byte[] { 4, 5, 6 },
                new byte[] { 7, 8, 9 }
            }
        };

        // Act
        var bytes = replyPath.Serialize();
        var restored = ReplyPath.Deserialize(bytes);

        // Assert
        restored.SenderPublicKey.ShouldBe(replyPath.SenderPublicKey);
        restored.Tokens.Count.ShouldBe(3);
        restored.Tokens[0].ShouldBe(replyPath.Tokens[0]);
        restored.Tokens[1].ShouldBe(replyPath.Tokens[1]);
        restored.Tokens[2].ShouldBe(replyPath.Tokens[2]);
    }

    [Fact]
    public void RecipientPayload_Serialization_RoundTrip()
    {
        // Arrange
        var senderPubKey = new byte[32];
        new Random(42).NextBytes(senderPubKey);

        var message = new ChatMessage
        {
            SenderPublicKey = senderPubKey,
            Content = "Test message",
            Timestamp = 12345678,
            MessageId = Guid.NewGuid()
        };

        var payload = new RecipientPayload
        {
            Message = message.Serialize(),
            ReplyPath = new ReplyPath
            {
                SenderPublicKey = senderPubKey,
                Tokens = new List<byte[]> { new byte[] { 1, 2, 3 } }
            }
        };

        // Act
        var bytes = payload.Serialize();
        var restored = RecipientPayload.Deserialize(bytes);

        // Assert
        restored.Message.ShouldBe(payload.Message);
        restored.ReplyPath.SenderPublicKey.ShouldBe(senderPubKey);
    }

    [Fact]
    public void ReplyTokenContent_Serialization_RoundTrip()
    {
        // Arrange
        var sessionKey = new byte[32];
        new Random(42).NextBytes(sessionKey);

        var content = new ReplyTokenContent
        {
            PreviousHopAddress = "10.0.0.1",
            PreviousHopPort = 9000,
            SessionKey = sessionKey
        };

        // Act
        var bytes = content.Serialize();
        var restored = ReplyTokenContent.Deserialize(bytes);

        // Assert
        restored.PreviousHopAddress.ShouldBe("10.0.0.1");
        restored.PreviousHopPort.ShouldBe(9000);
        restored.SessionKey.ShouldBe(sessionKey);
    }

    [Fact]
    public void AckMessage_Serialization_RoundTrip()
    {
        // Arrange
        var ack = new AckMessage
        {
            MessageId = Guid.NewGuid(),
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        };

        // Act
        var bytes = ack.Serialize();
        var restored = AckMessage.Deserialize(bytes);

        // Assert
        restored.MessageId.ShouldBe(ack.MessageId);
        restored.Timestamp.ShouldBe(ack.Timestamp);
    }

    [Fact]
    public void ChatMessage_UnicodeContent_PreservedInSerialization()
    {
        // Arrange
        var message = new ChatMessage
        {
            SenderPublicKey = new byte[32],
            Content = "Hello 世界! 🎉 Привет مرحبا",
            Timestamp = 12345678,
            MessageId = Guid.NewGuid()
        };

        // Act
        var bytes = message.Serialize();
        var restored = ChatMessage.Deserialize(bytes);

        // Assert
        restored.Content.ShouldBe(message.Content);
    }

    [Fact]
    public void ChatMessage_LongContent_SerializesCorrectly()
    {
        // Arrange
        var longContent = new string('x', 10000);
        var message = new ChatMessage
        {
            SenderPublicKey = new byte[32],
            Content = longContent,
            Timestamp = 12345678,
            MessageId = Guid.NewGuid()
        };

        // Act
        var bytes = message.Serialize();
        var restored = ChatMessage.Deserialize(bytes);

        // Assert
        restored.Content.ShouldBe(longContent);
    }
}
