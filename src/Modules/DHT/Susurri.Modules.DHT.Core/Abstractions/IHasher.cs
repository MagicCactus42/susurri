namespace Susurri.Modules.DHT.Core.Abstractions;

public interface IHasher
{
    string ComputeHash(string input);
}