#nullable enable
using System.Security.Cryptography;
using NSec.Cryptography;
using Susurri.Modules.IAM.Core.Abstractions;

namespace Susurri.Modules.IAM.Core.Keys;

public class KeyStorage : IKeyStorage
{
    private const int SaltSize = 32;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int KeyDerivationIterations = 600_000;

    private readonly string _filePath;
    private readonly string _directory;
    private readonly SignatureAlgorithm _signatureAlgorithm = SignatureAlgorithm.Ed25519;

    public KeyStorage()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        _directory = Path.Combine(appData, "Susurri");
        Directory.CreateDirectory(_directory);
        SetSecureDirectoryPermissions(_directory);
        _filePath = Path.Combine(_directory, "keys.enc");
    }

    public void Save(Key privateKey, string passphrase)
    {
        ArgumentNullException.ThrowIfNull(privateKey);
        ValidatePassphrase(passphrase);

        byte[] rawKey = privateKey.Export(KeyBlobFormat.RawPrivateKey);

        try
        {
            var salt = new byte[SaltSize];
            RandomNumberGenerator.Fill(salt);

            var nonce = new byte[NonceSize];
            RandomNumberGenerator.Fill(nonce);

            var derivedKey = DeriveKey(passphrase, salt);

            try
            {
                using var aesGcm = new AesGcm(derivedKey, TagSize);
                var ciphertext = new byte[rawKey.Length];
                var tag = new byte[TagSize];

                aesGcm.Encrypt(nonce, rawKey, ciphertext, tag);

                var payload = new byte[SaltSize + NonceSize + TagSize + ciphertext.Length];
                Buffer.BlockCopy(salt, 0, payload, 0, SaltSize);
                Buffer.BlockCopy(nonce, 0, payload, SaltSize, NonceSize);
                Buffer.BlockCopy(tag, 0, payload, SaltSize + NonceSize, TagSize);
                Buffer.BlockCopy(ciphertext, 0, payload, SaltSize + NonceSize + TagSize, ciphertext.Length);

                File.WriteAllBytes(_filePath, payload);

                CryptographicOperations.ZeroMemory(ciphertext);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(derivedKey);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(rawKey);
        }
    }

    public void Save(Key privateKey)
    {
        throw new InvalidOperationException("Passphrase required for secure key storage. Use Save(Key, string) instead.");
    }

    public Key? Load(string passphrase)
    {
        ValidatePassphrase(passphrase);

        if (!File.Exists(_filePath))
            return null;

        var payload = File.ReadAllBytes(_filePath);

        if (payload.Length < SaltSize + NonceSize + TagSize + 32)
            throw new CryptographicException("Invalid key file format");

        var salt = new byte[SaltSize];
        var nonce = new byte[NonceSize];
        var tag = new byte[TagSize];
        var ciphertext = new byte[payload.Length - SaltSize - NonceSize - TagSize];

        Buffer.BlockCopy(payload, 0, salt, 0, SaltSize);
        Buffer.BlockCopy(payload, SaltSize, nonce, 0, NonceSize);
        Buffer.BlockCopy(payload, SaltSize + NonceSize, tag, 0, TagSize);
        Buffer.BlockCopy(payload, SaltSize + NonceSize + TagSize, ciphertext, 0, ciphertext.Length);

        var derivedKey = DeriveKey(passphrase, salt);
        var rawKey = new byte[ciphertext.Length];

        try
        {
            using var aesGcm = new AesGcm(derivedKey, TagSize);
            aesGcm.Decrypt(nonce, ciphertext, tag, rawKey);

            return Key.Import(_signatureAlgorithm, rawKey, KeyBlobFormat.RawPrivateKey);
        }
        catch (CryptographicException)
        {
            throw new CryptographicException("Invalid passphrase or corrupted key file");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(derivedKey);
            CryptographicOperations.ZeroMemory(rawKey);
            CryptographicOperations.ZeroMemory(ciphertext);
        }
    }

    public Key? Load()
    {
        throw new InvalidOperationException("Passphrase required for secure key storage. Use Load(string) instead.");
    }

    public void Delete()
    {
        if (File.Exists(_filePath))
        {
            var fileInfo = new FileInfo(_filePath);
            var length = fileInfo.Length;

            using (var fs = new FileStream(_filePath, FileMode.Open, FileAccess.Write))
            {
                var zeros = new byte[length];
                fs.Write(zeros, 0, zeros.Length);
                fs.Flush();
            }

            File.Delete(_filePath);
        }
    }

    public bool Exists() => File.Exists(_filePath);

    private static byte[] DeriveKey(string passphrase, byte[] salt)
    {
        using var pbkdf2 = new Rfc2898DeriveBytes(
            passphrase,
            salt,
            KeyDerivationIterations,
            HashAlgorithmName.SHA256);
        return pbkdf2.GetBytes(32);
    }

    private static void ValidatePassphrase(string passphrase)
    {
        if (string.IsNullOrEmpty(passphrase))
            throw new ArgumentException("Passphrase cannot be empty", nameof(passphrase));

        if (passphrase.Length < 8)
            throw new ArgumentException("Passphrase must be at least 8 characters", nameof(passphrase));
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
