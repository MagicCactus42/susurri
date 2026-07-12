using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NSec.Cryptography;
using Susurri.Shared.Abstractions.Health;

namespace Susurri.CLI.Network;

internal sealed record BootstrapConfigInputs(
    int Port,
    bool EnableRelay,
    bool EnableOfflineStorage,
    int StorageLimitMb,
    int MaxConnections,
    uint NetworkId);

internal sealed class NodeAttestation : IAttestationProvider
{
    public string NodeId { get; }
    public string SigningPublicKey { get; }
    public string EncryptionPublicKey { get; }
    public string Fingerprint { get; }
    public string FingerprintShort => Fingerprint[..16];
    public string Version { get; }
    public long Timestamp { get; }
    public string Signature { get; }

    private NodeAttestation(
        string nodeId, string signingPublicKey, string encryptionPublicKey,
        string fingerprint, string version, long timestamp, string signature)
    {
        NodeId = nodeId;
        SigningPublicKey = signingPublicKey;
        EncryptionPublicKey = encryptionPublicKey;
        Fingerprint = fingerprint;
        Version = version;
        Timestamp = timestamp;
        Signature = signature;
    }

    public static NodeAttestation Compute(
        Key signingKey,
        string nodeId,
        byte[] signingPublicKey,
        byte[] encryptionPublicKey,
        BootstrapConfigInputs inputs,
        long timestamp)
    {
        var version = Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "0.0.0";
        var signingHex = Convert.ToHexString(signingPublicKey).ToLowerInvariant();

        var manifest = string.Join('\n', new[]
        {
            "susurri-node-fp-v1",
            $"version={version}",
            $"network={inputs.NetworkId}",
            $"port={inputs.Port}",
            $"relay={inputs.EnableRelay}",
            $"offlineStore={inputs.EnableOfflineStorage}",
            $"storageLimitMB={inputs.StorageLimitMb}",
            $"maxConnections={inputs.MaxConnections}",
            $"signing={signingHex}",
            $"binary={HashBinary()}"
        });

        var fingerprint = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(manifest))).ToLowerInvariant();

        var signed = Encoding.UTF8.GetBytes($"{fingerprint}|{timestamp}");
        var signature = Convert.ToHexString(
            SignatureAlgorithm.Ed25519.Sign(signingKey, signed)).ToLowerInvariant();

        return new NodeAttestation(
            nodeId,
            signingHex,
            Convert.ToHexString(encryptionPublicKey).ToLowerInvariant(),
            fingerprint,
            version,
            timestamp,
            signature);
    }

    public string AttestationJson => JsonSerializer.Serialize(new
    {
        nodeId = NodeId,
        signingPublicKey = SigningPublicKey,
        encryptionPublicKey = EncryptionPublicKey,
        fingerprint = Fingerprint,
        fingerprintShort = FingerprintShort,
        version = Version,
        timestamp = Timestamp,
        signature = Signature
    });

    public void WriteToDisk()
    {
        try
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Susurri");
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "fingerprint.txt"), Fingerprint + Environment.NewLine);
            File.WriteAllText(Path.Combine(directory, "attestation.json"), AttestationJson + Environment.NewLine);
        }
        catch
        {
        }
    }

    private static string HashBinary()
    {
        try
        {
            var path = Environment.ProcessPath;
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
                return "unavailable";
            using var stream = File.OpenRead(path);
            return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        }
        catch
        {
            return "unavailable";
        }
    }
}
