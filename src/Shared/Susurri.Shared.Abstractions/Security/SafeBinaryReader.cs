namespace Susurri.Shared.Abstractions.Security;

public static class SafeBinaryReader
{
    public static byte[] ReadBytesWithLimit(BinaryReader reader, int maxLength)
    {
        var length = reader.ReadInt32();

        if (length < 0)
            throw new InvalidDataException("Negative length is not allowed");

        if (length > maxLength)
            throw new InvalidDataException($"Data length {length} exceeds maximum allowed {maxLength}");

        return reader.ReadBytes(length);
    }

    public static string ReadStringWithLimit(BinaryReader reader, int maxLength)
    {
        var str = reader.ReadString();

        if (str.Length > maxLength)
            throw new InvalidDataException($"String length {str.Length} exceeds maximum allowed {maxLength}");

        return str;
    }

    public static int ReadInt32WithRange(BinaryReader reader, int min, int max)
    {
        var value = reader.ReadInt32();

        if (value < min || value > max)
            throw new InvalidDataException($"Value {value} is outside allowed range [{min}, {max}]");

        return value;
    }

    public static ushort ReadUInt16WithRange(BinaryReader reader, ushort min, ushort max)
    {
        var value = reader.ReadUInt16();

        if (value < min || value > max)
            throw new InvalidDataException($"Value {value} is outside allowed range [{min}, {max}]");

        return value;
    }

    public static byte ReadByteWithRange(BinaryReader reader, byte min, byte max)
    {
        var value = reader.ReadByte();

        if (value < min || value > max)
            throw new InvalidDataException($"Value {value} is outside allowed range [{min}, {max}]");

        return value;
    }
}
