using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using Susurri.GUI.Services;
using Susurri.Modules.DHT.Core.Onion.GroupChat;

namespace Susurri.GUI.ViewModels;

public class ShellViewModel : ViewModelBase, IDisposable
{
    private readonly AppSession _session;
    private readonly Action _onLoggedOut;
    private readonly DispatcherTimer _timer;

    private string _section = "chats";
    private ConversationModel? _selectedConversation;
    private ConversationModel? _selectedGroup;
    private ConversationModel? _selectedDirect;
    private string _messageText = string.Empty;
    private string _newChatName = string.Empty;
    private string _newGroupName = string.Empty;
    private string _joinCode = string.Empty;
    private string _inviteUsername = string.Empty;
    private string _inviteCode = string.Empty;
    private string _chatError = string.Empty;
    private int _peers;
    private int _relays;
    private bool _isConnected;
    private int _pinnedCount;
    private int _verifiedCount;

    public ShellViewModel(AppSession session, Action onLoggedOut, bool autoSelectConversation = true)
    {
        _session = session;
        _onLoggedOut = onLoggedOut;

        Store = session.Conversations ?? throw new InvalidOperationException("Not logged in.");
        Store.IsConversationVisible = c => IsChats && ReferenceEquals(c, SelectedConversation);
        Store.MessageAppended += OnMessageAppended;
        Store.HistoryStateChanged += OnHistoryStateChanged;

        Contacts = new ContactsViewModel(session);
        Transfers = new TransfersViewModel(session, Store);

        SendCommand = new RelayCommand(() => _ = SendAsync());
        StartChatCommand = new RelayCommand(StartChat);
        CreateGroupCommand = new RelayCommand(CreateGroup);
        JoinGroupCommand = new RelayCommand(JoinGroup);
        InviteCommand = new RelayCommand(() => _ = InviteAsync());
        RotateCommand = new RelayCommand(() => _ = RotateAsync());
        LeaveCommand = new RelayCommand(Leave);
        CloseConversationCommand = new RelayCommand(() => SelectConversation(null));
        LogoutCommand = new RelayCommand(() => _ = LogoutAsync());
        ShowChatsCommand = new RelayCommand(() => Section = "chats");
        ShowContactsCommand = new RelayCommand(() => Section = "contacts");
        ShowTransfersCommand = new RelayCommand(() => Section = "transfers");
        ShowNodeCommand = new RelayCommand(() => Section = "node");

        UpdateNetwork();
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _timer.Tick += (_, _) => UpdateNetwork();
        _timer.Start();

        if (autoSelectConversation)
            SelectConversation(Store.Groups.FirstOrDefault() ?? Store.Directs.FirstOrDefault());
    }

    public ConversationStore Store { get; }
    public ContactsViewModel Contacts { get; }
    public TransfersViewModel Transfers { get; }

    public RelayCommand SendCommand { get; }
    public RelayCommand StartChatCommand { get; }
    public RelayCommand CreateGroupCommand { get; }
    public RelayCommand JoinGroupCommand { get; }
    public RelayCommand InviteCommand { get; }
    public RelayCommand RotateCommand { get; }
    public RelayCommand LeaveCommand { get; }
    public RelayCommand CloseConversationCommand { get; }
    public RelayCommand LogoutCommand { get; }
    public RelayCommand ShowChatsCommand { get; }
    public RelayCommand ShowContactsCommand { get; }
    public RelayCommand ShowTransfersCommand { get; }
    public RelayCommand ShowNodeCommand { get; }

    public event Action? ScrollToEndRequested;

    public ObservableCollection<ConversationModel> Directs => Store.Directs;
    public ObservableCollection<ConversationModel> Groups => Store.Groups;

    public string Section
    {
        get => _section;
        set
        {
            if (!SetField(ref _section, value))
                return;
            OnPropertyChanged(nameof(IsChats));
            OnPropertyChanged(nameof(IsContacts));
            OnPropertyChanged(nameof(IsTransfers));
            OnPropertyChanged(nameof(IsNode));
            OnPropertyChanged(nameof(IsConversationOpen));
            if (IsChats && SelectedConversation is { } conversation)
                conversation.Unread = 0;
        }
    }

