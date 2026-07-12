using Susurri.Shared.Abstractions.Security;

namespace Susurri.Modules.DHT.Core.Contacts;

public sealed class Contact
{
    public string Petname { get; init; } = string.Empty;
    public string Username { get; init; } = string.Empty;
    public byte[] EncryptionPublicKey { get; init; } = Array.Empty<byte>();
    public byte[] SigningPublicKey { get; init; } = Array.Empty<byte>();
    public bool Verified { get; set; }
    public long AddedAt { get; init; }
}

public sealed class ContactBook
{
    private const byte StateVersion = 1;

    private readonly object _gate = new();
    private readonly Dictionary<string, Contact> _byPetname = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Contact> _byUsername = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Contact> _byEncKeyHex = new(StringComparer.Ordinal);
    private readonly byte[] _storageKey;
    private readonly string _filePath;

    public ContactBook(byte[] localStoreKey, byte[] localPublicKey)
    {
        _storageKey = LocalEncryption.DeriveSubkey(localStoreKey, HkdfContexts.LocalContacts);

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var directory = Path.Combine(appData, "Susurri", "contacts");
        Directory.CreateDirectory(directory);
        LocalEncryption.RestrictDirectory(directory);
        _filePath = Path.Combine(directory,
            $"{Convert.ToHexString(localPublicKey)[..16].ToLowerInvariant()}.cbk");

        Load();
    }

    public IReadOnlyList<Contact> All()
    {
        lock (_gate)
        {
            return _byPetname.Values.OrderBy(c => c.Petname, StringComparer.OrdinalIgnoreCase).ToList();
        }
    }

    public Contact? FindByPetname(string petname)
    {
        lock (_gate)
        {
            return _byPetname.GetValueOrDefault(petname);
        }
    }

    public Contact? FindByUsername(string username)
    {
        lock (_gate)
        {
            return _byUsername.GetValueOrDefault(username);
        }
    }

    public Contact? Find(string petnameOrUsername)
        => FindByPetname(petnameOrUsername) ?? FindByUsername(petnameOrUsername);

    public Contact? FindByEncryptionKey(byte[] encryptionPublicKey)
    {
        lock (_gate)
        {
            return _byEncKeyHex.GetValueOrDefault(Convert.ToHexString(encryptionPublicKey));
        }
    }

    public bool Add(Contact contact)
    {
        lock (_gate)
        {
            if (_byPetname.Count >= SecurityLimits.MaxContacts)
                return false;
            if (!_byPetname.TryAdd(contact.Petname, contact))
                return false;
            IndexLocked(contact);
            SaveLocked();
            return true;
        }
    }

    public bool Remove(string petname)
    {
        lock (_gate)
        {
            if (!_byPetname.Remove(petname, out var removed))
                return false;
            UnindexLocked(removed);
            SaveLocked();
            return true;
        }
    }

    public bool Rename(string petname, string newPetname)
    {
        lock (_gate)
        {
            if (!_byPetname.TryGetValue(petname, out var existing) || _byPetname.ContainsKey(newPetname))
                return false;

            _byPetname.Remove(petname);
            UnindexLocked(existing);

            var renamed = new Contact
            {
                Petname = newPetname,
                Username = existing.Username,
                EncryptionPublicKey = existing.EncryptionPublicKey,
                SigningPublicKey = existing.SigningPublicKey,
                Verified = existing.Verified,
                AddedAt = existing.AddedAt
            };
            _byPetname[newPetname] = renamed;
            IndexLocked(renamed);
            SaveLocked();
            return true;
        }
    }

    private void IndexLocked(Contact contact)
    {
        _byUsername[contact.Username] = contact;
        _byEncKeyHex[Convert.ToHexString(contact.EncryptionPublicKey)] = contact;
    }

    private void UnindexLocked(Contact contact)
    {
        if (_byUsername.TryGetValue(contact.Username, out var byName) && ReferenceEquals(byName, contact))
            _byUsername.Remove(contact.Username);

        var hex = Convert.ToHexString(contact.EncryptionPublicKey);
        if (_byEncKeyHex.TryGetValue(hex, out var byKey) && ReferenceEquals(byKey, contact))
            _byEncKeyHex.Remove(hex);
    }

    public bool SetVerified(string petname, bool verified)
    {
        lock (_gate)
        {
            if (!_byPetname.TryGetValue(petname, out var contact))
                return false;
            contact.Verified = verified;
            SaveLocked();
            return true;
        }
    }

    private void Load()
    {
        if (!File.Exists(_filePath))
            return;

        try
        {
            var data = LocalEncryption.Decrypt(_storageKey, File.ReadAllBytes(_filePath));
            using var ms = new MemoryStream(data);
            using var reader = new BinaryReader(ms);

            if (reader.ReadByte() != StateVersion)
                return;

            var count = reader.ReadInt32();
            for (var i = 0; i < count; i++)
            {
                var contact = new Contact
                {
                    Petname = reader.ReadString(),
                    Username = reader.ReadString(),
                    EncryptionPublicKey = reader.ReadBytes(reader.ReadByte()),
                    SigningPublicKey = reader.ReadBytes(reader.ReadByte()),
                    Verified = reader.ReadBoolean(),
                    AddedAt = reader.ReadInt64()
                };
                _byPetname[contact.Petname] = contact;
                IndexLocked(contact);
            }
        }
        catch
        {
            _byPetname.Clear();
            _byUsername.Clear();
            _byEncKeyHex.Clear();
            LocalEncryption.QuarantineCorrupt(_filePath);
        }
    }

    private void SaveLocked()
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);

        writer.Write(StateVersion);
        writer.Write(_byPetname.Count);
        foreach (var contact in _byPetname.Values)
        {
            writer.Write(contact.Petname);
            writer.Write(contact.Username);
            writer.Write((byte)contact.EncryptionPublicKey.Length);
            writer.Write(contact.EncryptionPublicKey);
            writer.Write((byte)contact.SigningPublicKey.Length);
            writer.Write(contact.SigningPublicKey);
            writer.Write(contact.Verified);
            writer.Write(contact.AddedAt);
        }

        writer.Flush();
        File.WriteAllBytes(_filePath, LocalEncryption.Encrypt(_storageKey, ms.ToArray()));
    }
}
