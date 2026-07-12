using System;
using System.Collections.Generic;
using System.IO;
using Susurri.Modules.DHT.Core.Services;
using Susurri.Shared.Abstractions.Security;

namespace Susurri.GUI.Services;

public sealed class GuiHistoryStore
{
    private const byte StateVersion = 1;

    private readonly byte[] _key;
    private readonly string _filePath;
    private readonly object _gate = new();

    public GuiHistoryStore(byte[] localStoreKey, byte[] localPublicKey)
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

    public void Enable(IReadOnlyList<ConversationModel> conversations)
    {
        if (!Enabled)
            Save(conversations);
    }

    public void Disable()
    {
        lock (_gate)
        {
            LocalEncryption.SecureDelete(_filePath);
        }
    }

    public List<ConversationModel> Load()
    {
        lock (_gate)
        {
            if (!File.Exists(_filePath))
                return new List<ConversationModel>();

            try
            {
                var data = LocalEncryption.Decrypt(_key, File.ReadAllBytes(_filePath));
                return Deserialize(data);
            }
            catch
            {
                LocalEncryption.QuarantineCorrupt(_filePath);
                return new List<ConversationModel>();
            }
        }
    }

    public void Save(IReadOnlyList<ConversationModel> conversations)
    {
        var data = Serialize(conversations);
        lock (_gate)
        {
            File.WriteAllBytes(_filePath, LocalEncryption.Encrypt(_key, data));
        }
    }

    private static byte[] Serialize(IReadOnlyList<ConversationModel> conversations)
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);

        writer.Write(StateVersion);
        writer.Write(conversations.Count);
        foreach (var conversation in conversations)
        {
            writer.Write((byte)conversation.Kind);
            writer.Write(conversation.Key);
            writer.Write(conversation.Title);
            writer.Write(conversation.Subtitle);
            writer.Write(conversation.Target);
            writer.Write(conversation.IsOwner);
            writer.Write(conversation.GroupId?.ToByteArray() ?? new byte[16]);
            writer.Write(conversation.GroupId.HasValue);
            writer.Write(conversation.LastActivity.ToUnixTimeMilliseconds());
            writer.Write(conversation.Messages.Count);
            foreach (var message in conversation.Messages)
            {
                writer.Write(message.Id.ToByteArray());
                writer.Write(message.Sender);
                writer.Write(message.Content);
                writer.Write(message.At.ToUnixTimeMilliseconds());
                writer.Write(message.Outgoing);
                writer.Write(message.IsEvent);
                writer.Write((byte)message.Status);
            }
        }

        writer.Flush();
        return ms.ToArray();
    }

    private static List<ConversationModel> Deserialize(byte[] data)
    {
        using var ms = new MemoryStream(data);
        using var reader = new BinaryReader(ms);

        var conversations = new List<ConversationModel>();
        if (reader.ReadByte() != StateVersion)
            return conversations;

        var count = reader.ReadInt32();
        for (var i = 0; i < count; i++)
        {
            var kind = (ConversationKind)reader.ReadByte();
            var key = reader.ReadString();
            var title = reader.ReadString();
            var subtitle = reader.ReadString();
            var target = reader.ReadString();
            var isOwner = reader.ReadBoolean();
            var groupIdBytes = reader.ReadBytes(16);
            var hasGroupId = reader.ReadBoolean();
            var lastActivity = DateTimeOffset.FromUnixTimeMilliseconds(reader.ReadInt64());

            var conversation = new ConversationModel
            {
                Kind = kind,
                Key = key,
                Title = title,
                Subtitle = subtitle,
                Target = target,
                IsOwner = isOwner,
                GroupId = hasGroupId ? new Guid(groupIdBytes) : null,
                LastActivity = lastActivity
            };

            var messageCount = reader.ReadInt32();
            for (var j = 0; j < messageCount; j++)
            {
                var id = new Guid(reader.ReadBytes(16));
                var sender = reader.ReadString();
                var content = reader.ReadString();
                var at = DateTimeOffset.FromUnixTimeMilliseconds(reader.ReadInt64());
                var outgoing = reader.ReadBoolean();
                var isEvent = reader.ReadBoolean();
                var status = (MessageStatus)reader.ReadByte();
                if (status == MessageStatus.Sending)
                    status = MessageStatus.Failed;

                conversation.Messages.Add(new MessageModel
                {
                    Id = id,
                    Sender = sender,
                    Content = content,
                    At = at,
                    Outgoing = outgoing,
                    IsEvent = isEvent,
                    Status = status
                });
            }

            conversations.Add(conversation);
        }

        return conversations;
    }
}
