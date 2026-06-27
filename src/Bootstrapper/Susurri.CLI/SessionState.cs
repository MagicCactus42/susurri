using Susurri.Modules.DHT.Core.Kademlia;

namespace Susurri.CLI;

/// <summary>
/// Mutable per-CLI-process session: login identity and the running DHT node.
/// Replaces the static fields previously living on Program.cs.
/// </summary>
internal sealed class SessionState : IAsyncDisposable
{
    public string? CurrentUser { get; private set; }
    public bool IsLoggedIn => CurrentUser != null;

    public KademliaDhtNode? DhtNode { get; private set; }
    public CancellationTokenSource? DhtCts { get; private set; }

    public void SetLoggedIn(string username)
    {
        CurrentUser = username;
    }

    public void SetLoggedOut()
    {
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
