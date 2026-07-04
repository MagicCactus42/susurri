using System.Security.Cryptography;
using NSec.Cryptography;
using Susurri.Shared.Abstractions.Security;

namespace Susurri.Modules.DHT.Core.Onion.GroupChat;

public sealed class EncryptedGroupMessageV2
{
    public Guid GroupId { get; init; }
    public Guid MessageId { get; init; }
    public byte[] SenderPublicKey { get; init; } = Array.Empty<byte>();
    public int Generation { get; init; }
    public uint Iteration { get; init; }
    public int KeyVersion { get; init; }
    public byte[] Nonce { get; init; } = Array.Empty<byte>();
    public byte[] Ciphertext { get; init; } = Array.Empty<byte>();

    public static EncryptedGroupMessageV2 Seal(
        GroupMessage message, byte[] messageKey, int generation, uint iteration, int keyVersion)
    {
        var nonce = GroupRatchet.GenerateNonce();
        var envelope = new EncryptedGroupMessageV2
        {
            GroupId = message.GroupId,
            MessageId = message.MessageId,
            SenderPublicKey = message.SenderPublicKey,
            Generation = generation,
            Iteration = iteration,
            KeyVersion = keyVersion,
            Nonce = nonce,
            Ciphertext = Array.Empty<byte>()
        };

        var ciphertext = GroupRatchet.Encrypt(messageKey, nonce, envelope.AssociatedData(), message.Serialize());

        return new EncryptedGroupMessageV2
        {
            GroupId = envelope.GroupId,
            MessageId = envelope.MessageId,
            SenderPublicKey = envelope.SenderPublicKey,
            Generation = generation,
            Iteration = iteration,
            KeyVersion = keyVersion,
            Nonce = nonce,
            Ciphertext = ciphertext
        };
    }

    public GroupMessage Open(byte[] messageKey)
    {
        var plaintext = GroupRatchet.Decrypt(messageKey, Nonce, AssociatedData(), Ciphertext);
        return GroupMessage.Deserialize(plaintext);
    }

    public byte[] AssociatedData()
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);

        writer.Write(GroupId.ToByteArray());
        writer.Write(MessageId.ToByteArray());
        writer.Write(SenderPublicKey);
        writer.Write(Generation);
        writer.Write(Iteration);
        writer.Write(KeyVersion);

        return ms.ToArray();
    }

    public byte[] Serialize()
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);

        writer.Write(GroupId.ToByteArray());
        writer.Write(MessageId.ToByteArray());
        writer.Write((byte)SenderPublicKey.Length);
        writer.Write(SenderPublicKey);
        writer.Write(Generation);
        writer.Write(Iteration);
        writer.Write(KeyVersion);
        writer.Write((byte)Nonce.Length);
        writer.Write(Nonce);
        writer.Write(Ciphertext.Length);
        writer.Write(Ciphertext);

        return ms.ToArray();
    }

    public static EncryptedGroupMessageV2 Deserialize(byte[] data)
    {
        using var ms = new MemoryStream(data);
        using var reader = new BinaryReader(ms);

        var groupId = new Guid(reader.ReadBytes(16));
        var messageId = new Guid(reader.ReadBytes(16));
        var pubKeyLen = reader.ReadByte();
        if (pubKeyLen != SecurityLimits.PublicKeySize)
            throw new InvalidDataException($"GroupMessageV2 sender key length {pubKeyLen} invalid");
        var senderPublicKey = reader.ReadBytes(pubKeyLen);
        var generation = reader.ReadInt32();
        var iteration = reader.ReadUInt32();
        var keyVersion = reader.ReadInt32();
        var nonceLen = reader.ReadByte();
        if (nonceLen != 12)
            throw new InvalidDataException($"GroupMessageV2 nonce length {nonceLen} invalid");
        var nonce = reader.ReadBytes(nonceLen);
        var ciphertextLen = reader.ReadInt32();
        if (ciphertextLen < 0 || ciphertextLen > SecurityLimits.MaxValueSize)
            throw new InvalidDataException($"GroupMessageV2 ciphertext length {ciphertextLen} out of range");
        var ciphertext = reader.ReadBytes(ciphertextLen);

        return new EncryptedGroupMessageV2
        {
            GroupId = groupId,
            MessageId = messageId,
            SenderPublicKey = senderPublicKey,
            Generation = generation,
            Iteration = iteration,
            KeyVersion = keyVersion,
            Nonce = nonce,
            Ciphertext = ciphertext
        };
    }
}

