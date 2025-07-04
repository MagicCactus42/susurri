namespace Susurri.Modules.DHT.Core.Kademlia.Protocol;

public abstract class KademliaMessage
{
    private const int MaxValueSize = 32 * 1024;
    private const int MaxStringLength = 1024;
    private const int MaxNodesPerResponse = 20;
    private const int PublicKeySize = 32;

    public Guid MessageId { get; init; } = Guid.NewGuid();
    public KademliaId SenderId { get; init; }
    public byte[] SenderPublicKey { get; init; } = Array.Empty<byte>();
    public abstract MessageType Type { get; }

    public byte[] Serialize()
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);

        writer.Write((byte)Type);
        writer.Write(MessageId.ToByteArray());
        writer.Write(SenderId.Bytes);
        writer.Write((byte)SenderPublicKey.Length);
        writer.Write(SenderPublicKey);
        SerializePayload(writer);

        return ms.ToArray();
    }

    protected abstract void SerializePayload(BinaryWriter writer);

    public static KademliaMessage Deserialize(byte[] data)
    {
        if (data == null || data.Length < 50)
            throw new InvalidDataException("Message data too short");

        using var ms = new MemoryStream(data);
        using var reader = new BinaryReader(ms);

        var type = (MessageType)reader.ReadByte();
        var messageId = new Guid(reader.ReadBytes(16));
        var senderId = KademliaId.FromBytes(reader.ReadBytes(32));
        var pubKeyLen = reader.ReadByte();

        if (pubKeyLen > PublicKeySize)
            throw new InvalidDataException($"Public key too large: {pubKeyLen}");

        var senderPublicKey = reader.ReadBytes(pubKeyLen);

        return type switch
        {
            MessageType.Ping => PingMessage.DeserializePayload(reader, messageId, senderId, senderPublicKey),
            MessageType.Pong => PongMessage.DeserializePayload(reader, messageId, senderId, senderPublicKey),
            MessageType.FindNode => FindNodeMessage.DeserializePayload(reader, messageId, senderId, senderPublicKey),
            MessageType.FindNodeResponse => FindNodeResponseMessage.DeserializePayload(reader, messageId, senderId, senderPublicKey),
            MessageType.FindValue => FindValueMessage.DeserializePayload(reader, messageId, senderId, senderPublicKey),
            MessageType.FindValueResponse => FindValueResponseMessage.DeserializePayload(reader, messageId, senderId, senderPublicKey),
            MessageType.Store => StoreMessage.DeserializePayload(reader, messageId, senderId, senderPublicKey),
            MessageType.StoreResponse => StoreResponseMessage.DeserializePayload(reader, messageId, senderId, senderPublicKey),
            MessageType.OnionMessage => OnionMessageWrapper.DeserializePayload(reader, messageId, senderId, senderPublicKey),
            _ => throw new InvalidDataException($"Unknown message type: {type}")
        };
    }

    protected static byte[] ReadBytesWithLimit(BinaryReader reader, int maxLength)
    {
        var length = reader.ReadInt32();
        if (length < 0 || length > maxLength)
            throw new InvalidDataException($"Invalid data length: {length}");
        return reader.ReadBytes(length);
    }

    protected static string ReadStringWithLimit(BinaryReader reader, int maxLength)
    {
        var str = reader.ReadString();
        if (str.Length > maxLength)
            throw new InvalidDataException($"String too long: {str.Length}");
        return str;
    }
}

public sealed class PingMessage : KademliaMessage
{
    public override MessageType Type => MessageType.Ping;
    protected override void SerializePayload(BinaryWriter writer) { }

    public static PingMessage DeserializePayload(BinaryReader reader, Guid messageId, KademliaId senderId, byte[] senderPublicKey)
        => new() { MessageId = messageId, SenderId = senderId, SenderPublicKey = senderPublicKey };
}

public sealed class PongMessage : KademliaMessage
{
    public override MessageType Type => MessageType.Pong;
    public Guid InResponseTo { get; init; }

    protected override void SerializePayload(BinaryWriter writer)
        => writer.Write(InResponseTo.ToByteArray());

    public static PongMessage DeserializePayload(BinaryReader reader, Guid messageId, KademliaId senderId, byte[] senderPublicKey)
    {
        var inResponseTo = new Guid(reader.ReadBytes(16));
        return new PongMessage { MessageId = messageId, SenderId = senderId, SenderPublicKey = senderPublicKey, InResponseTo = inResponseTo };
    }
}

public sealed class FindNodeMessage : KademliaMessage
{
    public override MessageType Type => MessageType.FindNode;
    public KademliaId TargetId { get; init; }

    protected override void SerializePayload(BinaryWriter writer)
        => writer.Write(TargetId.Bytes);

    public static FindNodeMessage DeserializePayload(BinaryReader reader, Guid messageId, KademliaId senderId, byte[] senderPublicKey)
    {
        var targetId = KademliaId.FromBytes(reader.ReadBytes(32));
        return new FindNodeMessage { MessageId = messageId, SenderId = senderId, SenderPublicKey = senderPublicKey, TargetId = targetId };
    }
}

