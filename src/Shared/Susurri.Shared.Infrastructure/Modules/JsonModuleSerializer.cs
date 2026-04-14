using System.Text;
using System.Text.Json;

namespace Susurri.Shared.Infrastructure.Modules;

internal sealed class JsonModuleSerializer : IModuleSerializer
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public byte[] Serialize<T>(T value)
        => Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value, SerializerOptions));

    public T Deserialize<T>(byte[] value)
    {
        var result = JsonSerializer.Deserialize<T>(Encoding.UTF8.GetString(value), SerializerOptions);
        if (result is null)
            throw new InvalidDataException($"Failed to deserialize payload to {typeof(T).Name}: result was null");
        return result;
    }

    public object Deserialize(byte[] value, Type type)
    {
        var result = JsonSerializer.Deserialize(Encoding.UTF8.GetString(value), type, SerializerOptions);
        if (result is null)
            throw new InvalidDataException($"Failed to deserialize payload to {type.Name}: result was null");
        return result;
    }
}