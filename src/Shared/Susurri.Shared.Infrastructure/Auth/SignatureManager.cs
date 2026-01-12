using NSec.Cryptography;
using Susurri.Shared.Abstractions.Auth;
using Susurri.Shared.Infrastructure.Exceptions;

namespace Susurri.Shared.Infrastructure.Auth;
#nullable enable
public sealed class SignatureManager : ISignatureManager
{
    private static readonly SignatureAlgorithm Algorithm = SignatureAlgorithm.Ed25519;

    public bool IsValid(byte[]? signature, byte[]? publicKey, byte[]? data)
    {
        if (signature is null || publicKey is null || data is null)
            return false;

        try
        {
            var publicKeyObj = PublicKey.Import(Algorithm, publicKey, KeyBlobFormat.RawPublicKey);
            return Algorithm.Verify(publicKeyObj, data, signature);
        }
        catch
        {
            return false;
        }
    }

    public byte[] Sign(byte[] data, Key privateKey)
    {
        if (data is null)
            throw new InvalidSignatureData(nameof(data));
        if (privateKey is null)
            throw new InvalidSignatureData(nameof(privateKey));

        return Algorithm.Sign(privateKey, data);
    }
}