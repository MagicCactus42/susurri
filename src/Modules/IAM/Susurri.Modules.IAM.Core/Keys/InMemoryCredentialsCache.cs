#nullable enable
using System.Security.Cryptography;
using Susurri.Modules.IAM.Core.Abstractions;

namespace Susurri.Modules.IAM.Core.Keys;

internal sealed class InMemoryCredentialsCache : IInMemoryCredentialsCache, IDisposable
{
    private byte[]? _passphraseBytes;
    private byte[]? _publicKey;
    private byte[]? _usernameBytes;
    private readonly object _lock = new();
    private bool _disposed;

    public void Set(string username, string passphrase, byte[] publicKey)
    {
        ArgumentNullException.ThrowIfNull(username);
        ArgumentNullException.ThrowIfNull(passphrase);
        ArgumentNullException.ThrowIfNull(publicKey);

        lock (_lock)
        {
            ClearInternal();

            _usernameBytes = System.Text.Encoding.UTF8.GetBytes(username);
            _passphraseBytes = System.Text.Encoding.UTF8.GetBytes(passphrase);
            _publicKey = new byte[publicKey.Length];
            Buffer.BlockCopy(publicKey, 0, _publicKey, 0, publicKey.Length);
        }
    }

    public (string Passphrase, byte[] PublicKey, string Username)? Get()
    {
        lock (_lock)
        {
            if (_passphraseBytes == null || _publicKey == null || _usernameBytes == null)
                return null;

            var passphrase = System.Text.Encoding.UTF8.GetString(_passphraseBytes);
            var username = System.Text.Encoding.UTF8.GetString(_usernameBytes);
            var publicKeyCopy = new byte[_publicKey.Length];
            Buffer.BlockCopy(_publicKey, 0, publicKeyCopy, 0, _publicKey.Length);

            return (passphrase, publicKeyCopy, username);
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            ClearInternal();
        }
    }

    private void ClearInternal()
    {
        if (_passphraseBytes != null)
        {
            CryptographicOperations.ZeroMemory(_passphraseBytes);
            _passphraseBytes = null;
        }

        if (_publicKey != null)
        {
            CryptographicOperations.ZeroMemory(_publicKey);
            _publicKey = null;
        }

        if (_usernameBytes != null)
        {
            CryptographicOperations.ZeroMemory(_usernameBytes);
            _usernameBytes = null;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;

        lock (_lock)
        {
            ClearInternal();
            _disposed = true;
        }
    }
}
