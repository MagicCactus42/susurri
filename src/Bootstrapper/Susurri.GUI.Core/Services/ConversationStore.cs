using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using Susurri.Modules.DHT.Core.Services;

namespace Susurri.GUI.Services;

public sealed class ConversationStore : IDisposable
{
    private readonly ChatService _chat;
    private readonly string _localUser;
    private readonly GuiHistoryStore? _history;
    private readonly Timer? _saveTimer;
    private readonly System.Collections.Generic.Dictionary<Guid, MessageModel> _outgoingById = new();

    private readonly Func<ReceivedMessage, Task> _onMessage;
    private readonly Func<Guid, Task> _onAck;
    private readonly Func<ReceivedGroupMessage, Task> _onGroupMessage;
    private readonly Func<FileTransferInfo, Task> _onTransferRequested;
    private readonly Func<TransferProgress, Task> _onTransferProgress;
    private readonly Func<CompletedTransfer, Task> _onTransferCompleted;
    private readonly Func<Guid, string, Task> _onTransferFailed;

    public ObservableCollection<ConversationModel> Directs { get; } = new();
    public ObservableCollection<ConversationModel> Groups { get; } = new();
    public ObservableCollection<TransferModel> Transfers { get; } = new();

    public Func<ConversationModel, bool>? IsConversationVisible { get; set; }
    public event Action<ConversationModel>? MessageAppended;
    public event Action? TransfersChanged;
    public event Action? HistoryStateChanged;

    public ConversationStore(ChatService chat, string localUser, GuiHistoryStore? history = null)
    {
        _chat = chat;
        _localUser = localUser;
        _history = history;
        if (_history != null)
            _saveTimer = new Timer(_ => OnSaveTick(), null, Timeout.Infinite, Timeout.Infinite);

        _onMessage = HandleMessageAsync;
        _onAck = HandleAckAsync;
        _onGroupMessage = HandleGroupMessageAsync;
        _onTransferRequested = HandleTransferRequestedAsync;
        _onTransferProgress = HandleTransferProgressAsync;
        _onTransferCompleted = HandleTransferCompletedAsync;
        _onTransferFailed = HandleTransferFailedAsync;

        _chat.OnMessageReceived += _onMessage;
        _chat.OnMessageAcknowledged += _onAck;
        _chat.OnGroupMessageReceived += _onGroupMessage;
        _chat.OnFileTransferRequested += _onTransferRequested;
        _chat.OnFileTransferProgress += _onTransferProgress;
        _chat.OnFileTransferCompleted += _onTransferCompleted;
        _chat.OnFileTransferFailed += _onTransferFailed;

        Seed();
    }

    private void Seed()
    {
        if (_history?.Enabled == true)
        {
            foreach (var conversation in _history.Load())
            {
                if (conversation.IsGroup)
                    Groups.Add(conversation);
                else
                    Directs.Add(conversation);
            }
        }

        foreach (var message in _chat.GetMessages().OrderBy(m => m.ReceivedAt))
            AppendIncomingDirect(message, countUnread: false);

        foreach (var group in _chat.GetGroups())
        {
            var conversation = EnsureGroup(group.GroupId, group.Name, group.IsOwner, group.Members.Count);
            foreach (var message in _chat.GetGroupMessages(group.GroupId).OrderBy(m => m.ReceivedAt))
                AppendIncomingGroup(conversation, message, countUnread: false);
        }

        SyncTransfers();
    }

