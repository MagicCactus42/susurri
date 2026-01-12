using System.Security.Cryptography;
using System.Text;
using dotnetstandard_bip39;
using NSec.Cryptography;
using Susurri.Modules.IAM.Core.Exceptions;

namespace Susurri.Modules.IAM.Core.Crypto;

// Generates deterministic key pairs from passphrase using BIP39 derivation.
public sealed class CryptoKeyGenerator : ICryptoKeyGenerator
{
    public KeyPair GenerateKeyPair(string passphrase)
    {
        var bip39Seed = DeriveBip39Seed(passphrase);

        // Derive Ed25519 signing key from first 32 bytes
        var ed25519Seed = bip39Seed[..32];
        var signingKey = Key.Import(
            SignatureAlgorithm.Ed25519,
            ed25519Seed,
            KeyBlobFormat.RawPrivateKey);

        // Derive X25519 encryption key from bytes 32-64
        var x25519Seed = bip39Seed[32..64];
        var encryptionKey = Key.Import(
            KeyAgreementAlgorithm.X25519,
            x25519Seed,
            KeyBlobFormat.RawPrivateKey);

        return new KeyPair(signingKey, encryptionKey);
    }

    public Key GenerateSigningKey(string passphrase)
    {
        var bip39Seed = DeriveBip39Seed(passphrase);
        var ed25519Seed = bip39Seed[..32];

        return Key.Import(
            SignatureAlgorithm.Ed25519,
            ed25519Seed,
            KeyBlobFormat.RawPrivateKey);
    }

    public Key GenerateEncryptionKey(string passphrase)
    {
        var bip39Seed = DeriveBip39Seed(passphrase);
        var x25519Seed = bip39Seed[32..64];

        return Key.Import(
            KeyAgreementAlgorithm.X25519,
            x25519Seed,
            KeyBlobFormat.RawPrivateKey);
    }

    private static byte[] DeriveBip39Seed(string passphrase)
    {
        var passphraseBytes = Encoding.UTF8.GetBytes(passphrase);
        var entropyBytes = SHA256.HashData(passphraseBytes);
        var entropyHex = Convert.ToHexString(entropyBytes).ToLowerInvariant();

        var bip = new BIP39();
        var mnemonicPhrase = bip.EntropyToMnemonic(entropyHex, BIP39Wordlist.English);
        var bip39SeedHex = bip.MnemonicToSeedHex(mnemonicPhrase, passphrase);
        return ConvertHexToBytes(bip39SeedHex);
    }

    public string GeneratePassphrase(int wordCount = 12)
    {
        // BIP39 supports 12, 15, 18, 21, 24 words
        // 12 words = 128 bits entropy, 24 words = 256 bits entropy
        var validWordCounts = new[] { 12, 15, 18, 21, 24 };
        if (!validWordCounts.Contains(wordCount))
            throw new ArgumentException("Word count must be 12, 15, 18, 21, or 24", nameof(wordCount));

        // Calculate entropy bytes needed: wordCount * 11 bits / 8, minus checksum bits
        // For 12 words: 128 bits = 16 bytes
        // For 24 words: 256 bits = 32 bytes
        var entropyBits = wordCount * 11 - wordCount / 3;
        var entropyBytes = entropyBits / 8;

        var entropy = new byte[entropyBytes];
        RandomNumberGenerator.Fill(entropy);

        var entropyHex = Convert.ToHexString(entropy).ToLowerInvariant();

        var bip = new BIP39();
        return bip.EntropyToMnemonic(entropyHex, BIP39Wordlist.English);
    }

    private static byte[] ConvertHexToBytes(string hex)
    {
        if (hex.Length % 2 != 0)
            throw new InvalidHexStringLengthException();

        return Enumerable.Range(0, hex.Length / 2)
            .Select(i => Convert.ToByte(hex.Substring(i * 2, 2), 16))
            .ToArray();
    }
}
