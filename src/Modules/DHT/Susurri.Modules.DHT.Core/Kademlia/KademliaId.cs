using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace Susurri.Modules.DHT.Core.Kademlia;

// 256-bit Kademlia node/key identifier using XOR distance metric for routing.
public readonly struct KademliaId : IEquatable<KademliaId>, IComparable<KademliaId>
{
    public const int BitLength = 256;
    public const int ByteLength = 32;

    private readonly byte[] _bytes;

    private static readonly byte[] EmptyBytes = new byte[ByteLength];

    public ReadOnlySpan<byte> Bytes => _bytes ?? EmptyBytes;

    private KademliaId(byte[] bytes)
    {
        if (bytes.Length != ByteLength)
            throw new ArgumentException($"KademliaId must be exactly {ByteLength} bytes", nameof(bytes));
        _bytes = bytes;
    }

    public static KademliaId FromBytes(byte[] bytes)
    {
        var copy = new byte[ByteLength];
        Array.Copy(bytes, copy, Math.Min(bytes.Length, ByteLength));
        return new KademliaId(copy);
    }

    public static KademliaId FromBytes(ReadOnlySpan<byte> bytes)
    {
        var copy = new byte[ByteLength];
        bytes[..Math.Min(bytes.Length, ByteLength)].CopyTo(copy);
        return new KademliaId(copy);
    }

    public static KademliaId FromString(string input)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return new KademliaId(hash);
    }

    public static KademliaId FromPublicKey(byte[] publicKey)
    {
        var hash = SHA256.HashData(publicKey);
        return new KademliaId(hash);
    }

    public static KademliaId Random()
    {
        var bytes = new byte[ByteLength];
        RandomNumberGenerator.Fill(bytes);
        return new KademliaId(bytes);
    }

    public KademliaId DistanceTo(KademliaId other)
    {
        var result = new byte[ByteLength];
        for (int i = 0; i < ByteLength; i++)
        {
            result[i] = (byte)(_bytes[i] ^ other._bytes[i]);
        }
        return new KademliaId(result);
    }

    // Returns the index of the highest set bit (0-255) to determine k-bucket.
    // Returns -1 if all bits are zero.
    public int GetHighestBitIndex()
    {
        for (int i = 0; i < ByteLength; i++)
        {
            if (_bytes[i] != 0)
            {
                int bitInByte = BitOperations.Log2(_bytes[i]);
                return (ByteLength - 1 - i) * 8 + bitInByte;
            }
        }
        return -1;
    }

    public int GetBucketIndex(KademliaId other)
    {
        var mine = _bytes ?? EmptyBytes;
        var theirs = other._bytes ?? EmptyBytes;
        for (int i = 0; i < ByteLength; i++)
        {
            int xor = mine[i] ^ theirs[i];
            if (xor != 0)
            {
                int bitInByte = BitOperations.Log2((uint)xor);
                return (ByteLength - 1 - i) * 8 + bitInByte;
            }
        }
        return -1;
    }

    public static int CompareDistances(in KademliaId a, in KademliaId b, in KademliaId target)
    {
        var ab = a._bytes ?? EmptyBytes;
        var bb = b._bytes ?? EmptyBytes;
        var tb = target._bytes ?? EmptyBytes;
        for (int i = 0; i < ByteLength; i++)
        {
            int da = ab[i] ^ tb[i];
            int db = bb[i] ^ tb[i];
            if (da != db)
                return da < db ? -1 : 1;
        }
        return 0;
    }

    public bool GetBit(int position)
    {
        if (position < 0 || position >= BitLength)
            throw new ArgumentOutOfRangeException(nameof(position));

        int byteIndex = position / 8;
        int bitIndex = 7 - (position % 8);
        return (_bytes[byteIndex] & (1 << bitIndex)) != 0;
    }

    public int CompareTo(KademliaId other)
    {
        var myBytes = _bytes ?? EmptyBytes;
        var otherBytes = other._bytes ?? EmptyBytes;

        for (int i = 0; i < ByteLength; i++)
        {
            int cmp = myBytes[i].CompareTo(otherBytes[i]);
            if (cmp != 0) return cmp;
        }
        return 0;
    }

    public bool Equals(KademliaId other)
    {
        var myBytes = _bytes ?? EmptyBytes;
        var otherBytes = other._bytes ?? EmptyBytes;
        return myBytes.AsSpan().SequenceEqual(otherBytes);
    }

    public override bool Equals(object? obj) => obj is KademliaId other && Equals(other);

    public override int GetHashCode()
    {
        // XOR-fold all 32 bytes into 4 to get better hash distribution
        // This prevents hash collision attacks against Dictionary/HashSet
        var bytes = _bytes ?? EmptyBytes;
        int hash = 0;
        for (int i = 0; i < ByteLength; i += 4)
        {
            hash ^= BitConverter.ToInt32(bytes, i);
        }
        return hash;
    }

    public override string ToString()
    {
        var bytes = _bytes ?? EmptyBytes;
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    public static bool operator ==(KademliaId left, KademliaId right) => left.Equals(right);
    public static bool operator !=(KademliaId left, KademliaId right) => !left.Equals(right);
    public static bool operator <(KademliaId left, KademliaId right) => left.CompareTo(right) < 0;
    public static bool operator >(KademliaId left, KademliaId right) => left.CompareTo(right) > 0;
    public static bool operator <=(KademliaId left, KademliaId right) => left.CompareTo(right) <= 0;
    public static bool operator >=(KademliaId left, KademliaId right) => left.CompareTo(right) >= 0;
}
