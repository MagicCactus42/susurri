using NSec.Cryptography;

namespace Susurri.Modules.IAM.Core.Crypto;

public interface ICryptoKeyGenerator
{
    KeyPair GenerateKeyPair(string passphrase);
    Key GenerateSigningKey(string passphrase);
    Key GenerateEncryptionKey(string passphrase);
    string GeneratePassphrase(int wordCount = 12);
}
