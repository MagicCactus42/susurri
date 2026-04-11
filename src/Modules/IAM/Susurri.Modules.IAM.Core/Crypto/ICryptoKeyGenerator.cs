using NSec.Cryptography;

namespace Susurri.Modules.IAM.Core.Crypto;

public interface ICryptoKeyGenerator
{
    /// <summary>
    /// Generates a key pair with a new random salt.
    /// The salt is stored in KeyPair.DerivationSalt for future re-derivation.
    /// </summary>
    KeyPair GenerateKeyPair(string passphrase);

    /// <summary>
    /// Generates a deterministic key pair from passphrase + stored salt.
    /// </summary>
    KeyPair GenerateKeyPair(string passphrase, byte[] salt);

    Key GenerateSigningKey(string passphrase, byte[] salt);
    Key GenerateEncryptionKey(string passphrase, byte[] salt);
    string GeneratePassphrase(int wordCount = 8);
}
