using System.Security.Cryptography;
using System.Text;
using dotnetstandard_bip39;
using NSec.Cryptography;
using Susurri.Modules.IAM.Core.Abstractions;
using Susurri.Modules.IAM.Core.Exceptions;
using Aes = System.Runtime.Intrinsics.X86.Aes;

namespace Susurri.Modules.IAM.Core.Keys;

internal sealed class KeyGenerator : IKeyGenerator
{
    public Key GenerateKeys(string passphrase)
    {
        var passphraseBytes = Encoding.UTF8.GetBytes(passphrase);
        var entropyBytes = System.Security.Cryptography.SHA256.HashData(passphraseBytes);
        var entropyHex = BitConverter.ToString(entropyBytes).Replace("-", "").ToLowerInvariant();

        var bip = new BIP39();
        var mnemonicPhrase = bip.EntropyToMnemonic(entropyHex, BIP39Wordlist.English);
        var bip39SeedHex = bip.MnemonicToSeedHex(mnemonicPhrase, passphrase);
        byte[] bip39Seed = ConvertHexToBytes(bip39SeedHex);

        byte[] ed25519Seed = bip39Seed.Take(32).ToArray();
        var algorithm = SignatureAlgorithm.Ed25519;
        var privateKey = Key.Import(algorithm, ed25519Seed, KeyBlobFormat.RawPrivateKey);

        return privateKey;
    }

    private static byte[] ConvertHexToBytes(string hex)
    {
        if (hex.Length % 2 != 0)
            throw new InvalidHexStringLengthException();

        return Enumerable.Range(0, hex.Length / 2)
            .Select(i => Convert.ToByte(hex.Substring(i * 2, 2), 16))
            .ToArray();
    }
}