    public bool IsChats => _section == "chats";
    public bool IsContacts => _section == "contacts";
    public bool IsTransfers => _section == "transfers";
    public bool IsNode => _section == "node";

    public ConversationModel? SelectedGroup
    {
        get => _selectedGroup;
        set
        {
            if (!SetField(ref _selectedGroup, value))
                return;
            if (value != null)
            {
                SelectedDirect = null;
                SelectedConversation = value;
            }
        }
    }

    public ConversationModel? SelectedDirect
    {
        get => _selectedDirect;
        set
        {
            if (!SetField(ref _selectedDirect, value))
                return;
            if (value != null)
            {
                SelectedGroup = null;
                SelectedConversation = value;
            }
        }
    }

    public ConversationModel? SelectedConversation
    {
        get => _selectedConversation;
        private set
        {
            if (!SetField(ref _selectedConversation, value))
                return;
            if (value != null)
                value.Unread = 0;
            InviteCode = string.Empty;
            ChatError = string.Empty;
            OnPropertyChanged(nameof(HasSelection));
            OnPropertyChanged(nameof(IsConversationOpen));
            OnPropertyChanged(nameof(SelectedIsGroup));
            OnPropertyChanged(nameof(SelectedIsOwnedGroup));
            OnPropertyChanged(nameof(SelectedIsDirect));
            OnPropertyChanged(nameof(ComposerPrompt));
            ScrollToEndRequested?.Invoke();
        }
    }

    private void SelectConversation(ConversationModel? conversation)
    {
        if (conversation == null)
        {
            SelectedGroup = null;
            SelectedDirect = null;
            SelectedConversation = null;
        }
        else if (conversation.IsGroup)
        {
            SelectedGroup = conversation;
        }
        else
        {
            SelectedDirect = conversation;
        }
    }

    public bool HasSelection => _selectedConversation != null;
    public bool IsConversationOpen => IsChats && _selectedConversation != null;
    public bool SelectedIsGroup => _selectedConversation?.IsGroup == true;
    public bool SelectedIsOwnedGroup => _selectedConversation is { IsGroup: true, IsOwner: true };
    public bool SelectedIsDirect => _selectedConversation is { IsGroup: false };
    public string ComposerPrompt => _selectedConversation == null
        ? "▸"
        : _selectedConversation.IsGroup ? $"▸ ◆{_selectedConversation.Title}" : $"▸ @{_selectedConversation.Title}";

    public string MessageText
    {
        get => _messageText;
        set => SetField(ref _messageText, value);
    }

    public string NewChatName
    {
        get => _newChatName;
        set => SetField(ref _newChatName, value);
    }

    public string NewGroupName
    {
        get => _newGroupName;
        set => SetField(ref _newGroupName, value);
    }

    public string JoinCode
    {
        get => _joinCode;
        set => SetField(ref _joinCode, value);
    }

    public string InviteUsername
    {
        get => _inviteUsername;
        set => SetField(ref _inviteUsername, value);
    }

    public string InviteCode
    {
        get => _inviteCode;
        private set
        {
            if (SetField(ref _inviteCode, value))
                OnPropertyChanged(nameof(HasInviteCode));
        }
    }

    public bool HasInviteCode => !string.IsNullOrEmpty(_inviteCode);

    public string ChatError
    {
        get => _chatError;
        private set
        {
            if (SetField(ref _chatError, value))
                OnPropertyChanged(nameof(HasChatError));
        }
    }

    public bool HasChatError => !string.IsNullOrEmpty(_chatError);

    public int Peers
    {
        get => _peers;
        private set => SetField(ref _peers, value);
    }

    public int Relays
    {
        get => _relays;
        private set => SetField(ref _relays, value);
    }

    public bool IsConnected
    {
        get => _isConnected;
        private set
        {
            if (SetField(ref _isConnected, value))
                OnPropertyChanged(nameof(ConnectionText));
        }
    }

    public string ConnectionText => _isConnected ? "online — onion route active" : "offline";

    public int PinnedCount
    {
        get => _pinnedCount;
        private set => SetField(ref _pinnedCount, value);
    }

    public int VerifiedCount
    {
        get => _verifiedCount;
        private set => SetField(ref _verifiedCount, value);
    }

    public string UserText => $"{_session.Username}";
    public string PortText => _session.Chat != null ? $":{_session.Chat.LocalPort}" : string.Empty;

