using System.Security.Cryptography;
using NSec.Cryptography;

namespace Susurri.Modules.IAM.Core.Crypto;

public sealed class OnionCrypto : IOnionCrypto
{
    private const int PublicKeySize = 32;
    private const int NonceSize = 12;
    private const int MinCiphertextSize = 16;

    private static readonly AeadAlgorithm Aead = AeadAlgorithm.ChaCha20Poly1305;
    private static readonly KeyAgreementAlgorithm KeyExchange = KeyAgreementAlgorithm.X25519;

    public EncryptedEnvelope Encrypt(byte[] recipientPublicKey, byte[] plaintext)
    {
        ValidatePublicKey(recipientPublicKey);
        ArgumentNullException.ThrowIfNull(plaintext);

        using var ephemeralKey = Key.Create(KeyExchange);
        var ephemeralPublicKeyBytes = ephemeralKey.PublicKey.Export(KeyBlobFormat.RawPublicKey);

        var recipientPubKey = PublicKey.Import(KeyExchange, recipientPublicKey, KeyBlobFormat.RawPublicKey);

        using var sharedSecret = KeyAgreementAlgorithm.X25519.Agree(ephemeralKey, recipientPubKey);
        if (sharedSecret == null)
            throw new CryptographicException("Key agreement failed");

        using var symmetricKey = DeriveSymmetricKey(sharedSecret);

        var nonce = new byte[Aead.NonceSize];
        RandomNumberGenerator.Fill(nonce);

        var ciphertext = Aead.Encrypt(symmetricKey, nonce, null, plaintext);

        return new EncryptedEnvelope
        {
            EphemeralPublicKey = ephemeralPublicKeyBytes,
            Nonce = nonce,
            Ciphertext = ciphertext
        };
    }

    public byte[] Decrypt(Key recipientPrivateKey, EncryptedEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(recipientPrivateKey);
        ArgumentNullException.ThrowIfNull(envelope);
        ValidatePublicKey(envelope.EphemeralPublicKey);
        ValidateNonce(envelope.Nonce);
        ValidateCiphertext(envelope.Ciphertext);

        var ephemeralPubKey = PublicKey.Import(KeyExchange, envelope.EphemeralPublicKey, KeyBlobFormat.RawPublicKey);

        using var sharedSecret = KeyAgreementAlgorithm.X25519.Agree(recipientPrivateKey, ephemeralPubKey);
        if (sharedSecret == null)
            throw new CryptographicException("Key agreement failed");

        using var symmetricKey = DeriveSymmetricKey(sharedSecret);

        var plaintext = Aead.Decrypt(symmetricKey, envelope.Nonce, null, envelope.Ciphertext);
        if (plaintext == null)
            throw new CryptographicException("Decryption failed - authentication tag invalid");

        return plaintext;
    }

    public byte[] EncryptSymmetric(byte[] key, byte[] plaintext, byte[] nonce)
    {
        ValidateSymmetricKey(key);
        ArgumentNullException.ThrowIfNull(plaintext);
        ValidateNonce(nonce);

        using var symmetricKey = Key.Import(Aead, key, KeyBlobFormat.RawSymmetricKey);
        return Aead.Encrypt(symmetricKey, nonce, null, plaintext);
    }

    public byte[] DecryptSymmetric(byte[] key, byte[] ciphertext, byte[] nonce)
    {
        ValidateSymmetricKey(key);
        ValidateCiphertext(ciphertext);
        ValidateNonce(nonce);

        using var symmetricKey = Key.Import(Aead, key, KeyBlobFormat.RawSymmetricKey);
        var plaintext = Aead.Decrypt(symmetricKey, nonce, null, ciphertext);

        if (plaintext == null)
            throw new CryptographicException("Decryption failed - authentication tag invalid");

        return plaintext;
    }

    public byte[] GenerateSymmetricKey()
    {
        var key = new byte[32];
        RandomNumberGenerator.Fill(key);
        return key;
    }

    public byte[] GenerateNonce()
    {
        var nonce = new byte[Aead.NonceSize];
        RandomNumberGenerator.Fill(nonce);
        return nonce;
    }

    private static Key DeriveSymmetricKey(SharedSecret sharedSecret)
    {
        var keyDerivation = KeyDerivationAlgorithm.HkdfSha256;
        return keyDerivation.DeriveKey(
            sharedSecret,
            ReadOnlySpan<byte>.Empty,
            ReadOnlySpan<byte>.Empty,
            Aead,
            new KeyCreationParameters { ExportPolicy = KeyExportPolicies.AllowPlaintextExport });
    }

    private static void ValidatePublicKey(byte[] publicKey)
    {
        if (publicKey == null || publicKey.Length != PublicKeySize)
            throw new CryptographicException($"Invalid public key size: expected {PublicKeySize}, got {publicKey?.Length ?? 0}");
    }

    private static void ValidateSymmetricKey(byte[] key)
    {
        if (key == null || key.Length != 32)
            throw new CryptographicException($"Invalid symmetric key size: expected 32, got {key?.Length ?? 0}");
    }

    private static void ValidateNonce(byte[] nonce)
    {
        if (nonce == null || nonce.Length != NonceSize)
            throw new CryptographicException($"Invalid nonce size: expected {NonceSize}, got {nonce?.Length ?? 0}");
    }

    private static void ValidateCiphertext(byte[] ciphertext)
    {
        if (ciphertext == null || ciphertext.Length < MinCiphertextSize)
            throw new CryptographicException($"Invalid ciphertext: minimum size is {MinCiphertextSize}");
    }
}