    public ConversationModel EnsureDirect(string petnameOrUsername)
    {
        var contact = _chat.Contacts?.Find(petnameOrUsername);
        var title = contact?.Petname ?? petnameOrUsername;
        var target = contact?.Username ?? petnameOrUsername;

        var existing = Directs.FirstOrDefault(c =>
            string.Equals(c.Title, title, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(c.Target, target, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
            return existing;

        var conversation = new ConversationModel
        {
            Kind = ConversationKind.Direct,
            Key = $"d:{title}",
            Title = title,
            Target = target,
            Subtitle = contact != null
                ? contact.Verified ? "pinned · verified" : "pinned · unverified"
                : "unpinned — add a contact to pin the key",
            LastActivity = DateTimeOffset.Now
        };
        Directs.Add(conversation);
        return conversation;
    }

    public ConversationModel EnsureGroup(Guid groupId, string name, bool isOwner, int memberCount)
    {
        var existing = Groups.FirstOrDefault(c => c.GroupId == groupId);
        if (existing != null)
            return existing;

        var conversation = new ConversationModel
        {
            Kind = ConversationKind.Group,
            Key = $"g:{groupId}",
            GroupId = groupId,
            Title = string.IsNullOrEmpty(name) ? groupId.ToString("N")[..8] : name,
            IsOwner = isOwner,
            Subtitle = $"{(isOwner ? "owner" : "member")} · {memberCount} members",
            LastActivity = DateTimeOffset.Now
        };
        Groups.Add(conversation);
        return conversation;
    }

    public void RefreshGroups()
    {
        foreach (var group in _chat.GetGroups())
        {
            var conversation = EnsureGroup(group.GroupId, group.Name, group.IsOwner, group.Members.Count);
            conversation.Subtitle = $"{(group.IsOwner ? "owner" : "member")} · {group.Members.Count} members";
        }
    }

    public void RemoveGroup(ConversationModel conversation)
    {
        Groups.Remove(conversation);
    }

    public void AddEvent(ConversationModel conversation, string text)
    {
        var entry = new MessageModel
        {
            Id = Guid.NewGuid(),
            Sender = "⚠",
            Content = text,
            At = DateTimeOffset.Now,
            IsEvent = true
        };
        conversation.Messages.Add(entry);
        conversation.LastActivity = entry.At;
        MessageAppended?.Invoke(conversation);
        SaveSoon();
    }

    public async Task<SendResult> SendDirectAsync(ConversationModel conversation, string content)
    {
        var entry = new MessageModel
        {
            Id = Guid.NewGuid(),
            Sender = _localUser,
            Content = Sanitize(content),
            At = DateTimeOffset.Now,
            Outgoing = true,
            Status = MessageStatus.Sending
        };
        conversation.Messages.Add(entry);
        conversation.LastActivity = entry.At;
        MessageAppended?.Invoke(conversation);
        SaveSoon();

        SendResult result;
        try
        {
            result = await _chat.SendMessageAsync(conversation.Target, content);
        }
        catch (Exception ex)
        {
            result = new SendResult(false, null, ex.Message);
        }

        entry.Status = result.Success ? MessageStatus.Sent : MessageStatus.Failed;
        if (result is { Success: true, MessageId: { } id })
            _outgoingById[id] = entry;
        return result;
    }

    public async Task<int> SendGroupAsync(ConversationModel conversation, string content)
    {
        if (conversation.GroupId is not { } groupId)
            return 0;

        var entry = new MessageModel
        {
            Id = Guid.NewGuid(),
            Sender = _localUser,
            Content = Sanitize(content),
            At = DateTimeOffset.Now,
            Outgoing = true,
            Status = MessageStatus.Sending
        };
        conversation.Messages.Add(entry);
        conversation.LastActivity = entry.At;
        MessageAppended?.Invoke(conversation);
        SaveSoon();

        var delivered = 0;
        try
        {
            delivered = await _chat.SendGroupMessageAsync(groupId, content);
        }
        catch
        {
        }

        entry.Status = delivered > 0 ? MessageStatus.Sent : MessageStatus.Failed;
        return delivered;
    }

    public void SyncTransfers()
    {
        foreach (var info in _chat.GetActiveFileTransfers())
        {
            var existing = Transfers.FirstOrDefault(t => t.TransferId == info.TransferId);
            if (existing == null)
            {
                Transfers.Insert(0, new TransferModel
                {
                    TransferId = info.TransferId,
                    FileName = info.FileName,
                    FileSize = info.FileSize,
                    Incoming = info.Direction == TransferDirection.Incoming,
                    Status = info.Status,
                    Percent = info.ChunkCount > 0 ? info.ChunksTransferred * 100.0 / info.ChunkCount : 0
                });
            }
            else
            {
                existing.Status = info.Status;
                if (info.ChunkCount > 0)
                    existing.Percent = info.ChunksTransferred * 100.0 / info.ChunkCount;
            }
        }
        TransfersChanged?.Invoke();
    }

    private Task HandleMessageAsync(ReceivedMessage message)
    {
        Dispatcher.UIThread.Post(() => AppendIncomingDirect(message, countUnread: true));
        return Task.CompletedTask;
    }

    private Task HandleAckAsync(Guid messageId)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_outgoingById.TryGetValue(messageId, out var entry))
            {
                entry.Status = MessageStatus.Acknowledged;
                SaveSoon();
            }
        });
        return Task.CompletedTask;
    }

    public bool HistoryEnabled => _history?.Enabled == true;

    public long HistorySizeBytes => _history?.SizeBytes ?? 0;

    public bool HistoryAvailable => _history != null;

    public void EnableHistory()
    {
        if (_history == null || _history.Enabled)
            return;
        _history.Enable(SnapshotList());
        HistoryStateChanged?.Invoke();
    }

    public void DisableHistory()
    {
        if (_history is not { Enabled: true })
            return;
        _history.Disable();
        HistoryStateChanged?.Invoke();
    }

    private void SaveSoon()
    {
        if (_history?.Enabled == true)
            _saveTimer?.Change(2000, Timeout.Infinite);
    }

    private void OnSaveTick()
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_history?.Enabled == true)
            {
                try
                {
                    _history.Save(SnapshotList());
                }
                catch
                {
                }
            }
        });
    }

    private System.Collections.Generic.List<ConversationModel> SnapshotList()
        => Directs.Concat(Groups).ToList();

    private Task HandleGroupMessageAsync(ReceivedGroupMessage message)
    {
        Dispatcher.UIThread.Post(() =>
        {
            var conversation = EnsureGroup(message.GroupId, message.GroupName, isOwner: false, memberCount: 0);
            RefreshGroups();
            AppendIncomingGroup(conversation, message, countUnread: true);
        });
        return Task.CompletedTask;
    }

    private Task HandleTransferRequestedAsync(FileTransferInfo info)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (Transfers.All(t => t.TransferId != info.TransferId))
            {
                Transfers.Insert(0, new TransferModel
                {
                    TransferId = info.TransferId,
                    FileName = info.FileName,
                    FileSize = info.FileSize,
                    Incoming = info.Direction == TransferDirection.Incoming,
                    Status = info.Status
                });
            }
            TransfersChanged?.Invoke();
        });
        return Task.CompletedTask;
    }

    private Task HandleTransferProgressAsync(TransferProgress progress)
    {
        Dispatcher.UIThread.Post(() =>
        {
            var transfer = Transfers.FirstOrDefault(t => t.TransferId == progress.TransferId);
            if (transfer == null)
                return;
            transfer.Status = TransferStatus.Transferring;
            transfer.Percent = progress.Percentage;
        });
        return Task.CompletedTask;
    }

    private Task HandleTransferCompletedAsync(CompletedTransfer completed)
    {
        Dispatcher.UIThread.Post(() =>
        {
            var transfer = Transfers.FirstOrDefault(t => t.TransferId == completed.TransferId);
            if (transfer is { Incoming: true })
            {
                try
                {
                    var path = GuiDownloads.Save(completed.FileName, completed.FileData);
                    transfer.Detail = path;
                }
                catch (Exception ex)
                {
                    transfer.Detail = $"could not save: {ex.Message}";
                }
            }
            if (transfer != null)
            {
                transfer.Status = TransferStatus.Completed;
                transfer.Percent = 100;
            }
            TransfersChanged?.Invoke();
        });
        return Task.CompletedTask;
    }

    private Task HandleTransferFailedAsync(Guid transferId, string reason)
    {
        Dispatcher.UIThread.Post(() =>
        {
            var transfer = Transfers.FirstOrDefault(t => t.TransferId == transferId);
            if (transfer == null)
                return;
            transfer.Status = TransferStatus.Failed;
            transfer.Detail = reason;
            TransfersChanged?.Invoke();
        });
        return Task.CompletedTask;
    }

    private void AppendIncomingDirect(ReceivedMessage message, bool countUnread)
    {
        var sender = message.SenderUsername ?? Convert.ToHexString(message.SenderPublicKey)[..16];
        var conversation = EnsureDirect(sender);
        if (conversation.Messages.Any(e => e.Id == message.MessageId))
            return;

        conversation.Messages.Add(new MessageModel
        {
            Id = message.MessageId,
            Sender = conversation.Title,
            Content = Sanitize(message.Content),
            At = message.ReceivedAt
        });
        conversation.LastActivity = message.ReceivedAt;
        if (countUnread && IsConversationVisible?.Invoke(conversation) != true)
            conversation.Unread++;
        MessageAppended?.Invoke(conversation);
        SaveSoon();
    }

    private void AppendIncomingGroup(ConversationModel conversation, ReceivedGroupMessage message, bool countUnread)
    {
        if (conversation.Messages.Any(e => e.Id == message.MessageId))
            return;

        var sender = message.SenderUsername ?? Convert.ToHexString(message.SenderPublicKey)[..16];
        conversation.Messages.Add(new MessageModel
        {
            Id = message.MessageId,
            Sender = sender,
            Content = Sanitize(message.Content),
            At = message.ReceivedAt
        });
        conversation.LastActivity = message.ReceivedAt;
        if (countUnread && IsConversationVisible?.Invoke(conversation) != true)
            conversation.Unread++;
        MessageAppended?.Invoke(conversation);
        SaveSoon();
    }

    private static string Sanitize(string content)
    {
        var chars = content.Where(c => !char.IsControl(c) || c == '\n' || c == '\t').ToArray();
        return new string(chars);
    }

    public void Dispose()
    {
        _chat.OnMessageReceived -= _onMessage;
        _chat.OnMessageAcknowledged -= _onAck;
        _chat.OnGroupMessageReceived -= _onGroupMessage;
        _chat.OnFileTransferRequested -= _onTransferRequested;
        _chat.OnFileTransferProgress -= _onTransferProgress;
        _chat.OnFileTransferCompleted -= _onTransferCompleted;
        _chat.OnFileTransferFailed -= _onTransferFailed;
        _saveTimer?.Dispose();
        if (_history?.Enabled == true)
        {
            try
            {
                _history.Save(SnapshotList());
            }
            catch
            {
            }
        }
    }
}
