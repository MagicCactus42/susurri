using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using NSec.Cryptography;

namespace Susurri.CLI.Network;

internal enum AttestationStatus
{
    Verified,
    FingerprintMismatch,
    SignatureInvalid,
    KeyMismatch,
    Unreachable,
    Malformed
}

internal sealed record AttestationResult(string Endpoint, AttestationStatus Status, string? FingerprintShort);

internal static class BootstrapVerifier
{
    public static async Task<IReadOnlyList<AttestationResult>> VerifyPinnedAsync(
        IEnumerable<IPEndPoint> seeds, CancellationToken ct)
    {
        var results = new List<AttestationResult>();
        foreach (var seed in seeds)
        {
            var host = seed.Address.ToString();
            var pin = BootstrapRegistry.Match(host, seed.Port);
            if (pin == null)
                continue;
            results.Add(await VerifyOneAsync(pin, ct).ConfigureAwait(false));
        }
        return results;
    }

    private static async Task<AttestationResult> VerifyOneAsync(PinnedBootstrap pin, CancellationToken ct)
    {
        var endpoint = $"{pin.Host}:{pin.DhtPort}";
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(4) };
            var url = $"http://{pin.Host}:{pin.AttestPort}/attest";
            using var response = await client.GetAsync(url, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return new AttestationResult(endpoint, AttestationStatus.Unreachable, null);

            var doc = await response.Content.ReadFromJsonAsync<AttestDoc>(ct).ConfigureAwait(false);
            if (doc?.Fingerprint == null || doc.Signature == null || doc.SigningPublicKey == null)
                return new AttestationResult(endpoint, AttestationStatus.Malformed, null);

            var shortFp = doc.Fingerprint.Length >= 16 ? doc.Fingerprint[..16] : doc.Fingerprint;

            if (!string.Equals(doc.SigningPublicKey, pin.SigningPublicKey, StringComparison.OrdinalIgnoreCase))
                return new AttestationResult(endpoint, AttestationStatus.KeyMismatch, shortFp);

            if (!string.Equals(doc.Fingerprint, pin.Fingerprint, StringComparison.OrdinalIgnoreCase))
                return new AttestationResult(endpoint, AttestationStatus.FingerprintMismatch, shortFp);

            if (!VerifySignature(doc))
                return new AttestationResult(endpoint, AttestationStatus.SignatureInvalid, shortFp);

            return new AttestationResult(endpoint, AttestationStatus.Verified, shortFp);
        }
        catch
        {
            return new AttestationResult(endpoint, AttestationStatus.Unreachable, null);
        }
    }

    private static bool VerifySignature(AttestDoc doc)
    {
        try
        {
            var publicKey = PublicKey.Import(
                SignatureAlgorithm.Ed25519,
                Convert.FromHexString(doc.SigningPublicKey!),
                KeyBlobFormat.RawPublicKey);
            var signed = Encoding.UTF8.GetBytes($"{doc.Fingerprint}|{doc.Timestamp}");
            return SignatureAlgorithm.Ed25519.Verify(publicKey, signed, Convert.FromHexString(doc.Signature!));
        }
        catch
        {
            return false;
        }
    }

    private sealed class AttestDoc
    {
        public string? Fingerprint { get; set; }
        public string? SigningPublicKey { get; set; }
        public string? Signature { get; set; }
        public long Timestamp { get; set; }
    }
}
