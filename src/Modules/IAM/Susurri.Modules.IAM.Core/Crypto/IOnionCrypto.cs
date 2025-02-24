using NSec.Cryptography;

namespace Susurri.Modules.IAM.Core.Crypto;

public interface IOnionCrypto
{
    EncryptedEnvelope Encrypt(byte[] recipientPublicKey, byte[] plaintext);
    byte[] Decrypt(Key recipientPrivateKey, EncryptedEnvelope envelope);
    byte[] EncryptSymmetric(byte[] key, byte[] plaintext, byte[] nonce);
    byte[] DecryptSymmetric(byte[] key, byte[] ciphertext, byte[] nonce);
    byte[] GenerateSymmetricKey();
    byte[] GenerateNonce();
}
