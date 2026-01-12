using NSec.Cryptography;

namespace Susurri.Modules.IAM.Core.Crypto;

// Ed25519 signing key + X25519 encryption key pair.
public sealed class KeyPair : IDisposable
{
    public Key SigningKey { get; }
    public Key EncryptionKey { get; }
    public byte[] SigningPublicKey { get; }
    public byte[] EncryptionPublicKey { get; }

    public KeyPair(Key signingKey, Key encryptionKey)
    {
        SigningKey = signingKey ?? throw new ArgumentNullException(nameof(signingKey));
        EncryptionKey = encryptionKey ?? throw new ArgumentNullException(nameof(encryptionKey));

        SigningPublicKey = signingKey.PublicKey.Export(KeyBlobFormat.RawPublicKey);
        EncryptionPublicKey = encryptionKey.PublicKey.Export(KeyBlobFormat.RawPublicKey);
    }

    public void Dispose()
    {
        SigningKey.Dispose();
        EncryptionKey.Dispose();
    }
}
