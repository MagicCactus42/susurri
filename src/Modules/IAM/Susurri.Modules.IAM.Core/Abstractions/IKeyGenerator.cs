using NSec.Cryptography;

namespace Susurri.Modules.IAM.Core.Abstractions;

public interface IKeyGenerator
{
    Key GenerateKeys(string passphrase);
}