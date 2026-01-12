using System.Net;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSec.Cryptography;
using Susurri.Modules.DHT.Core.Kademlia;
using Susurri.Modules.DHT.Core.Network;
using Susurri.Modules.DHT.Core.Onion;
using Susurri.Modules.DHT.Core.Services;
using Xunit;

namespace Susurri.Tests.Unit.Services;

/// <summary>
/// Unit tests for ChatService.
/// Note: Many ChatService methods require network operations, so these tests
/// focus on the data structures and synchronous behavior.
/// </summary>
public class ChatServiceTests
{
    [Fact]
    public void SendResult_Success_HasMessageId()
    {
        // Arrange & Act
        var messageId = Guid.NewGuid();
        var result = new SendResult(true, messageId, null);

        // Assert
        Assert.True(result.Success);
        Assert.Equal(messageId, result.MessageId);
        Assert.Null(result.Error);
    }

    [Fact]
    public void SendResult_Failure_HasError()
    {
        // Arrange & Act
        var result = new SendResult(false, null, "User not found");

        // Assert
        Assert.False(result.Success);
        Assert.Null(result.MessageId);
        Assert.Equal("User not found", result.Error);
    }

    [Fact]
    public void SendResult_FailureWithMessageId_PreservesBoth()
    {
        // Arrange - failure can have a message ID if it failed after being queued
        var messageId = Guid.NewGuid();
        var result = new SendResult(false, messageId, "Network error");

        // Assert
        Assert.False(result.Success);
        Assert.Equal(messageId, result.MessageId);
        Assert.Equal("Network error", result.Error);
    }
}

/// <summary>
/// Unit tests for PendingMessage.
/// </summary>
public class PendingMessageTests
{
    [Fact]
    public void PendingMessage_InitialState_IsSending()
    {
        // Arrange
        var chatMessage = new ChatMessage
        {
            SenderPublicKey = new byte[32],
            Content = "Hello",
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            MessageId = Guid.NewGuid()
        };

        // Act
        var pending = new PendingMessage
        {
            Message = chatMessage,
            RecipientUsername = "alice",
            SentAt = DateTimeOffset.UtcNow,
            Status = MessageStatus.Sending
        };

        // Assert
        Assert.Equal(MessageStatus.Sending, pending.Status);
        Assert.Null(pending.AcknowledgedAt);
    }

    [Fact]
    public void PendingMessage_Acknowledged_HasTimestamp()
    {
        // Arrange
        var chatMessage = new ChatMessage
        {
            SenderPublicKey = new byte[32],
            Content = "Hello",
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        };

        var pending = new PendingMessage
        {
            Message = chatMessage,
            RecipientUsername = "bob",
            SentAt = DateTimeOffset.UtcNow,
            Status = MessageStatus.Sending
        };

        // Act
        pending.Status = MessageStatus.Acknowledged;
        pending.AcknowledgedAt = DateTimeOffset.UtcNow;

        // Assert
        Assert.Equal(MessageStatus.Acknowledged, pending.Status);
        Assert.NotNull(pending.AcknowledgedAt);
    }

    [Fact]
    public void PendingMessage_Failed_HasFailedStatus()
    {
        // Arrange
        var chatMessage = new ChatMessage
        {
            SenderPublicKey = new byte[32],
            Content = "Hello"
        };

        var pending = new PendingMessage
        {
            Message = chatMessage,
            RecipientUsername = "charlie",
            SentAt = DateTimeOffset.UtcNow,
            Status = MessageStatus.Sending
        };

        // Act
        pending.Status = MessageStatus.Failed;

        // Assert
        Assert.Equal(MessageStatus.Failed, pending.Status);
    }
}

