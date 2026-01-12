#nullable enable
using NSec.Cryptography;

namespace Susurri.Modules.IAM.Core.Abstractions;

public interface IKeyStorage
{
    void Save(Key privateKey, string passphrase);
    void Save(Key privateKey);
    Key? Load(string passphrase);
    Key? Load();
    void Delete();
    bool Exists();
}
