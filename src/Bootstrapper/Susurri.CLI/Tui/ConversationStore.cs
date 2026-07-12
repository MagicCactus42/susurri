using Susurri.Modules.DHT.Core.Services;

namespace Susurri.CLI.Tui;

internal enum ConversationKind
{
    Direct,
    Group
}

internal sealed class ChatEntry
{
    public Guid Id { get; init; }
    public string Sender { get; init; } = string.Empty;
    public string Content { get; init; } = string.Empty;
    public DateTimeOffset At { get; init; }
    public bool Outgoing { get; init; }
    public MessageStatus Status { get; set; } = MessageStatus.Acknowledged;
}

internal sealed class Conversation
{
    public ConversationKind Kind { get; init; }
    public string Key { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public List<ChatEntry> Entries { get; } = new();
    public int Unread { get; set; }
    public DateTimeOffset LastActivity { get; set; }
}

internal sealed class ConversationStore : IDisposable
{
    private readonly ChatService _chat;
    private readonly string _localUser;
    private readonly HistoryStore? _history;
    private readonly Timer? _saveTimer;
    private readonly object _lock = new();
    private readonly Dictionary<string, Conversation> _conversations = new();
    private readonly Dictionary<Guid, ChatEntry> _outgoingById = new();

    private readonly Func<ReceivedMessage, Task> _onMessage;
    private readonly Func<Guid, Task> _onAck;
    private readonly Func<ReceivedGroupMessage, Task> _onGroupMessage;

    public event Action? Changed;

    public ConversationStore(ChatService chat, string localUser, HistoryStore? history = null)
    {
        _chat = chat;
        _localUser = localUser;
        _history = history;

        _onMessage = HandleMessageAsync;
        _onAck = HandleAckAsync;
        _onGroupMessage = HandleGroupMessageAsync;

        _chat.OnMessageReceived += _onMessage;
        _chat.OnMessageAcknowledged += _onAck;
        _chat.OnGroupMessageReceived += _onGroupMessage;

        Seed();

        if (_history != null)
            _saveTimer = new Timer(_ => SaveNow(), null, Timeout.Infinite, Timeout.Infinite);
    }

    private void Seed()
    {
        lock (_lock)
        {
            if (_history?.Enabled == true)
            {
                foreach (var conv in _history.Load())
                    _conversations[conv.Key] = conv;
            }

            foreach (var m in _chat.GetMessages())
                AddIncomingDirectLocked(m, countUnread: false);

            foreach (var g in _chat.GetGroups())
            {
                var conv = EnsureGroupLocked(g.GroupId, g.Name);
                foreach (var m in _chat.GetGroupMessages(g.GroupId))
                    AddIncomingGroupLocked(conv, m, countUnread: false);
            }
        }
    }

    public void SaveNow()
    {
        if (_history is not { Enabled: true })
            return;

        List<Conversation> snapshot;
        lock (_lock)
        {
            snapshot = _conversations.Values.Select(conv =>
            {
                var copy = new Conversation
                {
                    Kind = conv.Kind,
                    Key = conv.Key,
                    Title = conv.Title,
                    LastActivity = conv.LastActivity
                };
                copy.Entries.AddRange(conv.Entries);
                return copy;
            }).ToList();
        }

        try
        {
            _history.Save(snapshot);
        }
        catch
        {
        }
    }

    private void NotifyChanged()
    {
        Changed?.Invoke();
        if (_history?.Enabled == true)
            _saveTimer?.Change(2000, Timeout.Infinite);
    }

    public IReadOnlyList<Conversation> Snapshot()
    {
        lock (_lock)
        {
            return _conversations.Values
                .OrderBy(c => c.Kind)
                .ThenByDescending(c => c.LastActivity)
                .ToList();
        }
    }

    public List<ChatEntry> EntriesSnapshot(Conversation conv)
    {
        lock (_lock)
        {
            return conv.Entries.OrderBy(e => e.At).ToList();
        }
    }

    public Conversation EnsureDirect(string username)
    {
        Conversation conv;
        lock (_lock)
        {
            conv = EnsureDirectLocked(CanonicalName(username));
        }
        NotifyChanged();
        return conv;
    }

    private string CanonicalName(string username)
        => _chat.Contacts?.Find(username)?.Petname ?? username;

    public void MarkRead(Conversation conv)
    {
        var changed = false;
        lock (_lock)
        {
            if (conv.Unread > 0)
            {
                conv.Unread = 0;
                changed = true;
            }
        }
        if (changed)
            NotifyChanged();
    }

