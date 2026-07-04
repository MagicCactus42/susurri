using System.Security.Cryptography;

namespace Susurri.Modules.DHT.Core.Contacts;

public static class SafetyNumber
{
    public static string Compute(
        byte[] localEncryptionKey, byte[] localSigningKey,
        byte[] remoteEncryptionKey, byte[] remoteSigningKey)
    {
        var local = Identity(localSigningKey, localEncryptionKey);
        var remote = Identity(remoteSigningKey, remoteEncryptionKey);

        var (first, second) = Compare(local, remote) <= 0 ? (local, remote) : (remote, local);

        var combined = new byte[first.Length + second.Length];
        Buffer.BlockCopy(first, 0, combined, 0, first.Length);
        Buffer.BlockCopy(second, 0, combined, first.Length, second.Length);

        var digest = SHA512.HashData(combined);

        var groups = new string[12];
        for (var i = 0; i < groups.Length; i++)
        {
            var value = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(digest.AsSpan(i * 4, 4));
            groups[i] = (value % 100000).ToString("D5");
        }

        return string.Join(' ', groups);
    }

    private static byte[] Identity(byte[] signingKey, byte[] encryptionKey)
    {
        var identity = new byte[signingKey.Length + encryptionKey.Length];
        Buffer.BlockCopy(signingKey, 0, identity, 0, signingKey.Length);
        Buffer.BlockCopy(encryptionKey, 0, identity, signingKey.Length, encryptionKey.Length);
        return identity;
    }

    private static int Compare(byte[] a, byte[] b)
        => a.AsSpan().SequenceCompareTo(b);
}
