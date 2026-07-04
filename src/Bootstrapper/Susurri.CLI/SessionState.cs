using Susurri.CLI.Tui;
using Susurri.Modules.DHT.Core.Kademlia;
using Susurri.Modules.DHT.Core.Services;

namespace Susurri.CLI;

/// <summary>
/// Mutable per-CLI-process session: the logged-in chat identity (a running
/// <see cref="ChatService"/>) and/or a headless DHT node started via
/// <c>dht start</c>.
/// </summary>
internal sealed class SessionState : IAsyncDisposable
{
    public string? CurrentUser { get; private set; }
    public bool IsLoggedIn => CurrentUser != null && Chat != null;

    public ChatService? Chat { get; private set; }
    public ConversationStore? Conversations { get; private set; }
    public HistoryStore? History { get; private set; }
    public volatile bool TuiActive;

    public KademliaDhtNode? DhtNode { get; private set; }
    public CancellationTokenSource? DhtCts { get; private set; }

    public void SetChat(string username, ChatService chat, ConversationStore conversations, HistoryStore? history = null)
    {
        CurrentUser = username;
        Chat = chat;
        Conversations = conversations;
        History = history;
    }

    public async Task ClearChatAsync()
    {
        Conversations?.Dispose();
        Conversations = null;
        History = null;
        if (Chat != null)
        {
            await Chat.DisposeAsync().ConfigureAwait(false);
            Chat = null;
        }
        CurrentUser = null;
    }

    public void SetDhtNode(KademliaDhtNode node, CancellationTokenSource cts)
    {
        DhtNode = node;
        DhtCts = cts;
    }

    public void ClearDhtNode()
    {
        DhtNode = null;
        DhtCts?.Dispose();
        DhtCts = null;
    }

    public async ValueTask DisposeAsync()
    {
        Conversations?.Dispose();
        Conversations = null;
        if (Chat != null)
        {
            await Chat.DisposeAsync().ConfigureAwait(false);
            Chat = null;
            CurrentUser = null;
        }

        if (DhtNode != null)
        {
            DhtCts?.Cancel();
            await DhtNode.DisposeAsync().ConfigureAwait(false);
            DhtCts?.Dispose();
            DhtNode = null;
            DhtCts = null;
        }
    }
}
