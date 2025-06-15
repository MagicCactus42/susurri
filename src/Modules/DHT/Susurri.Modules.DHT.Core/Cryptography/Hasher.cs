using System.Security.Cryptography;
using System.Text;
using Susurri.Modules.DHT.Core.Abstractions;

namespace Susurri.Modules.DHT.Core.Cryptography;

public class Hasher : IHasher
{
    public string ComputeHash(string input)
    {
        using var sha1 = SHA1.Create();
        var hash = sha1.ComputeHash(Encoding.UTF8.GetBytes(input));
        return BitConverter.ToString(hash).Replace("-", "").ToLower();
    }
}