public sealed class GroupSenderKeyDistribution
{
    private static readonly AeadAlgorithm Aead = AeadAlgorithm.ChaCha20Poly1305;
    private static readonly KeyAgreementAlgorithm KeyExchange = KeyAgreementAlgorithm.X25519;
    private static readonly KeyDerivationAlgorithm KeyDerivation = KeyDerivationAlgorithm.HkdfSha256;

    public Guid GroupId { get; init; }
    public int Generation { get; init; }
    public uint Iteration { get; init; }
    public int KeyVersion { get; init; }
    public byte[] ChainKey { get; init; } = Array.Empty<byte>();
    public byte[] SenderPublicKey { get; init; } = Array.Empty<byte>();
    public byte[] SenderSigningPublicKey { get; init; } = Array.Empty<byte>();
    public long Timestamp { get; init; }
    public byte[] Signature { get; set; } = Array.Empty<byte>();

    public byte[] GetSignableData()
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);

        writer.Write(GroupId.ToByteArray());
        writer.Write(Generation);
        writer.Write(Iteration);
        writer.Write(KeyVersion);
        writer.Write(ChainKey);
        writer.Write(SenderPublicKey);
        writer.Write(SenderSigningPublicKey);
        writer.Write(Timestamp);

        return ms.ToArray();
    }

    public bool VerifySignature()
    {
        if (SenderSigningPublicKey.Length != SecurityLimits.PublicKeySize || Signature.Length == 0)
            return false;

        try
        {
            var signingKey = PublicKey.Import(
                SignatureAlgorithm.Ed25519, SenderSigningPublicKey, KeyBlobFormat.RawPublicKey);
            return SignatureAlgorithm.Ed25519.Verify(signingKey, GetSignableData(), Signature);
        }
        catch
        {
            return false;
        }
    }

    public byte[] Serialize()
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);

        writer.Write(GroupId.ToByteArray());
        writer.Write(Generation);
        writer.Write(Iteration);
        writer.Write(KeyVersion);
        writer.Write((byte)ChainKey.Length);
        writer.Write(ChainKey);
        writer.Write((byte)SenderPublicKey.Length);
        writer.Write(SenderPublicKey);
        writer.Write((byte)SenderSigningPublicKey.Length);
        writer.Write(SenderSigningPublicKey);
        writer.Write(Timestamp);
        writer.Write((ushort)Signature.Length);
        writer.Write(Signature);

        return ms.ToArray();
    }

    public static GroupSenderKeyDistribution Deserialize(byte[] data)
    {
        using var ms = new MemoryStream(data);
        using var reader = new BinaryReader(ms);

        var groupId = new Guid(reader.ReadBytes(16));
        var generation = reader.ReadInt32();
        var iteration = reader.ReadUInt32();
        var keyVersion = reader.ReadInt32();
        var chainKeyLen = reader.ReadByte();
        if (chainKeyLen != 32)
            throw new InvalidDataException($"Sender chain key length {chainKeyLen} invalid");
        var chainKey = reader.ReadBytes(chainKeyLen);
        var pubKeyLen = reader.ReadByte();
        if (pubKeyLen != SecurityLimits.PublicKeySize)
            throw new InvalidDataException($"Sender public key length {pubKeyLen} invalid");
        var senderPublicKey = reader.ReadBytes(pubKeyLen);
        var sigKeyLen = reader.ReadByte();
        if (sigKeyLen != SecurityLimits.PublicKeySize)
            throw new InvalidDataException($"Sender signing key length {sigKeyLen} invalid");
        var senderSigningPublicKey = reader.ReadBytes(sigKeyLen);
        var timestamp = reader.ReadInt64();
        var sigLen = reader.ReadUInt16();
        if (sigLen > 512)
            throw new InvalidDataException($"Signature length {sigLen} out of range");
        var signature = reader.ReadBytes(sigLen);

        return new GroupSenderKeyDistribution
        {
            GroupId = groupId,
            Generation = generation,
            Iteration = iteration,
            KeyVersion = keyVersion,
            ChainKey = chainKey,
            SenderPublicKey = senderPublicKey,
            SenderSigningPublicKey = senderSigningPublicKey,
            Timestamp = timestamp,
            Signature = signature
        };
    }

    public byte[] SealFor(byte[] memberPublicKey)
    {
        using var ephemeralKey = Key.Create(KeyExchange);
        var ephemeralPublic = ephemeralKey.PublicKey.Export(KeyBlobFormat.RawPublicKey);

        var memberKey = PublicKey.Import(KeyExchange, memberPublicKey, KeyBlobFormat.RawPublicKey);
        using var sharedSecret = KeyExchange.Agree(ephemeralKey, memberKey)
            ?? throw new CryptographicException("Key agreement failed");

        using var wrapKey = KeyDerivation.DeriveKey(
            sharedSecret,
            ReadOnlySpan<byte>.Empty,
            HkdfContexts.GroupSenderKeyWrap,
            Aead,
            new KeyCreationParameters { ExportPolicy = KeyExportPolicies.AllowPlaintextExport });

        var nonce = new byte[Aead.NonceSize];
        RandomNumberGenerator.Fill(nonce);
        var ciphertext = Aead.Encrypt(wrapKey, nonce, null, Serialize());

        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);
        writer.Write((byte)ephemeralPublic.Length);
        writer.Write(ephemeralPublic);
        writer.Write((byte)nonce.Length);
        writer.Write(nonce);
        writer.Write(ciphertext.Length);
        writer.Write(ciphertext);
        return ms.ToArray();
    }

    public static GroupSenderKeyDistribution OpenSealed(byte[] sealedBlob, Key identityKey)
    {
        using var ms = new MemoryStream(sealedBlob);
        using var reader = new BinaryReader(ms);

        var ephemeralLen = reader.ReadByte();
        if (ephemeralLen != SecurityLimits.PublicKeySize)
            throw new InvalidDataException($"Sealed distribution ephemeral key length {ephemeralLen} invalid");
        var ephemeralPublic = reader.ReadBytes(ephemeralLen);
        var nonceLen = reader.ReadByte();
        if (nonceLen != 12)
            throw new InvalidDataException($"Sealed distribution nonce length {nonceLen} invalid");
        var nonce = reader.ReadBytes(nonceLen);
        var ciphertextLen = reader.ReadInt32();
        if (ciphertextLen < 0 || ciphertextLen > SecurityLimits.MaxValueSize)
            throw new InvalidDataException($"Sealed distribution length {ciphertextLen} out of range");
        var ciphertext = reader.ReadBytes(ciphertextLen);

        var ephemeralKey = PublicKey.Import(KeyExchange, ephemeralPublic, KeyBlobFormat.RawPublicKey);
        using var sharedSecret = KeyExchange.Agree(identityKey, ephemeralKey)
            ?? throw new CryptographicException("Key agreement failed");

        using var wrapKey = KeyDerivation.DeriveKey(
            sharedSecret,
            ReadOnlySpan<byte>.Empty,
            HkdfContexts.GroupSenderKeyWrap,
            Aead,
            new KeyCreationParameters { ExportPolicy = KeyExportPolicies.AllowPlaintextExport });

        var plaintext = Aead.Decrypt(wrapKey, nonce, null, ciphertext)
            ?? throw new CryptographicException("Failed to unseal sender key distribution");

        return Deserialize(plaintext);
    }
}
