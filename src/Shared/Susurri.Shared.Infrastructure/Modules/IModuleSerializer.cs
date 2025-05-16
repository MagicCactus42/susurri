namespace Susurri.Shared.Infrastructure.Modules;

public interface IModuleSerializer
{
    byte[] Serialize<T>(T value);
    T Deserialize<T>(byte[] value);
}