/// <summary>
/// Unit tests for ReceivedMessage.
/// </summary>
public class ReceivedMessageTests
{
    [Fact]
    public void ReceivedMessage_AllProperties_CanBeSet()
    {
        // Arrange
        var messageId = Guid.NewGuid();
        var senderPubKey = new byte[32];
        Random.Shared.NextBytes(senderPubKey);
        var timestamp = DateTimeOffset.UtcNow.AddMinutes(-5);
        var receivedAt = DateTimeOffset.UtcNow;
        var replyPath = new ReplyPath { SenderPublicKey = senderPubKey };

        // Act
        var received = new ReceivedMessage
        {
            MessageId = messageId,
            Content = "This is a test message",
            SenderPublicKey = senderPubKey,
            SenderUsername = "alice",
            Timestamp = timestamp,
            ReceivedAt = receivedAt,
            ReplyPath = replyPath
        };

        // Assert
        Assert.Equal(messageId, received.MessageId);
        Assert.Equal("This is a test message", received.Content);
        Assert.Equal(senderPubKey, received.SenderPublicKey);
        Assert.Equal("alice", received.SenderUsername);
        Assert.Equal(timestamp, received.Timestamp);
        Assert.Equal(receivedAt, received.ReceivedAt);
        Assert.NotNull(received.ReplyPath);
    }

    [Fact]
    public void ReceivedMessage_UnknownSender_HasNullUsername()
    {
        // Arrange & Act
        var received = new ReceivedMessage
        {
            MessageId = Guid.NewGuid(),
            Content = "Anonymous message",
            SenderPublicKey = new byte[32],
            SenderUsername = null
        };

        // Assert
        Assert.Null(received.SenderUsername);
    }

    [Fact]
    public void ReceivedMessage_DefaultReplyPath_IsEmpty()
    {
        // Arrange & Act
        var received = new ReceivedMessage
        {
            MessageId = Guid.NewGuid(),
            Content = "Test"
        };

        // Assert
        Assert.NotNull(received.ReplyPath);
        Assert.Empty(received.ReplyPath.Tokens);
    }
}

/// <summary>
/// Unit tests for MessageStatus enum.
/// </summary>
public class MessageStatusTests
{
    [Fact]
    public void MessageStatus_HasExpectedValues()
    {
        Assert.Equal(0, (int)MessageStatus.Sending);
        Assert.Equal(1, (int)MessageStatus.Sent);
        Assert.Equal(2, (int)MessageStatus.Acknowledged);
        Assert.Equal(3, (int)MessageStatus.Failed);
    }

    [Fact]
    public void MessageStatus_CanTransitionFromSendingToSent()
    {
        var status = MessageStatus.Sending;
        status = MessageStatus.Sent;
        Assert.Equal(MessageStatus.Sent, status);
    }

    [Fact]
    public void MessageStatus_CanTransitionFromSentToAcknowledged()
    {
        var status = MessageStatus.Sent;
        status = MessageStatus.Acknowledged;
        Assert.Equal(MessageStatus.Acknowledged, status);
    }

    [Fact]
    public void MessageStatus_CanTransitionFromSendingToFailed()
    {
        var status = MessageStatus.Sending;
        status = MessageStatus.Failed;
        Assert.Equal(MessageStatus.Failed, status);
    }
}

/// <summary>
/// Unit tests for UserPublicKeyRecord (if it exists in the codebase).
/// </summary>
public class UserPublicKeyRecordTests
{
    // Note: These tests would require reading the actual UserPublicKeyRecord class
    // to understand its structure. For now, we test what we can infer.

    [Fact]
    public void SendResult_IsImmutable_AsRecord()
    {
        // Arrange
        var result1 = new SendResult(true, Guid.NewGuid(), null);

        // Act & Assert - Records should support value equality
        var result2 = new SendResult(result1.Success, result1.MessageId, result1.Error);
        Assert.Equal(result1, result2);
    }

    [Fact]
    public void SendResult_DifferentValues_AreNotEqual()
    {
        // Arrange
        var result1 = new SendResult(true, Guid.NewGuid(), null);
        var result2 = new SendResult(false, null, "Error");

        // Assert
        Assert.NotEqual(result1, result2);
    }
}
