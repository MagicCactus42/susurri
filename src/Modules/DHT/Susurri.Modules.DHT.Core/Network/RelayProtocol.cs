using System.Net;
using Susurri.Modules.DHT.Core.Kademlia;

namespace Susurri.Modules.DHT.Core.Network;

/// <summary>
/// Relay protocol message types.
/// </summary>
public enum RelayMessageType : byte
{
    /// <summary>
    /// Request to establish a relay circuit.
    /// </summary>
    CircuitRequest = 0x20,

    /// <summary>
    /// Response to circuit request.
    /// </summary>
    CircuitResponse = 0x21,

    /// <summary>
    /// Data to relay through an established circuit.
    /// </summary>
    RelayData = 0x22,

    /// <summary>
    /// Close a relay circuit.
    /// </summary>
    CircuitClose = 0x23,

    /// <summary>
    /// Keepalive for circuit.
    /// </summary>
    CircuitKeepalive = 0x24,

    /// <summary>
    /// Request to relay a message to a target (stateless relay).
    /// </summary>
    RelayRequest = 0x25,

    /// <summary>
    /// Response from relayed message.
    /// </summary>
    RelayResponse = 0x26
}

/// <summary>
/// Base class for relay protocol messages.
/// </summary>
public abstract class RelayMessage
{
    protected const int MaxPayloadSize = 256 * 1024; // 256 KB
    protected const int MaxStringLength = 1024;

    public Guid MessageId { get; init; } = Guid.NewGuid();
    public abstract RelayMessageType Type { get; }

    protected static void ValidateLength(int length, string fieldName)
    {
        if (length < 0 || length > MaxPayloadSize)
            throw new InvalidDataException($"Invalid {fieldName} length: {length}");
    }

    protected static string ReadBoundedString(BinaryReader reader)
    {
        var s = reader.ReadString();
        if (s.Length > MaxStringLength)
            throw new InvalidDataException($"String too long: {s.Length}");
        return s;
    }

    public byte[] Serialize()
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);

        writer.Write((byte)Type);
        writer.Write(MessageId.ToByteArray());
        SerializePayload(writer);

        return ms.ToArray();
    }

    protected abstract void SerializePayload(BinaryWriter writer);

    public static RelayMessage Deserialize(byte[] data)
    {
        using var ms = new MemoryStream(data);
        using var reader = new BinaryReader(ms);

        var type = (RelayMessageType)reader.ReadByte();
        var messageId = new Guid(reader.ReadBytes(16));

        return type switch
        {
            RelayMessageType.CircuitRequest => CircuitRequestMessage.DeserializePayload(reader, messageId),
            RelayMessageType.CircuitResponse => CircuitResponseMessage.DeserializePayload(reader, messageId),
            RelayMessageType.RelayData => RelayDataMessage.DeserializePayload(reader, messageId),
            RelayMessageType.CircuitClose => CircuitCloseMessage.DeserializePayload(reader, messageId),
            RelayMessageType.RelayRequest => RelayRequestMessage.DeserializePayload(reader, messageId),
            RelayMessageType.RelayResponse => RelayResponseMessage.DeserializePayload(reader, messageId),
            _ => throw new InvalidOperationException($"Unknown relay message type: {type}")
        };
    }
}

/// <summary>
/// Request to establish a relay circuit through this node.
/// </summary>
public sealed class CircuitRequestMessage : RelayMessage
{
    public override RelayMessageType Type => RelayMessageType.CircuitRequest;

    /// <summary>
    /// The circuit ID chosen by the requester.
    /// </summary>
    public Guid CircuitId { get; init; }

    /// <summary>
    /// The target node to connect to.
    /// </summary>
    public KademliaId TargetNodeId { get; init; }

    /// <summary>
    /// Requested bandwidth limit in bytes/sec (0 = no limit).
    /// </summary>
    public uint RequestedBandwidth { get; init; }

    protected override void SerializePayload(BinaryWriter writer)
    {
        writer.Write(CircuitId.ToByteArray());
        writer.Write(TargetNodeId.Bytes);
        writer.Write(RequestedBandwidth);
    }

    public static CircuitRequestMessage DeserializePayload(BinaryReader reader, Guid messageId)
    {
        var circuitId = new Guid(reader.ReadBytes(16));
        var targetNodeId = KademliaId.FromBytes(reader.ReadBytes(32));
        var bandwidth = reader.ReadUInt32();

        return new CircuitRequestMessage
        {
            MessageId = messageId,
            CircuitId = circuitId,
            TargetNodeId = targetNodeId,
            RequestedBandwidth = bandwidth
        };
    }
}

/// <summary>
/// Response to a circuit request.
/// </summary>
public sealed class CircuitResponseMessage : RelayMessage
{
    public override RelayMessageType Type => RelayMessageType.CircuitResponse;

    public Guid CircuitId { get; init; }
    public bool Accepted { get; init; }
    public string? RejectReason { get; init; }

    /// <summary>
    /// The endpoint of the target node (if found).
    /// </summary>
    public string? TargetEndpoint { get; init; }

    protected override void SerializePayload(BinaryWriter writer)
    {
        writer.Write(CircuitId.ToByteArray());
        writer.Write(Accepted);
        writer.Write(RejectReason ?? string.Empty);
        writer.Write(TargetEndpoint ?? string.Empty);
    }

