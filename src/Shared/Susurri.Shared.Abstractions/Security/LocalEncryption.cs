using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;

namespace Susurri.Shared.Abstractions.Security;

public static class LocalEncryption
{
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const byte FormatVersion = 1;

    public static byte[] DeriveSubkey(ReadOnlySpan<byte> masterKey, ReadOnlySpan<byte> context)
    {
        var subkey = new byte[32];
        HKDF.DeriveKey(HashAlgorithmName.SHA256, masterKey, subkey, ReadOnlySpan<byte>.Empty, context);
        return subkey;
    }

    public static byte[] Encrypt(byte[] key, byte[] plaintext)
    {
        var nonce = new byte[NonceSize];
        RandomNumberGenerator.Fill(nonce);

        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagSize];

        using var aes = new AesGcm(key, TagSize);
        aes.Encrypt(nonce, plaintext, ciphertext, tag);

        var payload = new byte[1 + NonceSize + TagSize + ciphertext.Length];
        payload[0] = FormatVersion;
        Buffer.BlockCopy(nonce, 0, payload, 1, NonceSize);
        Buffer.BlockCopy(tag, 0, payload, 1 + NonceSize, TagSize);
        Buffer.BlockCopy(ciphertext, 0, payload, 1 + NonceSize + TagSize, ciphertext.Length);
        return payload;
    }

    public static byte[] Decrypt(byte[] key, byte[] payload)
    {
        if (payload.Length < 1 + NonceSize + TagSize || payload[0] != FormatVersion)
            throw new CryptographicException("Invalid local store payload");

        var nonce = payload.AsSpan(1, NonceSize);
        var tag = payload.AsSpan(1 + NonceSize, TagSize);
        var ciphertext = payload.AsSpan(1 + NonceSize + TagSize);

        var plaintext = new byte[ciphertext.Length];
        using var aes = new AesGcm(key, TagSize);
        aes.Decrypt(nonce, ciphertext, tag, plaintext);
        return plaintext;
    }

    public static void SecureDelete(string filePath)
    {
        if (!File.Exists(filePath))
            return;

        var length = new FileInfo(filePath).Length;
        using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Write))
        {
            fs.Write(new byte[length], 0, (int)length);
            fs.Flush();
        }
        File.Delete(filePath);
    }

    public static string? QuarantineCorrupt(string filePath)
    {
        if (!File.Exists(filePath))
            return null;

        try
        {
            var target = filePath + ".corrupt";
            if (File.Exists(target))
                target = filePath + ".corrupt-" + Guid.NewGuid().ToString("N");
            File.Move(filePath, target);
            return target;
        }
        catch
        {
            return null;
        }
    }

    public static void RestrictDirectory(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            RestrictDirectoryWindows(path);
            return;
        }

        try
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        catch
        {
        }
    }

    [SupportedOSPlatform("windows")]
    private static void RestrictDirectoryWindows(string path)
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var user = identity.User;
            if (user == null)
                return;

            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(true, false);
            security.AddAccessRule(new FileSystemAccessRule(
                user,
                FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow));
            new DirectoryInfo(path).SetAccessControl(security);
        }
        catch
        {
        }
    }
}
