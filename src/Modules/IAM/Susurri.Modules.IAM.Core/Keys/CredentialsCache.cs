using System.Security.Cryptography;
using System.Text;
using Susurri.Modules.IAM.Core.Abstractions;

namespace Susurri.Modules.IAM.Core.Keys;

internal sealed class CredentialsCache : ICredentialsCache
{
    private static readonly string CachePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Susurri", "secure_cache", "keycache.dat");

    public async Task SaveAsync(string username, string passphrase, string pin)
    {
        ValidatePin(pin);

        var salt = RandomNumberGenerator.GetBytes(16);
        var iv = RandomNumberGenerator.GetBytes(16);
        var aesKey = DeriveKey(pin, salt);

        using var aes = Aes.Create();
        aes.Key = aesKey;
        aes.IV = iv;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;

        using var encryptor = aes.CreateEncryptor();

        var plainText = $"{username}\n{passphrase}";
        var plainBytes = Encoding.UTF8.GetBytes(plainText);

        var encrypted = encryptor.TransformFinalBlock(plainBytes, 0, plainBytes.Length);

        var fullPayload = salt.Concat(iv).Concat(encrypted).ToArray();

        Directory.CreateDirectory(Path.GetDirectoryName(CachePath)!);
        await File.WriteAllBytesAsync(CachePath, fullPayload);
    }

    public (string Username, string Passphrase) Load(string pin)
    {
        ValidatePin(pin);

        if (!File.Exists(CachePath))
            throw new InvalidOperationException("Key cache file not found.");

        var fullPayload = File.ReadAllBytes(CachePath);
        var salt = fullPayload[..16];
        var iv = fullPayload[16..32];
        var encrypted = fullPayload[32..];

        var aesKey = DeriveKey(pin, salt);

        using var aes = Aes.Create();
        aes.Key = aesKey;
        aes.IV = iv;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;

        try
        {
            using var decryptor = aes.CreateDecryptor();
            var decryptedBytes = decryptor.TransformFinalBlock(encrypted, 0, encrypted.Length);
            var decryptedText = Encoding.UTF8.GetString(decryptedBytes);

            var parts = decryptedText.Split('\n');
            if (parts.Length < 2)
                throw new InvalidOperationException("Cache file corrupted.");

            return (parts[0], parts[1]);
        }
        catch (CryptographicException)
        {
            throw new UnauthorizedAccessException("Invalid PIN.");
        }
    }

    public void Clear()
    {
        if (File.Exists(CachePath))
            File.Delete(CachePath);
    }

    private static byte[] DeriveKey(string pin, byte[] salt)
    {
        using var derive = new Rfc2898DeriveBytes(pin, salt, 100_000, HashAlgorithmName.SHA256);
        return derive.GetBytes(32);
    }

    private static void ValidatePin(string pin)
    {
        if (pin.Length != 4 || !int.TryParse(pin, out _))
            throw new ArgumentException("PIN must be exactly 4 digits.");
    }
}
