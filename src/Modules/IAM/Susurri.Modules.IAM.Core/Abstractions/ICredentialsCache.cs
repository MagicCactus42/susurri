namespace Susurri.Modules.IAM.Core.Abstractions;

public interface ICredentialsCache
{
    Task SaveAsync(string username, string passphrase, string pin);
    (string Username, string Passphrase) Load(string pin);
    void Clear();
    bool Exists();
}