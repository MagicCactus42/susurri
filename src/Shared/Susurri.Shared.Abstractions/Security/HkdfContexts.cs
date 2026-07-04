namespace Susurri.Shared.Abstractions.Security;

/// <summary>
/// HKDF info strings for domain separation.
/// Each cryptographic context must use a unique info parameter to ensure
/// that a shared secret derived in one context never produces the same key
/// as in another context (RFC 5869 Section 3.2).
/// </summary>
public static class HkdfContexts
{
    public static ReadOnlySpan<byte> OnionLayer => "susurri-onion-layer-v1"u8;
    public static ReadOnlySpan<byte> DirectMessage => "susurri-direct-message-v1"u8;
    public static ReadOnlySpan<byte> GroupKeyWrap => "susurri-group-key-wrap-v1"u8;
    public static ReadOnlySpan<byte> GroupSenderChain => "susurri-group-sender-chain-v1"u8;
    public static ReadOnlySpan<byte> GroupMessageKey => "susurri-group-message-key-v1"u8;
    public static ReadOnlySpan<byte> GroupSenderKeyWrap => "susurri-group-sender-key-wrap-v1"u8;
    public static ReadOnlySpan<byte> LocalStoreKey => "susurri-local-store-v1"u8;
    public static ReadOnlySpan<byte> LocalContacts => "susurri-local-contacts-v1"u8;
    public static ReadOnlySpan<byte> LocalHistory => "susurri-local-history-v1"u8;
    public static ReadOnlySpan<byte> LocalGroups => "susurri-local-groups-v1"u8;
    public static ReadOnlySpan<byte> LocalGroupRatchet => "susurri-local-group-ratchet-v1"u8;
}
