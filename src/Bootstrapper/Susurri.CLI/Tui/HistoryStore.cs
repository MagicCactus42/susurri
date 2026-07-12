using Susurri.Modules.DHT.Core.Services;
using Susurri.Shared.Abstractions.Security;

namespace Susurri.CLI.Tui;

internal sealed class HistoryStore
{
    private const byte StateVersion = 1;

    private readonly byte[] _key;
    private readonly string _filePath;
    private readonly object _gate = new();

    public HistoryStore(byte[] localStoreKey, byte[] localPublicKey)
    {
        _key = LocalEncryption.DeriveSubkey(localStoreKey, HkdfContexts.LocalHistory);

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var directory = Path.Combine(appData, "Susurri", "history");
        Directory.CreateDirectory(directory);
        LocalEncryption.RestrictDirectory(directory);
        _filePath = Path.Combine(directory,
            $"{Convert.ToHexString(localPublicKey)[..16].ToLowerInvariant()}.hst");
    }

    public bool Enabled => File.Exists(_filePath);

    public long SizeBytes => Enabled ? new FileInfo(_filePath).Length : 0;

    public void Enable()
    {
        if (!Enabled)
            Save(Array.Empty<Conversation>());
    }

    public void Disable()
    {
        lock (_gate)
        {
            LocalEncryption.SecureDelete(_filePath);
        }
    }

    public List<Conversation> Load()
    {
        lock (_gate)
        {
            if (!File.Exists(_filePath))
                return new List<Conversation>();

            try
            {
                var data = LocalEncryption.Decrypt(_key, File.ReadAllBytes(_filePath));
                return Deserialize(data);
            }
            catch
            {
                LocalEncryption.QuarantineCorrupt(_filePath);
                return new List<Conversation>();
            }
        }
    }

    public void Save(IReadOnlyList<Conversation> conversations)
    {
        var data = Serialize(conversations);
        lock (_gate)
        {
            File.WriteAllBytes(_filePath, LocalEncryption.Encrypt(_key, data));
        }
    }

    private static byte[] Serialize(IReadOnlyList<Conversation> conversations)
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);

        writer.Write(StateVersion);
        writer.Write(conversations.Count);
        foreach (var conv in conversations)
        {
            writer.Write((byte)conv.Kind);
            writer.Write(conv.Key);
            writer.Write(conv.Title);
            writer.Write(conv.LastActivity.ToUnixTimeMilliseconds());
            writer.Write(conv.Entries.Count);
            foreach (var entry in conv.Entries)
            {
                writer.Write(entry.Id.ToByteArray());
                writer.Write(entry.Sender);
                writer.Write(entry.Content);
                writer.Write(entry.At.ToUnixTimeMilliseconds());
                writer.Write(entry.Outgoing);
                writer.Write((byte)entry.Status);
            }
        }

        writer.Flush();
        return ms.ToArray();
    }

    private static List<Conversation> Deserialize(byte[] data)
    {
        using var ms = new MemoryStream(data);
        using var reader = new BinaryReader(ms);

        var conversations = new List<Conversation>();
        if (reader.ReadByte() != StateVersion)
            return conversations;

        var count = reader.ReadInt32();
        for (var i = 0; i < count; i++)
        {
            var conv = new Conversation
            {
                Kind = (ConversationKind)reader.ReadByte(),
                Key = reader.ReadString(),
                Title = reader.ReadString(),
                LastActivity = DateTimeOffset.FromUnixTimeMilliseconds(reader.ReadInt64()).ToLocalTime()
            };

            var entryCount = reader.ReadInt32();
            for (var j = 0; j < entryCount; j++)
            {
                var entry = new ChatEntry
                {
                    Id = new Guid(reader.ReadBytes(16)),
                    Sender = reader.ReadString(),
                    Content = reader.ReadString(),
                    At = DateTimeOffset.FromUnixTimeMilliseconds(reader.ReadInt64()).ToLocalTime(),
                    Outgoing = reader.ReadBoolean(),
                    Status = (MessageStatus)reader.ReadByte()
                };
                if (entry.Status == MessageStatus.Sending)
                    entry.Status = MessageStatus.Failed;
                conv.Entries.Add(entry);
            }

            conversations.Add(conv);
        }

        return conversations;
    }
}