public sealed class FindNodeResponseMessage : KademliaMessage
{
    private const int MaxNodes = 20;
    public override MessageType Type => MessageType.FindNodeResponse;
    public Guid InResponseTo { get; init; }
    public List<NodeRecord> Nodes { get; init; } = new();

    protected override void SerializePayload(BinaryWriter writer)
    {
        writer.Write(InResponseTo.ToByteArray());
        var count = Math.Min(Nodes.Count, MaxNodes);
        writer.Write((byte)count);
        for (int i = 0; i < count; i++)
            Nodes[i].Serialize(writer);
    }

    public static FindNodeResponseMessage DeserializePayload(BinaryReader reader, Guid messageId, KademliaId senderId, byte[] senderPublicKey)
    {
        var inResponseTo = new Guid(reader.ReadBytes(16));
        var nodeCount = reader.ReadByte();
        if (nodeCount > MaxNodes)
            throw new InvalidDataException($"Too many nodes: {nodeCount}");

        var nodes = new List<NodeRecord>(nodeCount);
        for (int i = 0; i < nodeCount; i++)
            nodes.Add(NodeRecord.Deserialize(reader));

        return new FindNodeResponseMessage { MessageId = messageId, SenderId = senderId, SenderPublicKey = senderPublicKey, InResponseTo = inResponseTo, Nodes = nodes };
    }
}

public sealed class FindValueMessage : KademliaMessage
{
    public override MessageType Type => MessageType.FindValue;
    public KademliaId Key { get; init; }

    protected override void SerializePayload(BinaryWriter writer)
        => writer.Write(Key.Bytes);

    public static FindValueMessage DeserializePayload(BinaryReader reader, Guid messageId, KademliaId senderId, byte[] senderPublicKey)
    {
        var key = KademliaId.FromBytes(reader.ReadBytes(32));
        return new FindValueMessage { MessageId = messageId, SenderId = senderId, SenderPublicKey = senderPublicKey, Key = key };
    }
}

public sealed class FindValueResponseMessage : KademliaMessage
{
    private const int MaxValueSize = 32 * 1024;
    private const int MaxNodes = 20;

    public override MessageType Type => MessageType.FindValueResponse;
    public Guid InResponseTo { get; init; }
    public bool Found { get; init; }
    public byte[]? Value { get; init; }
    public List<NodeRecord> ClosestNodes { get; init; } = new();

    protected override void SerializePayload(BinaryWriter writer)
    {
        writer.Write(InResponseTo.ToByteArray());
        writer.Write(Found);
        if (Found && Value != null)
        {
            writer.Write(Value.Length);
            writer.Write(Value);
        }
        else
        {
            var count = Math.Min(ClosestNodes.Count, MaxNodes);
            writer.Write((byte)count);
            for (int i = 0; i < count; i++)
                ClosestNodes[i].Serialize(writer);
        }
    }

    public static FindValueResponseMessage DeserializePayload(BinaryReader reader, Guid messageId, KademliaId senderId, byte[] senderPublicKey)
    {
        var inResponseTo = new Guid(reader.ReadBytes(16));
        var found = reader.ReadBoolean();

        if (found)
        {
            var valueLen = reader.ReadInt32();
            if (valueLen < 0 || valueLen > MaxValueSize)
                throw new InvalidDataException($"Invalid value length: {valueLen}");

            var value = reader.ReadBytes(valueLen);
            return new FindValueResponseMessage { MessageId = messageId, SenderId = senderId, SenderPublicKey = senderPublicKey, InResponseTo = inResponseTo, Found = true, Value = value };
        }

        var nodeCount = reader.ReadByte();
        if (nodeCount > MaxNodes)
            throw new InvalidDataException($"Too many nodes: {nodeCount}");

        var nodes = new List<NodeRecord>(nodeCount);
        for (int i = 0; i < nodeCount; i++)
            nodes.Add(NodeRecord.Deserialize(reader));

        return new FindValueResponseMessage { MessageId = messageId, SenderId = senderId, SenderPublicKey = senderPublicKey, InResponseTo = inResponseTo, Found = false, ClosestNodes = nodes };
    }
}

public sealed class StoreMessage : KademliaMessage
{
    private const int MaxValueSize = 32 * 1024;

    public override MessageType Type => MessageType.Store;
    public KademliaId Key { get; init; }
    public byte[] Value { get; init; } = Array.Empty<byte>();
    public uint TtlSeconds { get; init; }

    protected override void SerializePayload(BinaryWriter writer)
    {
        writer.Write(Key.Bytes);
        writer.Write(Value.Length);
        writer.Write(Value);
        writer.Write(TtlSeconds);
    }

