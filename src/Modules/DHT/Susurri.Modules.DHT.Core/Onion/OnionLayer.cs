namespace Susurri.Modules.DHT.Core.Onion;

public sealed class OnionLayer
{
    public byte[] EphemeralPublicKey { get; init; } = Array.Empty<byte>();
    public byte[] Nonce { get; init; } = Array.Empty<byte>();
    public byte[] Ciphertext { get; init; } = Array.Empty<byte>();

    public byte[] Serialize()
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);

        writer.Write((byte)EphemeralPublicKey.Length);
        writer.Write(EphemeralPublicKey);
        writer.Write((byte)Nonce.Length);
        writer.Write(Nonce);
        writer.Write(Ciphertext.Length);
        writer.Write(Ciphertext);

        return ms.ToArray();
    }

    public static OnionLayer Deserialize(byte[] data)
    {
        using var ms = new MemoryStream(data);
        using var reader = new BinaryReader(ms);

        var pubKeyLen = reader.ReadByte();
        var ephemeralPublicKey = reader.ReadBytes(pubKeyLen);

        var nonceLen = reader.ReadByte();
        var nonce = reader.ReadBytes(nonceLen);

        var ciphertextLen = reader.ReadInt32();
        var ciphertext = reader.ReadBytes(ciphertextLen);

        return new OnionLayer
        {
            EphemeralPublicKey = ephemeralPublicKey,
            Nonce = nonce,
            Ciphertext = ciphertext
        };
    }
}

public sealed class OnionLayerContent
{
    public OnionLayerType Type { get; init; }
    public string? NextHopAddress { get; init; }
    public int NextHopPort { get; init; }
    public byte[] ReplyToken { get; init; } = Array.Empty<byte>();
    public byte[] InnerPayload { get; init; } = Array.Empty<byte>();
    public byte[] RecipientPublicKey { get; init; } = Array.Empty<byte>();

    public byte[] Serialize()
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);

        writer.Write((byte)Type);

        if (Type == OnionLayerType.Relay)
        {
            writer.Write(NextHopAddress ?? string.Empty);
            writer.Write((ushort)NextHopPort);
        }

        if (Type == OnionLayerType.FinalHop)
        {
            writer.Write((byte)RecipientPublicKey.Length);
            writer.Write(RecipientPublicKey);
        }

        writer.Write(ReplyToken.Length);
        writer.Write(ReplyToken);
        writer.Write(InnerPayload.Length);
        writer.Write(InnerPayload);

        return ms.ToArray();
    }

    public static OnionLayerContent Deserialize(byte[] data)
    {
        using var ms = new MemoryStream(data);
        using var reader = new BinaryReader(ms);

        var type = (OnionLayerType)reader.ReadByte();

        string? nextHopAddress = null;
        int nextHopPort = 0;
        byte[] recipientPublicKey = Array.Empty<byte>();

        if (type == OnionLayerType.Relay)
        {
            nextHopAddress = reader.ReadString();
            nextHopPort = reader.ReadUInt16();
        }

        if (type == OnionLayerType.FinalHop)
        {
            var pubKeyLen = reader.ReadByte();
            recipientPublicKey = reader.ReadBytes(pubKeyLen);
        }

        var replyTokenLen = reader.ReadInt32();
        var replyToken = reader.ReadBytes(replyTokenLen);

        var innerPayloadLen = reader.ReadInt32();
        var innerPayload = reader.ReadBytes(innerPayloadLen);

        return new OnionLayerContent
        {
            Type = type,
            NextHopAddress = nextHopAddress,
            NextHopPort = nextHopPort,
            RecipientPublicKey = recipientPublicKey,
            ReplyToken = replyToken,
            InnerPayload = innerPayload
        };
    }
}

public enum OnionLayerType : byte
{
    Relay = 0x01,
    FinalHop = 0x02,
    Delivery = 0x03,
    Ack = 0x04,
    GroupMessage = 0x05
}
