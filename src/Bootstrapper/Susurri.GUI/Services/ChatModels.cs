using System;
using System.Collections.ObjectModel;
using Susurri.GUI.ViewModels;
using Susurri.Modules.DHT.Core.Services;

namespace Susurri.GUI.Services;

public enum ConversationKind
{
    Direct,
    Group
}

public sealed class MessageModel : ViewModelBase
{
    private MessageStatus _status = MessageStatus.Acknowledged;

    public Guid Id { get; init; }
    public string Sender { get; init; } = string.Empty;
    public string Content { get; init; } = string.Empty;
    public DateTimeOffset At { get; init; }
    public bool Outgoing { get; init; }
    public bool IsEvent { get; init; }

    public MessageStatus Status
    {
        get => _status;
        set
        {
            if (!SetField(ref _status, value))
                return;
            OnPropertyChanged(nameof(StatusGlyph));
            OnPropertyChanged(nameof(IsAcknowledged));
            OnPropertyChanged(nameof(IsFailed));
        }
    }

    public string Time => At.ToLocalTime().ToString("HH:mm");

    public string StatusGlyph => !Outgoing || IsEvent
        ? string.Empty
        : _status switch
        {
            MessageStatus.Sending => "⋯",
            MessageStatus.Sent => "✓",
            MessageStatus.Acknowledged => "✓✓",
            MessageStatus.Failed => "✗",
            _ => string.Empty
        };

    public bool IsAcknowledged => Outgoing && _status == MessageStatus.Acknowledged;
    public bool IsFailed => Outgoing && _status == MessageStatus.Failed;
}

public sealed class ConversationModel : ViewModelBase
{
    private string _title = string.Empty;
    private string _subtitle = string.Empty;
    private int _unread;
    private DateTimeOffset _lastActivity;

    public ConversationKind Kind { get; init; }
    public string Key { get; init; } = string.Empty;
    public Guid? GroupId { get; init; }
    public string Target { get; init; } = string.Empty;
    public bool IsOwner { get; init; }

    public ObservableCollection<MessageModel> Messages { get; } = new();

    public string Title
    {
        get => _title;
        set => SetField(ref _title, value);
    }

    public string Subtitle
    {
        get => _subtitle;
        set => SetField(ref _subtitle, value);
    }

    public int Unread
    {
        get => _unread;
        set
        {
            if (SetField(ref _unread, value))
                OnPropertyChanged(nameof(HasUnread));
        }
    }

    public bool HasUnread => _unread > 0;

    public DateTimeOffset LastActivity
    {
        get => _lastActivity;
        set => SetField(ref _lastActivity, value);
    }

    public bool IsGroup => Kind == ConversationKind.Group;
    public string Glyph => IsGroup ? "◆" : "@";
}

public sealed class TransferModel : ViewModelBase
{
    private TransferStatus _status;
    private double _percent;
    private string _detail = string.Empty;

    public Guid TransferId { get; init; }
    public string FileName { get; init; } = string.Empty;
    public long FileSize { get; init; }
    public bool Incoming { get; init; }

    public TransferStatus Status
    {
        get => _status;
        set
        {
            if (!SetField(ref _status, value))
                return;
            OnPropertyChanged(nameof(IsAwaitingAccept));
            OnPropertyChanged(nameof(StatusText));
            OnPropertyChanged(nameof(IsActive));
            OnPropertyChanged(nameof(IsFailed));
            OnPropertyChanged(nameof(IsCompleted));
        }
    }

    public double Percent
    {
        get => _percent;
        set => SetField(ref _percent, value);
    }

    public string Detail
    {
        get => _detail;
        set => SetField(ref _detail, value);
    }

    public bool IsAwaitingAccept => Incoming && _status == TransferStatus.Requesting;
    public bool IsActive => _status == TransferStatus.Transferring;
    public bool IsFailed => _status == TransferStatus.Failed;
    public bool IsCompleted => _status == TransferStatus.Completed;

    public string DirectionGlyph => Incoming ? "⇣" : "⇡";
    public string SizeText => FormatBytes(FileSize);
    public string IdShort => TransferId.ToString("N")[..8];

    public string StatusText => _status switch
    {
        TransferStatus.Requesting => Incoming ? "OFFER — ACCEPT?" : "OFFERED — WAITING",
        TransferStatus.Transferring => "TRANSFERRING",
        TransferStatus.Completed => "COMPLETED",
        TransferStatus.Failed => "FAILED",
        _ => string.Empty
    };

    public static string FormatBytes(long bytes)
    {
        if (bytes >= 1024L * 1024 * 1024)
            return $"{bytes / (1024.0 * 1024 * 1024):0.#} GB";
        if (bytes >= 1024L * 1024)
            return $"{bytes / (1024.0 * 1024):0.#} MB";
        if (bytes >= 1024)
            return $"{bytes / 1024.0:0.#} KB";
        return $"{bytes} B";
    }
}