    public static StoreMessage DeserializePayload(BinaryReader reader, Guid messageId, KademliaId senderId, byte[] senderPublicKey)
    {
        var key = KademliaId.FromBytes(reader.ReadBytes(32));
        var valueLen = reader.ReadInt32();
        if (valueLen < 0 || valueLen > MaxValueSize)
            throw new InvalidDataException($"Invalid value length: {valueLen}");

        var value = reader.ReadBytes(valueLen);
        var ttl = reader.ReadUInt32();

        return new StoreMessage { MessageId = messageId, SenderId = senderId, SenderPublicKey = senderPublicKey, Key = key, Value = value, TtlSeconds = ttl };
    }
}

public sealed class StoreResponseMessage : KademliaMessage
{
    private const int MaxErrorLength = 256;

    public override MessageType Type => MessageType.StoreResponse;
    public Guid InResponseTo { get; init; }
    public bool Success { get; init; }
    public string? Error { get; init; }

    protected override void SerializePayload(BinaryWriter writer)
    {
        writer.Write(InResponseTo.ToByteArray());
        writer.Write(Success);
        var errorMsg = Error ?? string.Empty;
        if (errorMsg.Length > MaxErrorLength)
            errorMsg = errorMsg[..MaxErrorLength];
        writer.Write(errorMsg);
    }

    public static StoreResponseMessage DeserializePayload(BinaryReader reader, Guid messageId, KademliaId senderId, byte[] senderPublicKey)
    {
        var inResponseTo = new Guid(reader.ReadBytes(16));
        var success = reader.ReadBoolean();
        var error = reader.ReadString();
        if (error.Length > MaxErrorLength)
            error = error[..MaxErrorLength];

        return new StoreResponseMessage { MessageId = messageId, SenderId = senderId, SenderPublicKey = senderPublicKey, InResponseTo = inResponseTo, Success = success, Error = string.IsNullOrEmpty(error) ? null : error };
    }
}

public sealed class OnionMessageWrapper : KademliaMessage
{
    private const int MaxPayloadSize = 64 * 1024;

    public override MessageType Type => MessageType.OnionMessage;
    public byte[] EncryptedPayload { get; init; } = Array.Empty<byte>();

    protected override void SerializePayload(BinaryWriter writer)
    {
        writer.Write(EncryptedPayload.Length);
        writer.Write(EncryptedPayload);
    }

    public static OnionMessageWrapper DeserializePayload(BinaryReader reader, Guid messageId, KademliaId senderId, byte[] senderPublicKey)
    {
        var len = reader.ReadInt32();
        if (len < 0 || len > MaxPayloadSize)
            throw new InvalidDataException($"Invalid payload length: {len}");

        var payload = reader.ReadBytes(len);
        return new OnionMessageWrapper { MessageId = messageId, SenderId = senderId, SenderPublicKey = senderPublicKey, EncryptedPayload = payload };
    }
}

public sealed class NodeRecord
{
    private const int MaxPublicKeySize = 32;
    private const int MaxIpLength = 16;

    public KademliaId Id { get; init; }
    public byte[] PublicKey { get; init; } = Array.Empty<byte>();
    public string IpAddress { get; init; } = string.Empty;
    public int Port { get; init; }

    public void Serialize(BinaryWriter writer)
    {
        writer.Write(Id.Bytes);
        writer.Write((byte)Math.Min(PublicKey.Length, MaxPublicKeySize));
        writer.Write(PublicKey.Length <= MaxPublicKeySize ? PublicKey : PublicKey[..MaxPublicKeySize]);

        var ipBytes = System.Net.IPAddress.Parse(IpAddress).GetAddressBytes();
        writer.Write((byte)ipBytes.Length);
        writer.Write(ipBytes);
        writer.Write((ushort)Port);
    }

    public static NodeRecord Deserialize(BinaryReader reader)
    {
        var id = KademliaId.FromBytes(reader.ReadBytes(32));
        var pubKeyLen = reader.ReadByte();
        if (pubKeyLen > MaxPublicKeySize)
            throw new InvalidDataException($"Public key too large: {pubKeyLen}");

        var publicKey = reader.ReadBytes(pubKeyLen);
        var ipLen = reader.ReadByte();
        if (ipLen > MaxIpLength)
            throw new InvalidDataException($"IP address too large: {ipLen}");

        var ipBytes = reader.ReadBytes(ipLen);
        var port = reader.ReadUInt16();

        return new NodeRecord
        {
            Id = id,
            PublicKey = publicKey,
            IpAddress = new System.Net.IPAddress(ipBytes).ToString(),
            Port = port
        };
    }

    public static NodeRecord FromNode(KademliaNode node)
        => new()
        {
            Id = node.Id,
            PublicKey = node.EncryptionPublicKey,
            IpAddress = node.EndPoint.Address.ToString(),
            Port = node.EndPoint.Port
        };

    public KademliaNode ToNode()
    {
        var endPoint = new System.Net.IPEndPoint(System.Net.IPAddress.Parse(IpAddress), Port);
        return new KademliaNode(Id, PublicKey, endPoint);
    }
}
