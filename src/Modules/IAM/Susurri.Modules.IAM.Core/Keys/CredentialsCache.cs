using System.Security.Cryptography;
using System.Text;
using Susurri.Modules.IAM.Core.Abstractions;

namespace Susurri.Modules.IAM.Core.Keys;

internal sealed class CredentialsCache : ICredentialsCache
{
    private const int SaltSize = 32;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int KeyDerivationIterations = 600_000;
    private const int MinPasswordLength = 8;

    private static readonly string CachePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Susurri", "secure_cache", "credentials.enc");

    public async Task SaveAsync(string username, string passphrase, string password)
    {
        ValidatePassword(password);
        ValidateUsername(username);

        if (string.IsNullOrEmpty(passphrase))
            throw new ArgumentException("Passphrase cannot be empty", nameof(passphrase));

        var salt = new byte[SaltSize];
        RandomNumberGenerator.Fill(salt);

        var nonce = new byte[NonceSize];
        RandomNumberGenerator.Fill(nonce);

        var derivedKey = DeriveKey(password, salt);
        var plainBytes = Encoding.UTF8.GetBytes($"{username}\n{passphrase}");

        try
        {
            using var aesGcm = new AesGcm(derivedKey, TagSize);
            var ciphertext = new byte[plainBytes.Length];
            var tag = new byte[TagSize];

            aesGcm.Encrypt(nonce, plainBytes, ciphertext, tag);

            var payload = new byte[SaltSize + NonceSize + TagSize + ciphertext.Length];
            Buffer.BlockCopy(salt, 0, payload, 0, SaltSize);
            Buffer.BlockCopy(nonce, 0, payload, SaltSize, NonceSize);
            Buffer.BlockCopy(tag, 0, payload, SaltSize + NonceSize, TagSize);
            Buffer.BlockCopy(ciphertext, 0, payload, SaltSize + NonceSize + TagSize, ciphertext.Length);

            var directory = Path.GetDirectoryName(CachePath)!;
            Directory.CreateDirectory(directory);
            SetSecureDirectoryPermissions(directory);

            await File.WriteAllBytesAsync(CachePath, payload);

            CryptographicOperations.ZeroMemory(ciphertext);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(derivedKey);
            CryptographicOperations.ZeroMemory(plainBytes);
        }
    }

    public (string Username, string Passphrase) Load(string password)
    {
        ValidatePassword(password);

        if (!File.Exists(CachePath))
            throw new InvalidOperationException("Credentials cache not found");

        var payload = File.ReadAllBytes(CachePath);

        if (payload.Length < SaltSize + NonceSize + TagSize + 1)
            throw new CryptographicException("Invalid cache file format");

        var salt = new byte[SaltSize];
        var nonce = new byte[NonceSize];
        var tag = new byte[TagSize];
        var ciphertext = new byte[payload.Length - SaltSize - NonceSize - TagSize];

        Buffer.BlockCopy(payload, 0, salt, 0, SaltSize);
        Buffer.BlockCopy(payload, SaltSize, nonce, 0, NonceSize);
        Buffer.BlockCopy(payload, SaltSize + NonceSize, tag, 0, TagSize);
        Buffer.BlockCopy(payload, SaltSize + NonceSize + TagSize, ciphertext, 0, ciphertext.Length);

        var derivedKey = DeriveKey(password, salt);
        var plainBytes = new byte[ciphertext.Length];

        try
        {
            using var aesGcm = new AesGcm(derivedKey, TagSize);
            aesGcm.Decrypt(nonce, ciphertext, tag, plainBytes);

            var plainText = Encoding.UTF8.GetString(plainBytes);
            var newlineIndex = plainText.IndexOf('\n');

            if (newlineIndex < 0)
                throw new CryptographicException("Corrupted cache file");

            return (plainText[..newlineIndex], plainText[(newlineIndex + 1)..]);
        }
        catch (CryptographicException)
        {
            throw new UnauthorizedAccessException("Invalid password or corrupted cache");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(derivedKey);
            CryptographicOperations.ZeroMemory(plainBytes);
            CryptographicOperations.ZeroMemory(ciphertext);
        }
    }

    public void Clear()
    {
        if (File.Exists(CachePath))
        {
            var fileInfo = new FileInfo(CachePath);
            var length = fileInfo.Length;

            using (var fs = new FileStream(CachePath, FileMode.Open, FileAccess.Write))
            {
                var zeros = new byte[length];
                fs.Write(zeros, 0, zeros.Length);
                fs.Flush();
            }

            File.Delete(CachePath);
        }
    }

    public bool Exists() => File.Exists(CachePath);

    private static byte[] DeriveKey(string password, byte[] salt)
    {
        using var pbkdf2 = new Rfc2898DeriveBytes(
            password,
            salt,
            KeyDerivationIterations,
            HashAlgorithmName.SHA256);
        return pbkdf2.GetBytes(32);
    }

    private static void ValidatePassword(string password)
    {
        if (string.IsNullOrEmpty(password))
            throw new ArgumentException("Password cannot be empty", nameof(password));

        if (password.Length < MinPasswordLength)
            throw new ArgumentException($"Password must be at least {MinPasswordLength} characters", nameof(password));
    }

    private static void ValidateUsername(string username)
    {
        if (string.IsNullOrEmpty(username))
            throw new ArgumentException("Username cannot be empty", nameof(username));

        if (username.Length > 64)
            throw new ArgumentException("Username cannot exceed 64 characters", nameof(username));

        if (username.Contains('\n') || username.Contains('\r'))
            throw new ArgumentException("Username cannot contain newline characters", nameof(username));
    }

    private static void SetSecureDirectoryPermissions(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            try
            {
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
            catch
            {
            }
        }
    }
}
