namespace Susurri.Modules.IAM.Core.Keys;

using Susurri.Modules.IAM.Core.Abstractions;


internal sealed class InMemoryCredentialsCache : IInMemoryCredentialsCache
{
    private string _passphrase;
    private byte[] _publicKey;
    private string _username;
    private readonly object _lock = new();

    public void Set(string username, string passphrase, byte[] publicKey)
    {
        lock (_lock)
        {
            _passphrase = passphrase;
            _publicKey = publicKey;
            _username = username;
        }
    }

    public (string Passphrase, byte[] PublicKey, string Username)? Get()
    {
        lock (_lock)
        {
            if (string.IsNullOrEmpty(_passphrase) || _publicKey == null || _username == null)
                return null;

            return (_passphrase, _publicKey, _username);
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _passphrase = null;
            _publicKey = null;
            _username = null;
        }
    }
}
