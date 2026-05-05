using Susurri.Modules.DHT.Core.Kademlia;
using Susurri.Modules.DHT.Core.Kademlia.Protocol;
using Susurri.Modules.DHT.Core.Onion;
using Susurri.Modules.DHT.Core.Onion.GroupChat;
using Susurri.Modules.DHT.Core.Services;

namespace Susurri.Tests.Fuzz;

/// <summary>
/// One method per parser. Each takes a raw byte payload and either parses
/// to a valid object or throws a graceful-rejection exception. The harness
/// (Program.cs / SharpFuzz) and the xUnit smoke tests both call into here.
/// Keeping the targets in a single static class lets us register them by
/// name without any per-target ceremony.
/// </summary>
public static class FuzzTargets
{
    public static void KademliaMessageDeserialize(byte[] data)
        => KademliaMessage.Deserialize(data);

    public static void OnionLayerDeserialize(byte[] data)
        => OnionLayer.Deserialize(data);

    public static void OnionLayerContentDeserialize(byte[] data)
        => OnionLayerContent.Deserialize(data);

    public static void ChatMessageDeserialize(byte[] data)
        => ChatMessage.Deserialize(data);

    public static void UserPublicKeyRecordDeserialize(byte[] data)
        => UserPublicKeyRecord.Deserialize(data);

    public static void FileTransferMessageDeserialize(byte[] data)
        => FileTransferMessage.Deserialize(data);

    public static void RecipientPayloadDeserialize(byte[] data)
        => RecipientPayload.Deserialize(data);

    public static void ReplyPathDeserialize(byte[] data)
        => ReplyPath.Deserialize(data);

    public static void ReplyTokenContentDeserialize(byte[] data)
        => ReplyTokenContent.Deserialize(data);

    public static void GroupKeyDeserialize(byte[] data)
        => GroupKey.Deserialize(data);

    public static void WrappedGroupKeyDeserialize(byte[] data)
        => WrappedGroupKey.Deserialize(data);

    public static void GroupMessageDeserialize(byte[] data)
        => GroupMessage.Deserialize(data);

    public static void EncryptedGroupMessageDeserialize(byte[] data)
        => EncryptedGroupMessage.Deserialize(data);

    /// <summary>
    /// Map of target name → action. Lets <see cref="Program"/> dispatch by
    /// the name passed on the command line, and lets the xUnit smoke tests
    /// iterate over every registered target.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, Action<byte[]>> All =
        new Dictionary<string, Action<byte[]>>(StringComparer.OrdinalIgnoreCase)
        {
            ["kademlia"] = KademliaMessageDeserialize,
            ["onion-layer"] = OnionLayerDeserialize,
            ["onion-content"] = OnionLayerContentDeserialize,
            ["chat"] = ChatMessageDeserialize,
            ["pubkey-record"] = UserPublicKeyRecordDeserialize,
            ["file-transfer"] = FileTransferMessageDeserialize,
            ["recipient"] = RecipientPayloadDeserialize,
            ["reply-path"] = ReplyPathDeserialize,
            ["reply-token"] = ReplyTokenContentDeserialize,
            ["group-key"] = GroupKeyDeserialize,
            ["wrapped-group-key"] = WrappedGroupKeyDeserialize,
            ["group-message"] = GroupMessageDeserialize,
            ["encrypted-group-message"] = EncryptedGroupMessageDeserialize,
        };

    /// <summary>
    /// True if <paramref name="ex"/> is one of the exception types the parsers
    /// are expected to throw on malformed input. Anything else propagating
    /// out of a fuzz target is a real bug.
    /// </summary>
    public static bool IsGracefulRejection(Exception ex) => ex is
        InvalidDataException
        or EndOfStreamException
        or OverflowException
        or ArgumentException
        or FormatException
        or IOException;
}
