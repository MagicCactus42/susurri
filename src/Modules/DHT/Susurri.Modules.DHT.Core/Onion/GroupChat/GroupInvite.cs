namespace Susurri.Modules.DHT.Core.Onion.GroupChat;

/// <summary>
/// Encodes a group name plus a member-wrapped group key into a shareable
/// base64 invite code. The recipient decodes it and joins with their private
/// key (the key material is sealed to their public key inside the wrapped key).
/// </summary>
public static class GroupInvite
{
    private const int MaxWrappedKeySize = 64 * 1024;

    public static string Encode(string groupName, WrappedGroupKey key)
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);

        writer.Write(groupName);
        var keyBytes = key.Serialize();
        writer.Write(keyBytes.Length);
        writer.Write(keyBytes);

        return Convert.ToBase64String(ms.ToArray());
    }

    public static (string Name, WrappedGroupKey Key) Decode(string code)
    {
        var data = Convert.FromBase64String(code.Trim());
        using var ms = new MemoryStream(data);
        using var reader = new BinaryReader(ms);

        var name = reader.ReadString();
        var length = reader.ReadInt32();
        if (length < 0 || length > MaxWrappedKeySize)
            throw new InvalidDataException("Invalid invite code");

        var keyBytes = reader.ReadBytes(length);
        return (name, WrappedGroupKey.Deserialize(keyBytes));
    }
}
