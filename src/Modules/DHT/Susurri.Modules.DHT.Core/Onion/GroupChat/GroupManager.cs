using System.Collections.Concurrent;
using System.Security.Cryptography;
using NSec.Cryptography;
using Susurri.Shared.Abstractions.Security;

namespace Susurri.Modules.DHT.Core.Onion.GroupChat;

public sealed class GroupManager : IDisposable
{
    private readonly ConcurrentDictionary<Guid, GroupInfo> _groups = new();
    private readonly Key _encryptionKey;
    private readonly byte[] _publicKey;
    private readonly byte[] _localSigningPublicKey;
    private readonly byte[]? _storageKey;
    private readonly string _storagePath;

    public GroupManager(Key encryptionKey, byte[]? localStoreKey = null, byte[]? localSigningPublicKey = null, string? storagePath = null)
    {
        _encryptionKey = encryptionKey;
        _publicKey = encryptionKey.PublicKey.Export(KeyBlobFormat.RawPublicKey);
        _localSigningPublicKey = localSigningPublicKey ?? Array.Empty<byte>();
        _storageKey = localStoreKey != null
            ? LocalEncryption.DeriveSubkey(localStoreKey, HkdfContexts.LocalGroups)
            : null;

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        _storagePath = storagePath ?? Path.Combine(appData, "Susurri", "groups");
        Directory.CreateDirectory(_storagePath);
        LocalEncryption.RestrictDirectory(_storagePath);

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
            OwnerSigningPublicKey = _localSigningPublicKey,
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

    public GroupInfo? JoinGroup(WrappedGroupKey wrappedKey, string name, byte[]? ownerSigningPublicKey = null)
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
                OwnerSigningPublicKey = ownerSigningPublicKey ?? Array.Empty<byte>(),
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

    public GroupInfo? ApplyRekey(GroupRekeyMessage rekey)
    {
        if (!_groups.TryGetValue(rekey.GroupId, out var info))
            return null;

        if (info.IsOwner)
            return null;

        if (info.OwnerSigningPublicKey.Length == 0 ||
            !rekey.OwnerSigningPublicKey.AsSpan().SequenceEqual(info.OwnerSigningPublicKey))
            return null;

        if (!rekey.VerifySignature())
            return null;

        GroupKey newKey;
        try
        {
            newKey = GroupKey.UnwrapWithPrivateKey(rekey.Wrapped, _encryptionKey);
        }
        catch
        {
            return null;
        }

        if (newKey.Version <= info.Key.Version)
            return null;

        info.Key = newKey;
        info.Members.Clear();
        info.Members.AddRange(rekey.Roster);
        if (!info.Members.Any(m => m.PublicKey.SequenceEqual(_publicKey)))
        {
            info.Members.Add(new GroupMember
            {
                PublicKey = _publicKey,
                JoinedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                Role = GroupRole.Member
            });
        }
        SaveGroup(info);

        return info;
    }

    public void TryAddKnownMember(Guid groupId, byte[] memberPublicKey)
    {
        if (!_groups.TryGetValue(groupId, out var info))
            return;

        if (memberPublicKey.Length != 32 ||
            info.Members.Any(m => m.PublicKey.SequenceEqual(memberPublicKey)))
            return;

        info.Members.Add(new GroupMember
        {
            PublicKey = memberPublicKey,
            JoinedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Role = GroupRole.Member
        });

        SaveGroup(info);
    }

    public void LeaveGroup(Guid groupId)
    {
        if (_groups.TryRemove(groupId, out _))
        {
            LocalEncryption.SecureDelete(GetGroupFilePath(groupId));
            LocalEncryption.SecureDelete(GetEncryptedGroupFilePath(groupId));
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

        if (_storageKey != null)
        {
            foreach (var file in Directory.GetFiles(_storagePath, "*.grpe"))
            {
                try
                {
                    var data = LocalEncryption.Decrypt(_storageKey, File.ReadAllBytes(file));
                    var info = GroupInfo.Deserialize(data);
                    _groups[info.GroupId] = info;
                }
                catch
                {
                }
            }
        }

        foreach (var file in Directory.GetFiles(_storagePath, "*.grp"))
        {
            try
            {
                var data = File.ReadAllBytes(file);
                var info = GroupInfo.Deserialize(data);
                if (_groups.TryAdd(info.GroupId, info))
                {
                    if (_storageKey != null)
                        SaveGroup(info);
                }
                else if (_storageKey != null)
                {
                    LocalEncryption.SecureDelete(file);
                }
            }
            catch
            {
            }
        }
    }

    private void SaveGroup(GroupInfo info)
    {
        var data = info.Serialize();
        if (_storageKey != null)
        {
            File.WriteAllBytes(GetEncryptedGroupFilePath(info.GroupId), LocalEncryption.Encrypt(_storageKey, data));
            LocalEncryption.SecureDelete(GetGroupFilePath(info.GroupId));
        }
        else
        {
            File.WriteAllBytes(GetGroupFilePath(info.GroupId), data);
        }
    }

    private string GetGroupFilePath(Guid groupId)
        => Path.Combine(_storagePath, $"{groupId:N}.grp");

    private string GetEncryptedGroupFilePath(Guid groupId)
        => Path.Combine(_storagePath, $"{groupId:N}.grpe");

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
    public byte[] OwnerSigningPublicKey { get; init; } = Array.Empty<byte>();
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

        writer.Write((byte)OwnerSigningPublicKey.Length);
        writer.Write(OwnerSigningPublicKey);

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

        var ownerSigningPublicKey = Array.Empty<byte>();
        if (ms.Position < ms.Length)
        {
            var ownerKeyLen = reader.ReadByte();
            ownerSigningPublicKey = reader.ReadBytes(ownerKeyLen);
        }

        return new GroupInfo
        {
            GroupId = groupId,
            Name = name,
            Key = key,
            CreatedAt = createdAt,
            IsOwner = isOwner,
            OwnerSigningPublicKey = ownerSigningPublicKey,
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
