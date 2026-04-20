using System.Security.Cryptography;
using NSec.Cryptography;
using Susurri.Modules.DHT.Core.Services;
using Xunit;

namespace Susurri.Tests.Unit.Services;

public class FileTransferMessageTests
{
    private readonly byte[] _senderPublicKey;
    private readonly byte[] _signingPublicKey;
    private readonly Key _signingKey;

    public FileTransferMessageTests()
    {
        _senderPublicKey = new byte[32];
        Random.Shared.NextBytes(_senderPublicKey);
        _signingKey = Key.Create(SignatureAlgorithm.Ed25519);
        _signingPublicKey = _signingKey.PublicKey.Export(KeyBlobFormat.RawPublicKey);
    }

    #region MessageEnvelope Tests

    [Fact]
    public void IsFileTransfer_WithPrefix_ReturnsTrue()
    {
        var data = new byte[] { 0x02, 0x01, 0x02, 0x03 };
        Assert.True(MessageEnvelope.IsFileTransfer(data));
    }

    [Fact]
    public void IsFileTransfer_ChatMessage_ReturnsFalse()
    {
        // ChatMessage starts with pubkey length = 32 (0x20)
        var data = new byte[] { 0x20, 0x01, 0x02 };
        Assert.False(MessageEnvelope.IsFileTransfer(data));
    }

    [Fact]
    public void IsFileTransfer_EmptyData_ReturnsFalse()
    {
        Assert.False(MessageEnvelope.IsFileTransfer(Array.Empty<byte>()));
    }

    #endregion

    #region FileTransferRequest Tests

    [Fact]
    public void FileTransferRequest_RoundTrip_PreservesAllFields()
    {
        var fileHash = SHA256.HashData(new byte[] { 1, 2, 3 });
        var transferId = Guid.NewGuid();

        var original = new FileTransferRequest
        {
            SenderPublicKey = _senderPublicKey,
            SenderSigningPublicKey = _signingPublicKey,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            TransferId = transferId,
            FileName = "test-document.pdf",
            FileSize = 1048576,
            FileHash = fileHash,
            ChunkSize = 15800,
            ChunkCount = 67
        };
        original.Sign(_signingKey);

        var serialized = original.Serialize();
        var deserialized = FileTransferMessage.Deserialize(serialized) as FileTransferRequest;

        Assert.NotNull(deserialized);
        Assert.Equal(original.MessageId, deserialized.MessageId);
        Assert.Equal(original.SenderPublicKey, deserialized.SenderPublicKey);
        Assert.Equal(original.SenderSigningPublicKey, deserialized.SenderSigningPublicKey);
        Assert.Equal(FileMessageType.Request, deserialized.FileType);
        Assert.Equal(transferId, deserialized.TransferId);
        Assert.Equal("test-document.pdf", deserialized.FileName);
        Assert.Equal(1048576, deserialized.FileSize);
        Assert.Equal(fileHash, deserialized.FileHash);
        Assert.Equal(15800, deserialized.ChunkSize);
        Assert.Equal(67, deserialized.ChunkCount);
        Assert.True(deserialized.VerifySignature());
    }

    #endregion

    #region FileTransferAccept Tests

    [Fact]
    public void FileTransferAccept_RoundTrip_PreservesAllFields()
    {
        var transferId = Guid.NewGuid();

        var original = new FileTransferAccept
        {
            SenderPublicKey = _senderPublicKey,
            SenderSigningPublicKey = _signingPublicKey,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            TransferId = transferId
        };
        original.Sign(_signingKey);

        var serialized = original.Serialize();
        var deserialized = FileTransferMessage.Deserialize(serialized) as FileTransferAccept;

        Assert.NotNull(deserialized);
        Assert.Equal(FileMessageType.Accept, deserialized.FileType);
        Assert.Equal(transferId, deserialized.TransferId);
        Assert.True(deserialized.VerifySignature());
    }

    #endregion

    #region FileTransferReject Tests

    [Fact]
    public void FileTransferReject_RoundTrip_PreservesAllFields()
    {
        var transferId = Guid.NewGuid();

        var original = new FileTransferReject
        {
            SenderPublicKey = _senderPublicKey,
            SenderSigningPublicKey = _signingPublicKey,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            TransferId = transferId,
            Reason = "File too large"
        };
        original.Sign(_signingKey);

        var serialized = original.Serialize();
        var deserialized = FileTransferMessage.Deserialize(serialized) as FileTransferReject;

        Assert.NotNull(deserialized);
        Assert.Equal(FileMessageType.Reject, deserialized.FileType);
        Assert.Equal(transferId, deserialized.TransferId);
        Assert.Equal("File too large", deserialized.Reason);
        Assert.True(deserialized.VerifySignature());
    }

    #endregion

    #region FileChunkMessage Tests

