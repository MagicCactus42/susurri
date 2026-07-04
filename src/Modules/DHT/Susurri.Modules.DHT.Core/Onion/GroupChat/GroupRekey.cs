using NSec.Cryptography;
using Susurri.Shared.Abstractions.Security;

namespace Susurri.Modules.DHT.Core.Onion.GroupChat;

public sealed class GroupRekeyMessage
{
    private const int MaxRosterSize = 1024;

    public Guid GroupId { get; init; }
    public Guid MessageId { get; init; } = Guid.NewGuid();
    public WrappedGroupKey Wrapped { get; init; } = null!;
    public List<GroupMember> Roster { get; init; } = new();
    public byte[] OwnerSigningPublicKey { get; init; } = Array.Empty<byte>();
    public long Timestamp { get; init; }
    public byte[] Signature { get; set; } = Array.Empty<byte>();

    public byte[] GetSignableData()
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);
        WriteBody(writer);
        return ms.ToArray();
    }

    public byte[] Serialize()
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);
        WriteBody(writer);
        writer.Write((ushort)Signature.Length);
        writer.Write(Signature);
        return ms.ToArray();
    }

    private void WriteBody(BinaryWriter writer)
    {
        writer.Write(GroupId.ToByteArray());
        writer.Write(MessageId.ToByteArray());

        var wrappedBytes = Wrapped.Serialize();
        writer.Write(wrappedBytes.Length);
        writer.Write(wrappedBytes);

        writer.Write(Roster.Count);
        foreach (var member in Roster)
        {
            writer.Write((byte)member.PublicKey.Length);
            writer.Write(member.PublicKey);
            writer.Write(member.JoinedAt);
            writer.Write((byte)member.Role);
        }

        writer.Write((byte)OwnerSigningPublicKey.Length);
        writer.Write(OwnerSigningPublicKey);
        writer.Write(Timestamp);
    }

    public static GroupRekeyMessage Deserialize(byte[] data)
    {
        using var ms = new MemoryStream(data);
        using var reader = new BinaryReader(ms);

        var groupId = new Guid(reader.ReadBytes(16));
        var messageId = new Guid(reader.ReadBytes(16));

        var wrappedLen = reader.ReadInt32();
        if (wrappedLen <= 0 || wrappedLen > SecurityLimits.MaxValueSize)
            throw new InvalidDataException($"GroupRekey wrapped-key length {wrappedLen} out of range");
        var wrapped = WrappedGroupKey.Deserialize(reader.ReadBytes(wrappedLen));

        var rosterCount = reader.ReadInt32();
        if (rosterCount < 0 || rosterCount > MaxRosterSize)
            throw new InvalidDataException($"GroupRekey roster size {rosterCount} out of range");
        var roster = new List<GroupMember>(rosterCount);
        for (var i = 0; i < rosterCount; i++)
        {
            var pubKeyLen = reader.ReadByte();
            if (pubKeyLen != SecurityLimits.PublicKeySize)
                throw new InvalidDataException($"GroupRekey roster key length {pubKeyLen} invalid");
            roster.Add(new GroupMember
            {
                PublicKey = reader.ReadBytes(pubKeyLen),
                JoinedAt = reader.ReadInt64(),
                Role = (GroupRole)reader.ReadByte()
            });
        }

        var ownerKeyLen = reader.ReadByte();
        if (ownerKeyLen != SecurityLimits.PublicKeySize)
            throw new InvalidDataException($"GroupRekey owner key length {ownerKeyLen} invalid");
        var ownerSigningPublicKey = reader.ReadBytes(ownerKeyLen);
        var timestamp = reader.ReadInt64();

        var sigLen = reader.ReadUInt16();
        if (sigLen > 512)
            throw new InvalidDataException($"GroupRekey signature length {sigLen} out of range");
        var signature = reader.ReadBytes(sigLen);

        return new GroupRekeyMessage
        {
            GroupId = groupId,
            MessageId = messageId,
            Wrapped = wrapped,
            Roster = roster,
            OwnerSigningPublicKey = ownerSigningPublicKey,
            Timestamp = timestamp,
            Signature = signature
        };
    }

    public bool VerifySignature()
    {
        if (OwnerSigningPublicKey.Length != SecurityLimits.PublicKeySize || Signature.Length == 0)
            return false;

        try
        {
            var signingKey = PublicKey.Import(
                SignatureAlgorithm.Ed25519, OwnerSigningPublicKey, KeyBlobFormat.RawPublicKey);
            return SignatureAlgorithm.Ed25519.Verify(signingKey, GetSignableData(), Signature);
        }
        catch
        {
            return false;
        }
    }
}
