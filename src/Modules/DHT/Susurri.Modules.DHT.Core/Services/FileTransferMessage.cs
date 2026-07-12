using System.Security.Cryptography;
using NSec.Cryptography;

namespace Susurri.Modules.DHT.Core.Services;

/// <summary>
/// Envelope prefix byte used to distinguish file transfer payloads from chat messages
/// in the onion routing layer. ChatMessage starts with byte 0x20 (pubkey length = 32),
/// so 0x02 is unambiguous.
/// </summary>
public static class MessageEnvelope
{
    public const byte FileTransfer = 0x02;
    public const byte GroupMessage = 0x03;

    /// <summary>
    /// Returns true if the first byte of an unpadded payload indicates a file transfer message.
    /// </summary>
    public static bool IsFileTransfer(byte[] data) =>
        data.Length > 0 && data[0] == FileTransfer;

    /// <summary>
    /// Returns true if the first byte of an unpadded payload indicates a group message.
    /// </summary>
    public static bool IsGroupMessage(byte[] data) =>
        data.Length > 0 && data[0] == GroupMessage;
}

public enum FileMessageType : byte
{
    Request = 0x01,
    Accept = 0x02,
    Reject = 0x03,
    Chunk = 0x04,
    Complete = 0x05
}

/// <summary>
/// Base class for all file transfer protocol messages.
/// Each message is signed with Ed25519 for authentication.
///
/// Wire format (after the 0x02 envelope prefix):
///   [1: SenderPublicKey.Length][N: SenderPublicKey]
///   [1: SenderSigningPublicKey.Length][N: SenderSigningPublicKey]
///   [1: FileMessageType]
///   [8: Timestamp]
///   [16: MessageId]
///   [variable: type-specific payload]
///   [2: Signature.Length][N: Signature]
/// </summary>
public abstract record FileTransferMessage
{
    private const int MaxPayloadSize = 64 * 1024;
    private const int MaxStringLength = 256;

    public byte[] SenderPublicKey { get; init; } = Array.Empty<byte>();
    public byte[] SenderSigningPublicKey { get; init; } = Array.Empty<byte>();
    public abstract FileMessageType FileType { get; }
    public long Timestamp { get; init; }
    public Guid MessageId { get; init; } = Guid.NewGuid();
    public byte[] Signature { get; set; } = Array.Empty<byte>();

    /// <summary>
    /// Returns the bytes that are signed (everything except the signature itself).
    /// </summary>
    public byte[] GetSignableData()
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);

        writer.Write((byte)SenderPublicKey.Length);
        writer.Write(SenderPublicKey);
        writer.Write((byte)SenderSigningPublicKey.Length);
        writer.Write(SenderSigningPublicKey);
        writer.Write((byte)FileType);
        writer.Write(Timestamp);
        writer.Write(MessageId.ToByteArray());
        SerializePayload(writer);

        return ms.ToArray();
    }

    /// <summary>
    /// Serializes the complete message including the 0x02 envelope prefix.
    /// </summary>
    public byte[] Serialize()
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);

        // Envelope prefix
        writer.Write(MessageEnvelope.FileTransfer);

        // Common header
        writer.Write((byte)SenderPublicKey.Length);
        writer.Write(SenderPublicKey);
        writer.Write((byte)SenderSigningPublicKey.Length);
        writer.Write(SenderSigningPublicKey);
        writer.Write((byte)FileType);
        writer.Write(Timestamp);
        writer.Write(MessageId.ToByteArray());

        // Type-specific payload
        SerializePayload(writer);

        // Signature
        writer.Write((ushort)Signature.Length);
        writer.Write(Signature);

        return ms.ToArray();
    }

    protected abstract void SerializePayload(BinaryWriter writer);

    /// <summary>
    /// Deserializes a file transfer message. Caller must have already consumed the 0x02 envelope byte.
    /// </summary>
    public static FileTransferMessage Deserialize(byte[] data)
    {
        using var ms = new MemoryStream(data);
        using var reader = new BinaryReader(ms);

        // Skip envelope prefix if present
        var firstByte = reader.ReadByte();
        if (firstByte != MessageEnvelope.FileTransfer)
        {
            // Reset and try reading as header directly
            ms.Position = 0;
        }

        var pubKeyLen = reader.ReadByte();
        if (pubKeyLen > 32)
            throw new InvalidDataException($"Public key too large: {pubKeyLen}");
        var senderPublicKey = reader.ReadBytes(pubKeyLen);

        var sigPubKeyLen = reader.ReadByte();
        if (sigPubKeyLen > 32)
            throw new InvalidDataException($"Signing public key too large: {sigPubKeyLen}");
        var senderSigningPublicKey = reader.ReadBytes(sigPubKeyLen);

        var fileType = (FileMessageType)reader.ReadByte();
        var timestamp = reader.ReadInt64();
        var messageId = new Guid(reader.ReadBytes(16));

        FileTransferMessage message = fileType switch
        {
            FileMessageType.Request => FileTransferRequest.DeserializePayload(reader),
            FileMessageType.Accept => FileTransferAccept.DeserializePayload(reader),
            FileMessageType.Reject => FileTransferReject.DeserializePayload(reader),
            FileMessageType.Chunk => FileChunkMessage.DeserializePayload(reader),
            FileMessageType.Complete => FileTransferComplete.DeserializePayload(reader),
            _ => throw new InvalidDataException($"Unknown file message type: {fileType}")
        };

        // Set common fields
        message = message with
        {
            SenderPublicKey = senderPublicKey,
            SenderSigningPublicKey = senderSigningPublicKey,
            Timestamp = timestamp,
            MessageId = messageId
        };

        // Read signature
        var sigLen = reader.ReadUInt16();
        if (sigLen > 64)
            throw new InvalidDataException($"Signature too large: {sigLen}");
        message.Signature = reader.ReadBytes(sigLen);

        return message;
    }

    public bool VerifySignature()
    {
        if (SenderSigningPublicKey.Length == 0 || Signature.Length == 0)
            return false;

        try
        {
            var signingPubKey = PublicKey.Import(
                SignatureAlgorithm.Ed25519,
                SenderSigningPublicKey,
                KeyBlobFormat.RawPublicKey);

            return SignatureAlgorithm.Ed25519.Verify(
                signingPubKey, GetSignableData(), Signature);
        }
        catch
        {
            return false;
        }
    }

    public void Sign(Key signingKey)
    {
        Signature = SignatureAlgorithm.Ed25519.Sign(signingKey, GetSignableData());
    }

    protected static string ReadBoundedString(BinaryReader reader)
    {
        var s = reader.ReadString();
        if (s.Length > MaxStringLength)
            throw new InvalidDataException($"String too long: {s.Length}");
        return s;
    }
}

