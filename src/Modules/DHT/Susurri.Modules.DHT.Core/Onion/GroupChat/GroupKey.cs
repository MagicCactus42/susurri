using System.Security.Cryptography;
using NSec.Cryptography;

namespace Susurri.Modules.DHT.Core.Onion.GroupChat;

public sealed class GroupKey
{
    private static readonly AeadAlgorithm Aead = AeadAlgorithm.ChaCha20Poly1305;
    private static readonly KeyAgreementAlgorithm KeyExchange = KeyAgreementAlgorithm.X25519;
    private static readonly KeyDerivationAlgorithm KeyDerivation = KeyDerivationAlgorithm.HkdfSha256;

    public Guid GroupId { get; init; }
    public byte[] SymmetricKey { get; init; } = Array.Empty<byte>();
    public long CreatedAt { get; init; }
    public long? RotatedAt { get; private set; }
    public int Version { get; private set; } = 1;

    public static GroupKey Create(Guid? groupId = null)
    {
        var key = new byte[32];
        RandomNumberGenerator.Fill(key);

        return new GroupKey
        {
            GroupId = groupId ?? Guid.NewGuid(),
            SymmetricKey = key,
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        };
    }

    public GroupKey Rotate()
    {
        var newKey = new byte[32];
        RandomNumberGenerator.Fill(newKey);

        return new GroupKey
        {
            GroupId = GroupId,
            SymmetricKey = newKey,
            CreatedAt = CreatedAt,
            RotatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Version = Version + 1
        };
    }

    public byte[] Encrypt(byte[] plaintext, byte[] nonce)
    {
        using var key = Key.Import(Aead, SymmetricKey, KeyBlobFormat.RawSymmetricKey);
        return Aead.Encrypt(key, nonce, null, plaintext);
    }

    public byte[] Decrypt(byte[] ciphertext, byte[] nonce)
    {
        using var key = Key.Import(Aead, SymmetricKey, KeyBlobFormat.RawSymmetricKey);
        var plaintext = Aead.Decrypt(key, nonce, null, ciphertext);

        if (plaintext == null)
            throw new CryptographicException("Decryption failed - authentication tag invalid");

        return plaintext;
    }

    public byte[] GenerateNonce()
    {
        var nonce = new byte[Aead.NonceSize];
        RandomNumberGenerator.Fill(nonce);
        return nonce;
    }

    public WrappedGroupKey WrapForMember(byte[] memberPublicKey)
    {
        using var ephemeralKey = Key.Create(KeyExchange);
        var ephemeralPubKeyBytes = ephemeralKey.PublicKey.Export(KeyBlobFormat.RawPublicKey);

        var memberPubKey = PublicKey.Import(KeyExchange, memberPublicKey, KeyBlobFormat.RawPublicKey);

        using var sharedSecret = KeyExchange.Agree(ephemeralKey, memberPubKey);
        if (sharedSecret == null)
            throw new CryptographicException("Key agreement failed");

        using var wrapKey = KeyDerivation.DeriveKey(
            sharedSecret,
            ReadOnlySpan<byte>.Empty,
            ReadOnlySpan<byte>.Empty,
            Aead,
            new KeyCreationParameters { ExportPolicy = KeyExportPolicies.AllowPlaintextExport });

        var nonce = new byte[Aead.NonceSize];
        RandomNumberGenerator.Fill(nonce);

        var ciphertext = Aead.Encrypt(wrapKey, nonce, null, SymmetricKey);

        return new WrappedGroupKey
        {
            GroupId = GroupId,
            EphemeralPublicKey = ephemeralPubKeyBytes,
            Nonce = nonce,
            EncryptedKey = ciphertext,
            Version = Version
        };
    }

    public static GroupKey UnwrapWithPrivateKey(WrappedGroupKey wrapped, Key privateKey)
    {
        var ephemeralPubKey = PublicKey.Import(KeyExchange, wrapped.EphemeralPublicKey, KeyBlobFormat.RawPublicKey);

        using var sharedSecret = KeyExchange.Agree(privateKey, ephemeralPubKey);
        if (sharedSecret == null)
            throw new CryptographicException("Key agreement failed");

        using var wrapKey = KeyDerivation.DeriveKey(
            sharedSecret,
            ReadOnlySpan<byte>.Empty,
            ReadOnlySpan<byte>.Empty,
            Aead,
            new KeyCreationParameters { ExportPolicy = KeyExportPolicies.AllowPlaintextExport });

        var symmetricKey = Aead.Decrypt(wrapKey, wrapped.Nonce, null, wrapped.EncryptedKey);
        if (symmetricKey == null)
            throw new CryptographicException("Failed to unwrap group key");

        return new GroupKey
        {
            GroupId = wrapped.GroupId,
            SymmetricKey = symmetricKey,
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Version = wrapped.Version
        };
    }

    public byte[] Serialize()
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);

        writer.Write(GroupId.ToByteArray());
        writer.Write(SymmetricKey.Length);
        writer.Write(SymmetricKey);
        writer.Write(CreatedAt);
        writer.Write(RotatedAt ?? 0);
        writer.Write(Version);

        return ms.ToArray();
    }

    public static GroupKey Deserialize(byte[] data)
    {
        using var ms = new MemoryStream(data);
        using var reader = new BinaryReader(ms);

        var groupId = new Guid(reader.ReadBytes(16));
        var keyLen = reader.ReadInt32();
        var symmetricKey = reader.ReadBytes(keyLen);
        var createdAt = reader.ReadInt64();
        var rotatedAt = reader.ReadInt64();
        var version = reader.ReadInt32();

        return new GroupKey
        {
            GroupId = groupId,
            SymmetricKey = symmetricKey,
            CreatedAt = createdAt,
            RotatedAt = rotatedAt == 0 ? null : rotatedAt,
            Version = version
        };
    }
}

public sealed class WrappedGroupKey
{
    public Guid GroupId { get; init; }
    public byte[] EphemeralPublicKey { get; init; } = Array.Empty<byte>();
    public byte[] Nonce { get; init; } = Array.Empty<byte>();
    public byte[] EncryptedKey { get; init; } = Array.Empty<byte>();
    public int Version { get; init; }

    public byte[] Serialize()
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);

        writer.Write(GroupId.ToByteArray());
        writer.Write((byte)EphemeralPublicKey.Length);
        writer.Write(EphemeralPublicKey);
        writer.Write((byte)Nonce.Length);
        writer.Write(Nonce);
        writer.Write(EncryptedKey.Length);
        writer.Write(EncryptedKey);
        writer.Write(Version);

        return ms.ToArray();
    }

    public static WrappedGroupKey Deserialize(byte[] data)
    {
        using var ms = new MemoryStream(data);
        using var reader = new BinaryReader(ms);

        var groupId = new Guid(reader.ReadBytes(16));
        var pubKeyLen = reader.ReadByte();
        var ephemeralPublicKey = reader.ReadBytes(pubKeyLen);
        var nonceLen = reader.ReadByte();
        var nonce = reader.ReadBytes(nonceLen);
        var encKeyLen = reader.ReadInt32();
        var encryptedKey = reader.ReadBytes(encKeyLen);
        var version = reader.ReadInt32();

        return new WrappedGroupKey
        {
            GroupId = groupId,
            EphemeralPublicKey = ephemeralPublicKey,
            Nonce = nonce,
            EncryptedKey = encryptedKey,
            Version = version
        };
    }
}
