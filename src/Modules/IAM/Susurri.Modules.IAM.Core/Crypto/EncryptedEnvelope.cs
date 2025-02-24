namespace Susurri.Modules.IAM.Core.Crypto;

public sealed class EncryptedEnvelope
{
    public required byte[] EphemeralPublicKey { get; init; }
    public required byte[] Nonce { get; init; }
    public required byte[] Ciphertext { get; init; }

    // Format: [1 byte: pubkey length][pubkey][1 byte: nonce length][nonce][ciphertext]
    public byte[] ToBytes()
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);

        writer.Write((byte)EphemeralPublicKey.Length);
        writer.Write(EphemeralPublicKey);
        writer.Write((byte)Nonce.Length);
        writer.Write(Nonce);
        writer.Write(Ciphertext);

        return ms.ToArray();
    }

    public static EncryptedEnvelope FromBytes(byte[] data)
    {
        using var ms = new MemoryStream(data);
        using var reader = new BinaryReader(ms);

        var pubKeyLength = reader.ReadByte();
        var ephemeralPublicKey = reader.ReadBytes(pubKeyLength);

        var nonceLength = reader.ReadByte();
        var nonce = reader.ReadBytes(nonceLength);

        var remaining = (int)(ms.Length - ms.Position);
        var ciphertext = reader.ReadBytes(remaining);

        return new EncryptedEnvelope
        {
            EphemeralPublicKey = ephemeralPublicKey,
            Nonce = nonce,
            Ciphertext = ciphertext
        };
    }
}
