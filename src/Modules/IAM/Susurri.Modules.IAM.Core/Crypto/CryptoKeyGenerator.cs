#nullable enable
using System.Security.Cryptography;
using System.Text;
using dotnetstandard_bip39;
using NSec.Cryptography;

namespace Susurri.Modules.IAM.Core.Crypto;

// Generates key pairs from passphrase using PBKDF2-SHA256 with random salt.
public sealed class CryptoKeyGenerator : ICryptoKeyGenerator
{
    public const int SaltSize = 32;
    private const int Pbkdf2Iterations = 600_000;
    private const int SeedSize = 64;

    public KeyPair GenerateKeyPair(string passphrase)
    {
        var salt = new byte[SaltSize];
        RandomNumberGenerator.Fill(salt);
        return GenerateKeyPair(passphrase, salt);
    }

    public KeyPair GenerateKeyPair(string passphrase, byte[] salt)
    {
        ValidateSalt(salt);
        var seed = DeriveSeed(passphrase, salt);
        try
        {
            return ImportKeysFromSeed(seed, salt);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(seed);
        }
    }

    public Key GenerateSigningKey(string passphrase, byte[] salt)
    {
        ValidateSalt(salt);
        var seed = DeriveSeed(passphrase, salt);
        try
        {
            return Key.Import(SignatureAlgorithm.Ed25519, seed[..32], KeyBlobFormat.RawPrivateKey);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(seed);
        }
    }

    public Key GenerateEncryptionKey(string passphrase, byte[] salt)
    {
        ValidateSalt(salt);
        var seed = DeriveSeed(passphrase, salt);
        try
        {
            return Key.Import(KeyAgreementAlgorithm.X25519, seed[32..64], KeyBlobFormat.RawPrivateKey);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(seed);
        }
    }

    public string GeneratePassphrase(int wordCount = 8)
    {
        if (wordCount < 6 || wordCount > 20)
            throw new ArgumentException("Word count must be between 6 and 20", nameof(wordCount));

        var entropy = new byte[32];
        RandomNumberGenerator.Fill(entropy);
        var entropyHex = Convert.ToHexString(entropy).ToLowerInvariant();

        var bip = new BIP39();
        var fullMnemonic = bip.EntropyToMnemonic(entropyHex, BIP39Wordlist.English);
        var allWords = fullMnemonic.Split(' ');

        return string.Join(" ", allWords.Take(wordCount));
    }

    public static KeyPair GenerateKeyPairLegacy(string passphrase)
    {
        var seed = LegacyDeriveBip39Seed(passphrase);
        try
        {
            return ImportKeysFromSeed(seed, derivationSalt: null);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(seed);
        }
    }

    private static byte[] DeriveSeed(string passphrase, byte[] salt)
    {
        return Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(passphrase),
            salt,
            Pbkdf2Iterations,
            HashAlgorithmName.SHA256,
            SeedSize);
    }

    private static byte[] LegacyDeriveBip39Seed(string passphrase)
    {
        var passphraseBytes = Encoding.UTF8.GetBytes(passphrase);
        var entropyBytes = SHA256.HashData(passphraseBytes);
        var entropyHex = Convert.ToHexString(entropyBytes).ToLowerInvariant();

        var bip = new BIP39();
        var mnemonicPhrase = bip.EntropyToMnemonic(entropyHex, BIP39Wordlist.English);
        var bip39SeedHex = bip.MnemonicToSeedHex(mnemonicPhrase, passphrase);
        return Convert.FromHexString(bip39SeedHex);
    }

    private static KeyPair ImportKeysFromSeed(byte[] seed, byte[]? derivationSalt)
    {
        var signingKey = Key.Import(
            SignatureAlgorithm.Ed25519,
            seed[..32],
            KeyBlobFormat.RawPrivateKey);

        var encryptionKey = Key.Import(
            KeyAgreementAlgorithm.X25519,
            seed[32..64],
            KeyBlobFormat.RawPrivateKey);

        return new KeyPair(signingKey, encryptionKey, derivationSalt);
    }

    private static void ValidateSalt(byte[] salt)
    {
        ArgumentNullException.ThrowIfNull(salt);
        if (salt.Length != SaltSize)
            throw new ArgumentException($"Salt must be exactly {SaltSize} bytes", nameof(salt));
    }
}
