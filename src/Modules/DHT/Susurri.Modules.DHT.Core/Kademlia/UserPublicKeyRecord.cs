using NSec.Cryptography;

namespace Susurri.Modules.DHT.Core.Kademlia;

public sealed record UserPublicKeyRecord
{
    public byte[] EncryptionPublicKey { get; init; } = Array.Empty<byte>();
    public byte[] SigningPublicKey { get; init; } = Array.Empty<byte>();
    public long Timestamp { get; init; }
    public byte[]? Signature { get; init; }

    public byte[] GetSignableData()
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);

        writer.Write((byte)EncryptionPublicKey.Length);
        writer.Write(EncryptionPublicKey);
        writer.Write((byte)SigningPublicKey.Length);
        writer.Write(SigningPublicKey);
        writer.Write(Timestamp);

        return ms.ToArray();
    }

    public bool VerifySignature()
    {
        if (SigningPublicKey.Length == 0 || Signature == null || Signature.Length == 0)
            return false;

        try
        {
            var signingPubKey = PublicKey.Import(
                SignatureAlgorithm.Ed25519,
                SigningPublicKey,
                KeyBlobFormat.RawPublicKey);

            return SignatureAlgorithm.Ed25519.Verify(signingPubKey, GetSignableData(), Signature);
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

        writer.Write((byte)EncryptionPublicKey.Length);
        writer.Write(EncryptionPublicKey);
        writer.Write((byte)SigningPublicKey.Length);
        writer.Write(SigningPublicKey);
        writer.Write(Timestamp);

        if (Signature != null)
        {
            writer.Write(true);
            writer.Write((byte)Signature.Length);
            writer.Write(Signature);
        }
        else
        {
            writer.Write(false);
        }

        return ms.ToArray();
    }

    public static UserPublicKeyRecord Deserialize(byte[] data)
    {
        using var ms = new MemoryStream(data);
        using var reader = new BinaryReader(ms);

        var encKeyLen = reader.ReadByte();
        var encryptionPublicKey = reader.ReadBytes(encKeyLen);

        var signKeyLen = reader.ReadByte();
        var signingPublicKey = reader.ReadBytes(signKeyLen);

        var timestamp = reader.ReadInt64();

        byte[]? signature = null;
        if (reader.ReadBoolean())
        {
            var sigLen = reader.ReadByte();
            signature = reader.ReadBytes(sigLen);
        }

        return new UserPublicKeyRecord
        {
            EncryptionPublicKey = encryptionPublicKey,
            SigningPublicKey = signingPublicKey,
            Timestamp = timestamp,
            Signature = signature
        };
    }
}
