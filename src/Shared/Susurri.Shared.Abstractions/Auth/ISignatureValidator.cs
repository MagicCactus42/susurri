namespace Susurri.Shared.Abstractions.Auth;

public interface ISignatureValidator
{
    bool IsValid(byte[] signature, byte[] publicKey);
}