    public string FingerprintShort
    {
        get
        {
            var chat = _session.Chat;
            if (chat == null)
                return string.Empty;
            var hex = Convert.ToHexString(chat.LocalPublicKey).ToLowerInvariant();
            return $"{hex[..4]} {hex[4..8]} … {hex[^4..]}";
        }
    }

    public string FingerprintFull
    {
        get
        {
            var chat = _session.Chat;
            if (chat == null)
                return string.Empty;
            var hex = Convert.ToHexString(chat.LocalPublicKey).ToLowerInvariant();
            return string.Join(' ', Enumerable.Range(0, hex.Length / 8).Select(i => hex.Substring(i * 8, 8)));
        }
    }

    public bool HistoryAvailable => Store.HistoryAvailable;

    public bool HistoryEnabled
    {
        get => Store.HistoryEnabled;
        set
        {
            if (value == Store.HistoryEnabled)
                return;
            if (value)
                Store.EnableHistory();
            else
                Store.DisableHistory();
        }
    }

    public string HistoryStatusText
    {
        get
        {
            if (!Store.HistoryAvailable)
                return "unavailable — no local store key for this identity";
            if (!Store.HistoryEnabled)
                return "off — conversations live in RAM and are forgotten on close";
            var kb = Store.HistorySizeBytes / 1024.0;
            return $"on — AES-256-GCM, key derived from your passphrase · {kb:0.0} KB on disk";
        }
    }

    public string HistoryShortText => Store.HistoryEnabled ? "encrypted" : "ram only";

    private void OnHistoryStateChanged()
    {
        OnPropertyChanged(nameof(HistoryEnabled));
        OnPropertyChanged(nameof(HistoryStatusText));
        OnPropertyChanged(nameof(HistoryShortText));
    }

    public string VersionText
    {
        get
        {
            var version = typeof(ShellViewModel).Assembly.GetName().Version;
            return version == null ? "dev" : $"v{version.ToString(3)}";
        }
    }

    public string DownloadsText => System.IO.Path.Combine("Downloads", "susurri");

    public string SeedsText
    {
        get
        {
            var seeds = _session.Seeds();
            return seeds.Count == 0
                ? "(none — set DHT__BootstrapNodes__0 before login)"
                : string.Join(Environment.NewLine, seeds);
        }
    }

    private void OnMessageAppended(ConversationModel conversation)
    {
        if (IsChats && ReferenceEquals(conversation, SelectedConversation))
        {
            conversation.Unread = 0;
            ScrollToEndRequested?.Invoke();
        }
    }

    private async Task SendAsync()
    {
        var conversation = SelectedConversation;
        var text = MessageText.Trim();
        if (conversation == null || text.Length == 0)
            return;

        MessageText = string.Empty;
        ChatError = string.Empty;

        if (conversation.IsGroup)
        {
            var delivered = await Store.SendGroupAsync(conversation, text);
            if (delivered == 0)
                ChatError = "Delivered to 0 members — everyone may be offline.";
        }
        else
        {
            var result = await Store.SendDirectAsync(conversation, text);
            if (!result.Success)
                ChatError = result.Error ?? "Send failed.";
        }
    }

    private void StartChat()
    {
        var name = NewChatName.Trim();
        if (name.Length == 0)
            return;
        NewChatName = string.Empty;
        var conversation = Store.EnsureDirect(name);
        Section = "chats";
        SelectConversation(conversation);
    }

    private void CreateGroup()
    {
        var chat = _session.Chat;
        var name = NewGroupName.Trim();
        if (chat == null || name.Length == 0)
            return;

        try
        {
            var group = chat.CreateGroup(name);
            NewGroupName = string.Empty;
            Store.RefreshGroups();
            var conversation = Store.Groups.FirstOrDefault(c => c.GroupId == group.GroupId);
            if (conversation != null)
            {
                Store.AddEvent(conversation, "GROUP CREATED — issue invites from the header");
                Section = "chats";
                SelectConversation(conversation);
            }
        }
        catch (Exception ex)
        {
            ChatError = ex.Message;
        }
    }