    [Fact]
    public void FileChunkMessage_RoundTrip_PreservesAllFields()
    {
        var transferId = Guid.NewGuid();
        var chunkData = new byte[1024];
        Random.Shared.NextBytes(chunkData);

        var original = new FileChunkMessage
        {
            SenderPublicKey = _senderPublicKey,
            SenderSigningPublicKey = _signingPublicKey,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            TransferId = transferId,
            ChunkIndex = 42,
            Data = chunkData
        };
        original.Sign(_signingKey);

        var serialized = original.Serialize();
        var deserialized = FileTransferMessage.Deserialize(serialized) as FileChunkMessage;

        Assert.NotNull(deserialized);
        Assert.Equal(FileMessageType.Chunk, deserialized.FileType);
        Assert.Equal(transferId, deserialized.TransferId);
        Assert.Equal(42, deserialized.ChunkIndex);
        Assert.Equal(chunkData, deserialized.Data);
        Assert.True(deserialized.VerifySignature());
    }

    [Fact]
    public void FileChunkMessage_EmptyData_Survives()
    {
        var original = new FileChunkMessage
        {
            SenderPublicKey = _senderPublicKey,
            SenderSigningPublicKey = _signingPublicKey,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            TransferId = Guid.NewGuid(),
            ChunkIndex = 0,
            Data = Array.Empty<byte>()
        };
        original.Sign(_signingKey);

        var serialized = original.Serialize();
        var deserialized = FileTransferMessage.Deserialize(serialized) as FileChunkMessage;

        Assert.NotNull(deserialized);
        Assert.Empty(deserialized.Data);
    }

    #endregion

    #region FileTransferComplete Tests

    [Fact]
    public void FileTransferComplete_RoundTrip_PreservesAllFields()
    {
        var transferId = Guid.NewGuid();

        var original = new FileTransferComplete
        {
            SenderPublicKey = _senderPublicKey,
            SenderSigningPublicKey = _signingPublicKey,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            TransferId = transferId
        };
        original.Sign(_signingKey);

        var serialized = original.Serialize();
        var deserialized = FileTransferMessage.Deserialize(serialized) as FileTransferComplete;

        Assert.NotNull(deserialized);
        Assert.Equal(FileMessageType.Complete, deserialized.FileType);
        Assert.Equal(transferId, deserialized.TransferId);
        Assert.True(deserialized.VerifySignature());
    }

    #endregion

    #region Signature Verification Tests

    [Fact]
    public void VerifySignature_TamperedContent_ReturnsFalse()
    {
        var original = new FileTransferRequest
        {
            SenderPublicKey = _senderPublicKey,
            SenderSigningPublicKey = _signingPublicKey,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            TransferId = Guid.NewGuid(),
            FileName = "original.txt",
            FileSize = 100,
            FileHash = new byte[32],
            ChunkSize = 15800,
            ChunkCount = 1
        };
        original.Sign(_signingKey);

        var serialized = original.Serialize();
        var deserialized = FileTransferMessage.Deserialize(serialized) as FileTransferRequest;

        // Tamper with the deserialized message
        var tampered = deserialized! with { FileName = "tampered.txt" };
        tampered.Signature = deserialized.Signature; // Keep old signature

        Assert.False(tampered.VerifySignature());
    }

    [Fact]
    public void VerifySignature_NoSignature_ReturnsFalse()
    {
        var message = new FileTransferAccept
        {
            SenderPublicKey = _senderPublicKey,
            SenderSigningPublicKey = _signingPublicKey,
            TransferId = Guid.NewGuid()
        };
        // Don't sign

        Assert.False(message.VerifySignature());
    }

    #endregion

    #region Serialization Size Tests

    [Fact]
    public void FileChunkMessage_FitsInPaddingBlock()
    {
        // Verify that a chunk with DefaultChunkSize data fits within 16KB padding
        var chunkData = new byte[FileTransferService.DefaultChunkSize];
        Random.Shared.NextBytes(chunkData);

        var chunk = new FileChunkMessage
        {
            SenderPublicKey = _senderPublicKey,
            SenderSigningPublicKey = _signingPublicKey,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            TransferId = Guid.NewGuid(),
            ChunkIndex = 0,
            Data = chunkData
        };
        chunk.Sign(_signingKey);

        var serialized = chunk.Serialize();
        var maxPayload = Modules.DHT.Core.Onion.MessagePadding.GetMaxPayloadSize();

        Assert.True(serialized.Length <= maxPayload,
            $"Serialized chunk ({serialized.Length} bytes) exceeds max payload ({maxPayload} bytes)");
    }

    [Fact]
    public void Serialized_StartsWithEnvelopeByte()
    {
        var message = new FileTransferAccept
        {
            SenderPublicKey = _senderPublicKey,
            SenderSigningPublicKey = _signingPublicKey,
            TransferId = Guid.NewGuid()
        };
        message.Sign(_signingKey);

        var serialized = message.Serialize();

        Assert.Equal(MessageEnvelope.FileTransfer, serialized[0]);
    }

    #endregion

    #region FileMessageType Enum Tests

    [Fact]
    public void FileMessageType_HasExpectedValues()
    {
        Assert.Equal(0x01, (byte)FileMessageType.Request);
        Assert.Equal(0x02, (byte)FileMessageType.Accept);
        Assert.Equal(0x03, (byte)FileMessageType.Reject);
        Assert.Equal(0x04, (byte)FileMessageType.Chunk);
        Assert.Equal(0x05, (byte)FileMessageType.Complete);
    }

    #endregion
}
