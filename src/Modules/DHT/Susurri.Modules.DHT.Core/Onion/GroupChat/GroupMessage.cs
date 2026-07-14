using System.Security.Cryptography;
using Susurri.Shared.Abstractions.Security;

namespace Susurri.Modules.DHT.Core.Onion.GroupChat;

public sealed class GroupMessage
{
    public Guid GroupId { get; init; }
    public Guid MessageId { get; init; } = Guid.NewGuid();
    public byte[] SenderPublicKey { get; init; } = Array.Empty<byte>();
    public string Content { get; init; } = string.Empty;
    public long Timestamp { get; init; }

    public byte[] Serialize()
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);

        writer.Write(GroupId.ToByteArray());
        writer.Write(MessageId.ToByteArray());
        writer.Write((byte)SenderPublicKey.Length);
        writer.Write(SenderPublicKey);
        writer.Write(Content);
        writer.Write(Timestamp);

        return ms.ToArray();
    }

    public static GroupMessage Deserialize(byte[] data)
    {
        using var ms = new MemoryStream(data);
        using var reader = new BinaryReader(ms);

        var groupId = new Guid(reader.ReadBytes(16));
        var messageId = new Guid(reader.ReadBytes(16));
        var pubKeyLen = reader.ReadByte();
        var senderPublicKey = reader.ReadBytes(pubKeyLen);
        var content = reader.ReadString();
        var timestamp = reader.ReadInt64();

        return new GroupMessage
        {
            GroupId = groupId,
            MessageId = messageId,
            SenderPublicKey = senderPublicKey,
            Content = content,
            Timestamp = timestamp
        };
    }

    public EncryptedGroupMessage Encrypt(GroupKey groupKey)
    {
        var plaintext = Serialize();
        var paddedPlaintext = MessagePadding.Pad(plaintext);
        var nonce = groupKey.GenerateNonce();
        var ciphertext = groupKey.Encrypt(paddedPlaintext, nonce);

        return new EncryptedGroupMessage
        {
            GroupId = GroupId,
            MessageId = MessageId,
            Nonce = nonce,
            Ciphertext = ciphertext,
            KeyVersion = groupKey.Version
        };
    }

    public static GroupMessage Decrypt(EncryptedGroupMessage encrypted, GroupKey groupKey)
    {
        if (encrypted.GroupId != groupKey.GroupId)
            throw new InvalidOperationException("Group ID mismatch");

        var paddedPlaintext = groupKey.Decrypt(encrypted.Ciphertext, encrypted.Nonce);
        var plaintext = MessagePadding.Unpad(paddedPlaintext);

        return Deserialize(plaintext);
    }

    /// <summary>
    /// Encrypts without the internal 16 KB padding, for delivery inside an onion
    /// recipient layer that already applies fixed-size padding — avoids padding
    /// twice (which would overflow the onion block).
    /// </summary>
    public EncryptedGroupMessage EncryptUnpadded(GroupKey groupKey)
    {
        var nonce = groupKey.GenerateNonce();
        var ciphertext = groupKey.Encrypt(Serialize(), nonce);

        return new EncryptedGroupMessage
        {
            GroupId = GroupId,
            MessageId = MessageId,
            Nonce = nonce,
            Ciphertext = ciphertext,
            KeyVersion = groupKey.Version
        };
    }

    public static GroupMessage DecryptUnpadded(EncryptedGroupMessage encrypted, GroupKey groupKey)
    {
        if (encrypted.GroupId != groupKey.GroupId)
            throw new InvalidOperationException("Group ID mismatch");

        var plaintext = groupKey.Decrypt(encrypted.Ciphertext, encrypted.Nonce);
        return Deserialize(plaintext);
    }
}

public sealed class EncryptedGroupMessage
{
    public Guid GroupId { get; init; }
    public Guid MessageId { get; init; }
    public byte[] Nonce { get; init; } = Array.Empty<byte>();
    public byte[] Ciphertext { get; init; } = Array.Empty<byte>();
    public int KeyVersion { get; init; }

    public byte[] Serialize()
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);

        writer.Write(GroupId.ToByteArray());
        writer.Write(MessageId.ToByteArray());
        writer.Write((byte)Nonce.Length);
        writer.Write(Nonce);
        writer.Write(Ciphertext.Length);
        writer.Write(Ciphertext);
        writer.Write(KeyVersion);

        return ms.ToArray();
    }

    public static EncryptedGroupMessage Deserialize(byte[] data)
    {
        using var ms = new MemoryStream(data);
        using var reader = new BinaryReader(ms);

        var groupId = new Guid(reader.ReadBytes(16));
        var messageId = new Guid(reader.ReadBytes(16));
        var nonceLen = reader.ReadByte();
        var nonce = reader.ReadBytes(nonceLen);
        var ciphertextLen = reader.ReadInt32();
        if (ciphertextLen < 0 || ciphertextLen > SecurityLimits.MaxValueSize)
            throw new InvalidDataException($"EncryptedGroupMessage ciphertext length {ciphertextLen} out of range");
        var ciphertext = reader.ReadBytes(ciphertextLen);
        var keyVersion = reader.ReadInt32();

        return new EncryptedGroupMessage
        {
            GroupId = groupId,
            MessageId = messageId,
            Nonce = nonce,
            Ciphertext = ciphertext,
            KeyVersion = keyVersion
        };
    }
}