    private void JoinGroup()
    {
        var chat = _session.Chat;
        var code = JoinCode.Trim();
        if (chat == null || code.Length == 0)
            return;

        try
        {
            var (name, key, ownerSigningKey) = GroupInvite.Decode(code);
            var group = chat.JoinGroup(key, name, ownerSigningKey);
            if (group == null)
            {
                ChatError = "Could not join — the invite is not addressed to your identity.";
                return;
            }
            JoinCode = string.Empty;
            Store.RefreshGroups();
            var conversation = Store.Groups.FirstOrDefault(c => c.GroupId == group.GroupId);
            if (conversation != null)
            {
                Store.AddEvent(conversation, "JOINED — group key received · owner identity pinned");
                Section = "chats";
                SelectConversation(conversation);
            }
        }
        catch (Exception ex)
        {
            ChatError = $"Invalid invite code: {ex.Message}";
        }
    }

    private async Task InviteAsync()
    {
        var chat = _session.Chat;
        var conversation = SelectedConversation;
        var username = InviteUsername.Trim();
        if (chat == null || conversation?.GroupId is not { } groupId || username.Length == 0)
            return;

        ChatError = string.Empty;
        try
        {
            var record = await chat.GetPublicKeyAsync(username);
            if (record == null)
            {
                ChatError = $"No DHT record found for @{username}.";
                return;
            }
            var group = chat.GetGroup(groupId);
            if (group == null)
                return;
            var wrapped = chat.InviteMember(groupId, record.EncryptionPublicKey);
            InviteCode = GroupInvite.Encode(group.Name, wrapped, group.OwnerSigningPublicKey);
            InviteUsername = string.Empty;
            Store.AddEvent(conversation, $"INVITE ISSUED for @{username} — send them the code over a channel you trust");
            Store.RefreshGroups();
        }
        catch (Exception ex)
        {
            ChatError = ex.Message;
        }
    }

    private async Task RotateAsync()
    {
        var chat = _session.Chat;
        var conversation = SelectedConversation;
        if (chat == null || conversation?.GroupId is not { } groupId)
            return;

        ChatError = string.Empty;
        try
        {
            var delivered = await chat.RotateGroupKeyAsync(groupId);
            Store.AddEvent(conversation, $"GROUP KEY ROTATED — new key delivered to {delivered} members · owner-signed · sender chains reset");
            Store.RefreshGroups();
        }
        catch (Exception ex)
        {
            ChatError = ex.Message;
        }
    }

    private void Leave()
    {
        var chat = _session.Chat;
        var conversation = SelectedConversation;
        if (chat == null || conversation?.GroupId is not { } groupId)
            return;

        try
        {
            chat.LeaveGroup(groupId);
            Store.RemoveGroup(conversation);
            SelectConversation(Store.Groups.FirstOrDefault() ?? Store.Directs.FirstOrDefault());
        }
        catch (Exception ex)
        {
            ChatError = ex.Message;
        }
    }

    public async Task SendFileAsync(string path)
    {
        var chat = _session.Chat;
        var conversation = SelectedConversation;
        if (chat == null || conversation == null || conversation.IsGroup)
            return;

        ChatError = string.Empty;
        try
        {
            var result = await chat.SendFileAsync(conversation.Target, path);
            Store.SyncTransfers();
            if (result.Success)
                Store.AddEvent(conversation, $"FILE OFFERED — {System.IO.Path.GetFileName(path)} · waiting for the recipient to accept");
            else
                ChatError = result.Error ?? "File offer failed.";
        }
        catch (Exception ex)
        {
            ChatError = ex.Message;
        }
    }

    private void UpdateNetwork()
    {
        var chat = _session.Chat;
        if (chat == null)
            return;
        Peers = chat.PeerCount;
        Relays = chat.ActiveRelays;
        IsConnected = chat.IsConnected;

        var contacts = chat.Contacts?.All();
        PinnedCount = contacts?.Count ?? 0;
        VerifiedCount = contacts?.Count(c => c.Verified) ?? 0;
    }

    private async Task LogoutAsync()
    {
        Dispose();
        await _session.LogoutAsync();
        _onLoggedOut();
    }

    public void Dispose()
    {
        _timer.Stop();
        Store.MessageAppended -= OnMessageAppended;
        Store.HistoryStateChanged -= OnHistoryStateChanged;
        Store.IsConversationVisible = null;
    }
}
