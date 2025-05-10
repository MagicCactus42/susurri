using System.Text;
using dotnetstandard_bip39;
using NSec.Cryptography;
using Susurri.Modules.IAM.Core.Abstractions;
using Susurri.Modules.IAM.Core.Exceptions;

namespace Susurri.Modules.IAM.Core.Keys;

internal sealed class KeyGenerator : IKeyGenerator
{
    public Key GenerateKeys(string passphrase)
    {
        // 1. Convert passphrase into deterministic entropy (SHA256 → 32 bytes)
        var passphraseBytes = Encoding.UTF8.GetBytes(passphrase);
        var entropyBytes = System.Security.Cryptography.SHA256.HashData(passphraseBytes); // 32 bytes

        // 2. Convert entropy to hex string (EntropyToMnemonic expects a hex string)
        var entropyHex = BitConverter.ToString(entropyBytes).Replace("-", "").ToLowerInvariant();

        // 3. Generate BIP39 mnemonic
        var bip = new BIP39();
        var mnemonicPhrase = bip.EntropyToMnemonic(entropyHex, BIP39Wordlist.English);

        // 4. Generate BIP39 seed (hex string), then convert it to byte[]
        var bip39SeedHex = bip.MnemonicToSeedHex(mnemonicPhrase, passphrase);
        byte[] bip39Seed = ConvertHexToBytes(bip39SeedHex);

        // 5. Take the first 32 bytes as the Ed25519 seed
        byte[] ed25519Seed = bip39Seed.Take(32).ToArray();

        // 6. Generate Ed25519 key pair
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