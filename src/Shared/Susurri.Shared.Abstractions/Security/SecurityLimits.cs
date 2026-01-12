namespace Susurri.Shared.Abstractions.Security;

public static class SecurityLimits
{
    public const int MaxMessageSize = 64 * 1024;
    public const int MaxValueSize = 32 * 1024;
    public const int MaxStringLength = 1024;
    public const int MaxUsernameLength = 32;
    public const int MinUsernameLength = 3;
    public const int MaxPathLength = 7;
    public const int MinPathLength = 3;
    public const int MaxNodesPerResponse = 20;
    public const int MaxOfflineMessagesPerUser = 100;
    public const int PublicKeySize = 32;
    public const int MaxIpAddressLength = 45;
    public const int MaxPortValue = 65535;

    public const int MinPassphraseWords = 6;
    public const int MaxPassphraseWords = 24;
    public const int MinCachePasswordLength = 8;
}
