using NSec.Cryptography;

namespace Susurri.Modules.IAM.Core.Abstractions;

public interface IInMemoryCredentialsCache
{
    void Set(string username, string passphrase, byte[] publicKey);
    (string Passphrase, byte[] PublicKey, string Username)? Get();
    void Clear();
}