/// <summary>
/// Initiates a file transfer. Sent from sender to recipient.
/// </summary>
public sealed record FileTransferRequest : FileTransferMessage
{
    public override FileMessageType FileType => FileMessageType.Request;

    public Guid TransferId { get; init; } = Guid.NewGuid();
    public string FileName { get; init; } = string.Empty;
    public long FileSize { get; init; }
    public byte[] FileHash { get; init; } = Array.Empty<byte>(); // SHA-256
    public int ChunkSize { get; init; }
    public int ChunkCount { get; init; }

    protected override void SerializePayload(BinaryWriter writer)
    {
        writer.Write(TransferId.ToByteArray());
        writer.Write(FileName);
        writer.Write(FileSize);
        writer.Write((byte)FileHash.Length);
        writer.Write(FileHash);
        writer.Write(ChunkSize);
        writer.Write(ChunkCount);
    }

    public static FileTransferRequest DeserializePayload(BinaryReader reader)
    {
        var transferId = new Guid(reader.ReadBytes(16));
        var fileName = ReadBoundedString(reader);
        var fileSize = reader.ReadInt64();
        var hashLen = reader.ReadByte();
        var fileHash = reader.ReadBytes(hashLen);
        var chunkSize = reader.ReadInt32();
        var chunkCount = reader.ReadInt32();

        return new FileTransferRequest
        {
            TransferId = transferId,
            FileName = fileName,
            FileSize = fileSize,
            FileHash = fileHash,
            ChunkSize = chunkSize,
            ChunkCount = chunkCount
        };
    }
}

/// <summary>
/// Accepts a pending file transfer.
/// </summary>
public sealed record FileTransferAccept : FileTransferMessage
{
    public override FileMessageType FileType => FileMessageType.Accept;

    public Guid TransferId { get; init; }

    protected override void SerializePayload(BinaryWriter writer)
    {
        writer.Write(TransferId.ToByteArray());
    }

    public static FileTransferAccept DeserializePayload(BinaryReader reader)
    {
        var transferId = new Guid(reader.ReadBytes(16));
        return new FileTransferAccept { TransferId = transferId };
    }
}

/// <summary>
/// Rejects a pending file transfer.
/// </summary>
public sealed record FileTransferReject : FileTransferMessage
{
    public override FileMessageType FileType => FileMessageType.Reject;

    public Guid TransferId { get; init; }
    public string Reason { get; init; } = string.Empty;

    protected override void SerializePayload(BinaryWriter writer)
    {
        writer.Write(TransferId.ToByteArray());
        writer.Write(Reason);
    }

    public static FileTransferReject DeserializePayload(BinaryReader reader)
    {
        var transferId = new Guid(reader.ReadBytes(16));
        var reason = ReadBoundedString(reader);
        return new FileTransferReject { TransferId = transferId, Reason = reason };
    }
}

/// <summary>
/// A chunk of file data.
/// </summary>
public sealed record FileChunkMessage : FileTransferMessage
{
    private const int MaxChunkSize = 16 * 1024;

    public override FileMessageType FileType => FileMessageType.Chunk;

    public Guid TransferId { get; init; }
    public int ChunkIndex { get; init; }
    public byte[] Data { get; init; } = Array.Empty<byte>();

    protected override void SerializePayload(BinaryWriter writer)
    {
        writer.Write(TransferId.ToByteArray());
        writer.Write(ChunkIndex);
        writer.Write(Data.Length);
        writer.Write(Data);
    }

    public static FileChunkMessage DeserializePayload(BinaryReader reader)
    {
        var transferId = new Guid(reader.ReadBytes(16));
        var chunkIndex = reader.ReadInt32();
        var dataLen = reader.ReadInt32();
        if (dataLen < 0 || dataLen > MaxChunkSize)
            throw new InvalidDataException($"Invalid chunk data length: {dataLen}");
        var data = reader.ReadBytes(dataLen);

        return new FileChunkMessage
        {
            TransferId = transferId,
            ChunkIndex = chunkIndex,
            Data = data
        };
    }
}

/// <summary>
/// Signals that all chunks have been sent.
/// </summary>
public sealed record FileTransferComplete : FileTransferMessage
{
    public override FileMessageType FileType => FileMessageType.Complete;

    public Guid TransferId { get; init; }

    protected override void SerializePayload(BinaryWriter writer)
    {
        writer.Write(TransferId.ToByteArray());
    }

    public static FileTransferComplete DeserializePayload(BinaryReader reader)
    {
        var transferId = new Guid(reader.ReadBytes(16));
        return new FileTransferComplete { TransferId = transferId };
    }
}
