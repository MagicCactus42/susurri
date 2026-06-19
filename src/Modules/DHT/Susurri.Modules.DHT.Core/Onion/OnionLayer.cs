using Susurri.Shared.Abstractions.Security;

namespace Susurri.Modules.DHT.Core.Onion;

public enum OnionWireKind : byte
{
    Layer = 0x01,
    ReplyChain = 0x02
}

public sealed class OnionLayer
{
    private const int PublicKeySize = 32;
    private const int MinNonceSize = 12;
    private const int MaxNonceSize = 24;

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
        if (pubKeyLen != PublicKeySize)
            throw new InvalidDataException($"Invalid ephemeral public key length: {pubKeyLen}");
        var ephemeralPublicKey = ReadExactly(reader, pubKeyLen);

        var nonceLen = reader.ReadByte();
        if (nonceLen < MinNonceSize || nonceLen > MaxNonceSize)
            throw new InvalidDataException($"Invalid nonce length: {nonceLen}");
        var nonce = ReadExactly(reader, nonceLen);

        var ciphertextLen = reader.ReadInt32();
        if (ciphertextLen <= 0 || ciphertextLen > SecurityLimits.MaxMessageSize)
            throw new InvalidDataException($"Invalid ciphertext length: {ciphertextLen}");
        var ciphertext = ReadExactly(reader, ciphertextLen);

        return new OnionLayer
        {
            EphemeralPublicKey = ephemeralPublicKey,
            Nonce = nonce,
            Ciphertext = ciphertext
        };
    }

    internal static byte[] ReadExactly(BinaryReader reader, int length)
    {
        var bytes = reader.ReadBytes(length);
        if (bytes.Length != length)
            throw new InvalidDataException($"Truncated data: expected {length} bytes, got {bytes.Length}");
        return bytes;
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

        var typeByte = reader.ReadByte();
        if (!Enum.IsDefined(typeof(OnionLayerType), typeByte))
            throw new InvalidDataException($"Unknown onion layer type: {typeByte}");
        var type = (OnionLayerType)typeByte;

        string? nextHopAddress = null;
        int nextHopPort = 0;
        byte[] recipientPublicKey = Array.Empty<byte>();

        if (type == OnionLayerType.Relay)
        {
            nextHopAddress = SafeBinaryReader.ReadStringWithLimit(reader, SecurityLimits.MaxIpAddressLength);
            nextHopPort = reader.ReadUInt16();
        }

        if (type == OnionLayerType.FinalHop)
        {
            var pubKeyLen = reader.ReadByte();
            if (pubKeyLen > SecurityLimits.PublicKeySize)
                throw new InvalidDataException($"Recipient public key too large: {pubKeyLen}");
            recipientPublicKey = OnionLayer.ReadExactly(reader, pubKeyLen);
        }

        var replyTokenLen = reader.ReadInt32();
        if (replyTokenLen < 0 || replyTokenLen > SecurityLimits.MaxValueSize)
            throw new InvalidDataException($"Invalid reply token length: {replyTokenLen}");
        var replyToken = OnionLayer.ReadExactly(reader, replyTokenLen);

        var innerPayloadLen = reader.ReadInt32();
        if (innerPayloadLen < 0 || innerPayloadLen > SecurityLimits.MaxMessageSize)
            throw new InvalidDataException($"Invalid inner payload length: {innerPayloadLen}");
        var innerPayload = OnionLayer.ReadExactly(reader, innerPayloadLen);

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
