using System.Security.Cryptography;
using System.Text;

namespace Susurri.CLI;

/// <summary>
/// Derives a stable, per-username salt so the same (username, passphrase) pair
/// reproduces the same X25519/Ed25519 identity on any device — the passphrase
/// is the account, there is nothing device-local to copy. The salt is not
/// secret; a per-username value just prevents a single rainbow table across all
/// users. Brute-force resistance comes from PBKDF2 600k over a high-entropy
/// BIP39 passphrase.
/// </summary>
internal static class Identity
{
    private const string Domain = "susurri-identity-v1:";

    public static byte[] DeriveSalt(string username)
        => SHA256.HashData(Encoding.UTF8.GetBytes(Domain + username.Trim().ToLowerInvariant()));
}
