namespace Susurri.CLI.Network;

internal sealed record PinnedBootstrap(
    string Host,
    int DhtPort,
    int AttestPort,
    string Fingerprint,
    string SigningPublicKey);

internal static class BootstrapRegistry
{
    public static readonly IReadOnlyList<PinnedBootstrap> Pins = new PinnedBootstrap[]
    {
    };

    public static PinnedBootstrap? Match(string host, int dhtPort)
    {
        foreach (var pin in Pins)
        {
            if (pin.DhtPort == dhtPort &&
                string.Equals(pin.Host, host, StringComparison.OrdinalIgnoreCase))
                return pin;
        }
        return null;
    }
}
