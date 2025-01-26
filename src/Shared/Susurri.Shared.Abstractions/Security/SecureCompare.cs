using System.Runtime.CompilerServices;
using System.Security.Cryptography;

namespace Susurri.Shared.Abstractions.Security;

public static class SecureCompare
{
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.NoOptimization)]
    public static bool ConstantTimeEquals(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        return CryptographicOperations.FixedTimeEquals(a, b);
    }

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.NoOptimization)]
    public static bool ConstantTimeEquals(byte[] a, byte[] b)
    {
        if (a == null || b == null)
            return a == b;

        return CryptographicOperations.FixedTimeEquals(a, b);
    }
}
