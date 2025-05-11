using NSec.Cryptography;

namespace Susurri.Modules.IAM.Core.Abstractions;

public interface IKeyStorage
{
    void Save(Key privateKey);
    Key Load();
    void Delete();

}