    public async Task<SendResult> SendDirectAsync(string username, string content)
    {
        var entry = new ChatEntry
        {
            Id = Guid.NewGuid(),
            Sender = _localUser,
            Content = TextMeasure.Sanitize(content),
            At = DateTimeOffset.Now,
            Outgoing = true,
            Status = MessageStatus.Sending
        };

        lock (_lock)
        {
            var conv = EnsureDirectLocked(CanonicalName(username));
            conv.Entries.Add(entry);
            conv.LastActivity = entry.At;
        }
        NotifyChanged();

        SendResult result;
        try
        {
            result = await _chat.SendMessageAsync(username, content).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            result = new SendResult(false, null, ex.Message);
        }

        lock (_lock)
        {
            entry.Status = result.Success ? MessageStatus.Sent : MessageStatus.Failed;
            if (result.MessageId is { } id)
                _outgoingById[id] = entry;
        }
        NotifyChanged();
        return result;
    }

    public async Task SendGroupAsync(Guid groupId, string content)
    {
        var group = _chat.GetGroup(groupId);
        if (group == null)
            return;

        var entry = new ChatEntry
        {
            Id = Guid.NewGuid(),
            Sender = _localUser,
            Content = TextMeasure.Sanitize(content),
            At = DateTimeOffset.Now,
            Outgoing = true,
            Status = MessageStatus.Sending
        };

        lock (_lock)
        {
            var conv = EnsureGroupLocked(groupId, group.Name);
            conv.Entries.Add(entry);
            conv.LastActivity = entry.At;
        }
        NotifyChanged();

        var delivered = 0;
        try
        {
            delivered = await _chat.SendGroupMessageAsync(groupId, content).ConfigureAwait(false);
        }
        catch
        {
        }

        lock (_lock)
        {
            entry.Status = delivered > 0 ? MessageStatus.Sent : MessageStatus.Failed;
        }
        NotifyChanged();
    }

    public void RefreshGroups()
    {
        lock (_lock)
        {
            foreach (var g in _chat.GetGroups())
                EnsureGroupLocked(g.GroupId, g.Name);
        }
        NotifyChanged();
    }

    private Task HandleMessageAsync(ReceivedMessage message)
    {
        lock (_lock)
        {
            AddIncomingDirectLocked(message, countUnread: true);
        }
        NotifyChanged();
        return Task.CompletedTask;
    }

    private Task HandleAckAsync(Guid messageId)
    {
        lock (_lock)
        {
            if (_outgoingById.TryGetValue(messageId, out var entry))
                entry.Status = MessageStatus.Acknowledged;
        }
        NotifyChanged();
        return Task.CompletedTask;
    }

    private Task HandleGroupMessageAsync(ReceivedGroupMessage message)
    {
        lock (_lock)
        {
            var conv = EnsureGroupLocked(message.GroupId, message.GroupName);
            AddIncomingGroupLocked(conv, message, countUnread: true);
        }
        NotifyChanged();
        return Task.CompletedTask;
    }

    private void AddIncomingDirectLocked(ReceivedMessage message, bool countUnread)
    {
        var sender = message.SenderUsername ?? Convert.ToHexString(message.SenderPublicKey)[..16];
        var conv = EnsureDirectLocked(sender);
        if (conv.Entries.Any(e => e.Id == message.MessageId))
            return;

        conv.Entries.Add(new ChatEntry
        {
            Id = message.MessageId,
            Sender = sender,
            Content = TextMeasure.Sanitize(message.Content),
            At = message.ReceivedAt,
            Outgoing = false
        });
        conv.LastActivity = message.ReceivedAt;
        if (countUnread)
            conv.Unread++;
    }

    private void AddIncomingGroupLocked(Conversation conv, ReceivedGroupMessage message, bool countUnread)
    {
        if (conv.Entries.Any(e => e.Id == message.MessageId))
            return;

        var sender = message.SenderUsername ?? Convert.ToHexString(message.SenderPublicKey)[..16];
        conv.Entries.Add(new ChatEntry
        {
            Id = message.MessageId,
            Sender = sender,
            Content = TextMeasure.Sanitize(message.Content),
            At = message.ReceivedAt,
            Outgoing = false
        });
        conv.LastActivity = message.ReceivedAt;
        if (countUnread)
            conv.Unread++;
    }

    private Conversation EnsureDirectLocked(string username)
    {
        var key = $"d:{username}";
        if (!_conversations.TryGetValue(key, out var conv))
        {
            conv = new Conversation
            {
                Kind = ConversationKind.Direct,
                Key = key,
                Title = username,
                LastActivity = DateTimeOffset.Now
            };
            _conversations[key] = conv;
        }
        return conv;
    }

    private Conversation EnsureGroupLocked(Guid groupId, string name)
    {
        var key = $"g:{groupId}";
        if (!_conversations.TryGetValue(key, out var conv))
        {
            conv = new Conversation
            {
                Kind = ConversationKind.Group,
                Key = key,
                Title = string.IsNullOrEmpty(name) ? groupId.ToString()[..8] : name,
                LastActivity = DateTimeOffset.Now
            };
            _conversations[key] = conv;
        }
        return conv;
    }

    public void Dispose()
    {
        _chat.OnMessageReceived -= _onMessage;
        _chat.OnMessageAcknowledged -= _onAck;
        _chat.OnGroupMessageReceived -= _onGroupMessage;
        _saveTimer?.Dispose();
        SaveNow();
    }
}
