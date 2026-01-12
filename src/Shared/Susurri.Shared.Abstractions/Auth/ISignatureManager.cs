using NSec.Cryptography;

namespace Susurri.Shared.Abstractions.Auth;

public interface ISignatureManager
{
    bool IsValid(byte[] data, byte[] signature, byte[] publicKey);
    byte[] Sign(byte[] data, Key privateKey);
}