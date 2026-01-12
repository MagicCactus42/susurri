using System.Collections.Concurrent;
using System.Security.Cryptography;
using NSec.Cryptography;

namespace Susurri.Modules.DHT.Core.Onion.GroupChat;

public sealed class GroupManager : IDisposable
{
    private readonly ConcurrentDictionary<Guid, GroupInfo> _groups = new();
    private readonly Key _encryptionKey;
    private readonly byte[] _publicKey;
    private readonly string _storagePath;

    public GroupManager(Key encryptionKey)
    {
        _encryptionKey = encryptionKey;
        _publicKey = encryptionKey.PublicKey.Export(KeyBlobFormat.RawPublicKey);

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        _storagePath = Path.Combine(appData, "Susurri", "groups");
        Directory.CreateDirectory(_storagePath);

        LoadGroups();
    }

    public GroupInfo CreateGroup(string name)
    {
        var groupKey = GroupKey.Create();
        var info = new GroupInfo
        {
            GroupId = groupKey.GroupId,
            Name = name,
            Key = groupKey,
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            IsOwner = true,
            Members = new List<GroupMember>
            {
                new()
                {
                    PublicKey = _publicKey,
                    JoinedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    Role = GroupRole.Owner
                }
            }
        };

        _groups[info.GroupId] = info;
        SaveGroup(info);

        return info;
    }

    public GroupInfo? JoinGroup(WrappedGroupKey wrappedKey, string name)
    {
        try
        {
            var groupKey = GroupKey.UnwrapWithPrivateKey(wrappedKey, _encryptionKey);

            var info = new GroupInfo
            {
                GroupId = groupKey.GroupId,
                Name = name,
                Key = groupKey,
                CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                IsOwner = false,
                Members = new List<GroupMember>
                {
                    new()
                    {
                        PublicKey = _publicKey,
                        JoinedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                        Role = GroupRole.Member
                    }
                }
            };

            _groups[info.GroupId] = info;
            SaveGroup(info);

            return info;
        }
        catch
        {
            return null;
        }
    }

    public void LeaveGroup(Guid groupId)
    {
        if (_groups.TryRemove(groupId, out _))
        {
            var filePath = GetGroupFilePath(groupId);
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }
        }
    }

    public GroupInfo? GetGroup(Guid groupId)
        => _groups.TryGetValue(groupId, out var info) ? info : null;

    public IEnumerable<GroupInfo> GetAllGroups()
        => _groups.Values;

    public WrappedGroupKey GenerateInvite(Guid groupId, byte[] memberPublicKey)
    {
        if (!_groups.TryGetValue(groupId, out var info))
            throw new InvalidOperationException("Group not found");

        return info.Key.WrapForMember(memberPublicKey);
    }

    public void AddMember(Guid groupId, byte[] memberPublicKey)
    {
        if (!_groups.TryGetValue(groupId, out var info))
            throw new InvalidOperationException("Group not found");

        if (info.Members.Any(m => m.PublicKey.SequenceEqual(memberPublicKey)))
            return;

        info.Members.Add(new GroupMember
        {
            PublicKey = memberPublicKey,
            JoinedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Role = GroupRole.Member
        });

        SaveGroup(info);
    }

    public void RemoveMember(Guid groupId, byte[] memberPublicKey)
    {
        if (!_groups.TryGetValue(groupId, out var info))
            throw new InvalidOperationException("Group not found");

        var member = info.Members.FirstOrDefault(m => m.PublicKey.SequenceEqual(memberPublicKey));
        if (member != null)
        {
            info.Members.Remove(member);
            SaveGroup(info);
        }
    }

    public void RotateKey(Guid groupId)
    {
        if (!_groups.TryGetValue(groupId, out var info))
            throw new InvalidOperationException("Group not found");

        if (!info.IsOwner)
            throw new InvalidOperationException("Only group owner can rotate keys");

        info.Key = info.Key.Rotate();
        SaveGroup(info);
    }

    private void LoadGroups()
    {
        if (!Directory.Exists(_storagePath))
            return;

        foreach (var file in Directory.GetFiles(_storagePath, "*.grp"))
        {
            try
            {
                var data = File.ReadAllBytes(file);
                var info = GroupInfo.Deserialize(data);
                _groups[info.GroupId] = info;
            }
            catch
            {
                // Skip corrupted group files
            }
        }
    }

    private void SaveGroup(GroupInfo info)
    {
        var filePath = GetGroupFilePath(info.GroupId);
        File.WriteAllBytes(filePath, info.Serialize());
    }

    private string GetGroupFilePath(Guid groupId)
        => Path.Combine(_storagePath, $"{groupId:N}.grp");

    public void Dispose()
    {
        _groups.Clear();
    }
}

public sealed class GroupInfo
{
    public Guid GroupId { get; init; }
    public string Name { get; init; } = string.Empty;
    public GroupKey Key { get; set; } = null!;
    public long CreatedAt { get; init; }
    public bool IsOwner { get; init; }
    public List<GroupMember> Members { get; init; } = new();

    public byte[] Serialize()
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);

        writer.Write(GroupId.ToByteArray());
        writer.Write(Name);
        var keyData = Key.Serialize();
        writer.Write(keyData.Length);
        writer.Write(keyData);
        writer.Write(CreatedAt);
        writer.Write(IsOwner);

        writer.Write(Members.Count);
        foreach (var member in Members)
        {
            writer.Write((byte)member.PublicKey.Length);
            writer.Write(member.PublicKey);
            writer.Write(member.JoinedAt);
            writer.Write((byte)member.Role);
        }

        return ms.ToArray();
    }

    public static GroupInfo Deserialize(byte[] data)
    {
        using var ms = new MemoryStream(data);
        using var reader = new BinaryReader(ms);

        var groupId = new Guid(reader.ReadBytes(16));
        var name = reader.ReadString();
        var keyLen = reader.ReadInt32();
        var keyData = reader.ReadBytes(keyLen);
        var key = GroupKey.Deserialize(keyData);
        var createdAt = reader.ReadInt64();
        var isOwner = reader.ReadBoolean();

        var memberCount = reader.ReadInt32();
        var members = new List<GroupMember>(memberCount);
        for (int i = 0; i < memberCount; i++)
        {
            var pubKeyLen = reader.ReadByte();
            var publicKey = reader.ReadBytes(pubKeyLen);
            var joinedAt = reader.ReadInt64();
            var role = (GroupRole)reader.ReadByte();

            members.Add(new GroupMember
            {
                PublicKey = publicKey,
                JoinedAt = joinedAt,
                Role = role
            });
        }

        return new GroupInfo
        {
            GroupId = groupId,
            Name = name,
            Key = key,
            CreatedAt = createdAt,
            IsOwner = isOwner,
            Members = members
        };
    }
}

public sealed class GroupMember
{
    public byte[] PublicKey { get; init; } = Array.Empty<byte>();
    public long JoinedAt { get; init; }
    public GroupRole Role { get; init; }
}

public enum GroupRole : byte
{
    Member = 0,
    Admin = 1,
    Owner = 2
}
