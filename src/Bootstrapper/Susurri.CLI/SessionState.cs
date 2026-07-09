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

    public KademliaDhtNode? DhtNode { get; private set; }
    public CancellationTokenSource? DhtCts { get; private set; }

    public void SetChat(string username, ChatService chat)
    {
        CurrentUser = username;
        Chat = chat;
    }

    public async Task ClearChatAsync()
    {
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