    public static CircuitResponseMessage DeserializePayload(BinaryReader reader, Guid messageId)
    {
        var circuitId = new Guid(reader.ReadBytes(16));
        var accepted = reader.ReadBoolean();
        var rejectReason = ReadBoundedString(reader);
        var targetEndpoint = ReadBoundedString(reader);

        return new CircuitResponseMessage
        {
            MessageId = messageId,
            CircuitId = circuitId,
            Accepted = accepted,
            RejectReason = string.IsNullOrEmpty(rejectReason) ? null : rejectReason,
            TargetEndpoint = string.IsNullOrEmpty(targetEndpoint) ? null : targetEndpoint
        };
    }
}

/// <summary>
/// Data to relay through an established circuit.
/// </summary>
public sealed class RelayDataMessage : RelayMessage
{
    public override RelayMessageType Type => RelayMessageType.RelayData;

    public Guid CircuitId { get; init; }
    public byte[] Data { get; init; } = Array.Empty<byte>();

    protected override void SerializePayload(BinaryWriter writer)
    {
        writer.Write(CircuitId.ToByteArray());
        writer.Write(Data.Length);
        writer.Write(Data);
    }

    public static RelayDataMessage DeserializePayload(BinaryReader reader, Guid messageId)
    {
        var circuitId = new Guid(reader.ReadBytes(16));
        var dataLen = reader.ReadInt32();
        ValidateLength(dataLen, "relay data");
        var data = reader.ReadBytes(dataLen);

        return new RelayDataMessage
        {
            MessageId = messageId,
            CircuitId = circuitId,
            Data = data
        };
    }
}

/// <summary>
/// Close a relay circuit.
/// </summary>
public sealed class CircuitCloseMessage : RelayMessage
{
    public override RelayMessageType Type => RelayMessageType.CircuitClose;

    public Guid CircuitId { get; init; }

    protected override void SerializePayload(BinaryWriter writer)
    {
        writer.Write(CircuitId.ToByteArray());
    }

    public static CircuitCloseMessage DeserializePayload(BinaryReader reader, Guid messageId)
    {
        var circuitId = new Guid(reader.ReadBytes(16));
        return new CircuitCloseMessage { MessageId = messageId, CircuitId = circuitId };
    }
}

/// <summary>
/// Stateless relay request - relay a single message to a target.
/// </summary>
public sealed class RelayRequestMessage : RelayMessage
{
    public override RelayMessageType Type => RelayMessageType.RelayRequest;

    /// <summary>
    /// Target node to relay to.
    /// </summary>
    public KademliaId TargetNodeId { get; init; }

    /// <summary>
    /// The payload to relay.
    /// </summary>
    public byte[] Payload { get; init; } = Array.Empty<byte>();

    /// <summary>
    /// Whether to wait for and relay back a response.
    /// </summary>
    public bool ExpectResponse { get; init; }

    protected override void SerializePayload(BinaryWriter writer)
    {
        writer.Write(TargetNodeId.Bytes);
        writer.Write(Payload.Length);
        writer.Write(Payload);
        writer.Write(ExpectResponse);
    }

    public static RelayRequestMessage DeserializePayload(BinaryReader reader, Guid messageId)
    {
        var targetNodeId = KademliaId.FromBytes(reader.ReadBytes(32));
        var payloadLen = reader.ReadInt32();
        ValidateLength(payloadLen, "relay request payload");
        var payload = reader.ReadBytes(payloadLen);
        var expectResponse = reader.ReadBoolean();

        return new RelayRequestMessage
        {
            MessageId = messageId,
            TargetNodeId = targetNodeId,
            Payload = payload,
            ExpectResponse = expectResponse
        };
    }
}

/// <summary>
/// Response from a relayed request.
/// </summary>
public sealed class RelayResponseMessage : RelayMessage
{
    public override RelayMessageType Type => RelayMessageType.RelayResponse;

    /// <summary>
    /// The original request ID.
    /// </summary>
    public Guid InResponseTo { get; init; }

    /// <summary>
    /// Whether the relay was successful.
    /// </summary>
    public bool Success { get; init; }

    /// <summary>
    /// The response payload (if any).
    /// </summary>
    public byte[] Payload { get; init; } = Array.Empty<byte>();

    /// <summary>
    /// Error message if failed.
    /// </summary>
    public string? Error { get; init; }

    protected override void SerializePayload(BinaryWriter writer)
    {
        writer.Write(InResponseTo.ToByteArray());
        writer.Write(Success);
        writer.Write(Payload.Length);
        writer.Write(Payload);
        writer.Write(Error ?? string.Empty);
    }

    public static RelayResponseMessage DeserializePayload(BinaryReader reader, Guid messageId)
    {
        var inResponseTo = new Guid(reader.ReadBytes(16));
        var success = reader.ReadBoolean();
        var payloadLen = reader.ReadInt32();
        ValidateLength(payloadLen, "relay response payload");
        var payload = reader.ReadBytes(payloadLen);
        var error = ReadBoundedString(reader);

        return new RelayResponseMessage
        {
            MessageId = messageId,
            InResponseTo = inResponseTo,
            Success = success,
            Payload = payload,
            Error = string.IsNullOrEmpty(error) ? null : error
        };
    }
}

/// <summary>
/// Represents an established relay circuit.
/// </summary>
public sealed class RelayCircuit
{
    public Guid CircuitId { get; init; }
    public IPEndPoint RequesterEndpoint { get; init; } = null!;
    public KademliaId TargetNodeId { get; init; }
    public IPEndPoint? TargetEndpoint { get; set; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset LastActivity { get; set; } = DateTimeOffset.UtcNow;
    public long BytesRelayed { get; set; }

    public bool IsExpired(TimeSpan timeout) => DateTimeOffset.UtcNow - LastActivity > timeout;
}
