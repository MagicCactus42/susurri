using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;
using NSec.Cryptography;
using Susurri.Shared.Abstractions.Security;

namespace Susurri.CLI.Network;

internal static class BootstrapIdentity
{
    private static readonly byte[] SignContext = Encoding.UTF8.GetBytes("susurri-bootstrap-identity-sign-v1");
    private static readonly byte[] EncContext = Encoding.UTF8.GetBytes("susurri-bootstrap-identity-enc-v1");

    public static (Key Signing, Key Encryption) Derive(IConfiguration config)
    {
        var seed = ResolveSeed(config);
        try
        {
            var signSeed = LocalEncryption.DeriveSubkey(seed, SignContext);
            var encSeed = LocalEncryption.DeriveSubkey(seed, EncContext);
            try
            {
                var signing = Key.Import(SignatureAlgorithm.Ed25519, signSeed, KeyBlobFormat.RawPrivateKey);
                var encryption = Key.Import(KeyAgreementAlgorithm.X25519, encSeed, KeyBlobFormat.RawPrivateKey);
                return (signing, encryption);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(signSeed);
                CryptographicOperations.ZeroMemory(encSeed);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(seed);
        }
    }

    private static byte[] ResolveSeed(IConfiguration config)
    {
        var configured = config["DHT:Bootstrap:IdentitySeed"]?.Trim();
        if (!string.IsNullOrWhiteSpace(configured))
        {
            if (configured.Length == 64 && configured.All(Uri.IsHexDigit))
                return Convert.FromHexString(configured);
            ConsoleUi.PrintWarning("DHT:Bootstrap:IdentitySeed must be 64 hex characters — falling back to the persisted seed.");
        }

        return LoadOrCreatePersistedSeed();
    }

    private static byte[] LoadOrCreatePersistedSeed()
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Susurri");
        Directory.CreateDirectory(directory);
        LocalEncryption.RestrictDirectory(directory);

        var path = Path.Combine(directory, "node-identity.seed");
        if (File.Exists(path))
        {
            var existing = File.ReadAllText(path).Trim();
            if (existing.Length == 64 && existing.All(Uri.IsHexDigit))
                return Convert.FromHexString(existing);
        }

        var seed = RandomNumberGenerator.GetBytes(32);
        File.WriteAllText(path, Convert.ToHexString(seed).ToLowerInvariant());
        if (!OperatingSystem.IsWindows())
        {
            try
            {
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
            catch
            {
            }
        }
        return seed;
    }
}
