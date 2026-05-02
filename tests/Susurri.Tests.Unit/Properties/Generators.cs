using CsCheck;
using Susurri.Modules.DHT.Core.Kademlia;
using Susurri.Modules.DHT.Core.Kademlia.Protocol;
using Susurri.Modules.DHT.Core.Onion;

namespace Susurri.Tests.Unit.Properties;

/// <summary>
/// Reusable CsCheck generators for protocol message types. These produce
/// instances that the corresponding Serialize/Deserialize round-trip should
/// preserve. Keeps generator wiring out of individual test bodies.
/// </summary>
internal static class MsgGen
{
    /// <summary>32-byte public-key blob (matches X25519 / Ed25519 raw size).</summary>
    public static readonly Gen<byte[]> PublicKey = Gen.Byte.Array[32];

    /// <summary>64-byte Ed25519 signature blob.</summary>
    public static readonly Gen<byte[]> Signature64 = Gen.Byte.Array[64];

    /// <summary>32-byte KademliaId.</summary>
    public static readonly Gen<KademliaId> KademliaIdGen =
        Gen.Byte.Array[32].Select(KademliaId.FromBytes);

    /// <summary>16-byte Guid.</summary>
    public static readonly Gen<Guid> GuidGen =
        Gen.Byte.Array[16].Select(b => new Guid(b));

    /// <summary>Realistic Unix timestamp in [2020, 2090).</summary>
    public static readonly Gen<long> Timestamp =
        Gen.Long[1_577_836_800L, 3_786_998_400L];

    /// <summary>Bounded ASCII content for ChatMessage etc.</summary>
    public static readonly Gen<string> ContentString =
        Gen.String[Gen.Char[' ', '~'], 0, 256];

    /// <summary>Realistic IPv4 string in dotted-quad form.</summary>
    public static readonly Gen<string> IpV4String =
        Gen.Select(Gen.Byte, Gen.Byte, Gen.Byte, Gen.Byte,
            (a, b, c, d) => $"{a}.{b}.{c}.{d}");

    /// <summary>Bounded variable-length byte payload.</summary>
    public static Gen<byte[]> BytesUpTo(int max) =>
        Gen.Int[0, max].SelectMany(n => Gen.Byte.Array[n]);

    public static readonly Gen<ChatMessage> ChatMessageGen =
        Gen.Select(
            PublicKey, PublicKey, ContentString, Timestamp, GuidGen, Signature64,
            (encPub, sigPub, content, ts, id, sig) => new ChatMessage
            {
                SenderPublicKey = encPub,
                SenderSigningPublicKey = sigPub,
                Content = content,
                Timestamp = ts,
                MessageId = id,
                Signature = sig
            });

    public static readonly Gen<UserPublicKeyRecord> UserPublicKeyRecordGen =
        Gen.Select(
            PublicKey, PublicKey, Timestamp, Signature64,
            (encPub, sigPub, ts, sig) => new UserPublicKeyRecord
            {
                EncryptionPublicKey = encPub,
                SigningPublicKey = sigPub,
                Timestamp = ts,
                Signature = sig
            });

    public static readonly Gen<UserPublicKeyRecord> UserPublicKeyRecordUnsignedGen =
        Gen.Select(
            PublicKey, PublicKey, Timestamp,
            (encPub, sigPub, ts) => new UserPublicKeyRecord
            {
                EncryptionPublicKey = encPub,
                SigningPublicKey = sigPub,
                Timestamp = ts,
                Signature = null
            });

    public static readonly Gen<PingMessage> PingMessageGen =
        Gen.Select(GuidGen, KademliaIdGen, PublicKey,
            (id, sender, pub) => new PingMessage
            {
                MessageId = id,
                SenderId = sender,
                SenderPublicKey = pub
            });

    public static readonly Gen<PongMessage> PongMessageGen =
        Gen.Select(GuidGen, KademliaIdGen, PublicKey, GuidGen,
            (id, sender, pub, inResponse) => new PongMessage
            {
                MessageId = id,
                SenderId = sender,
                SenderPublicKey = pub,
                InResponseTo = inResponse
            });

    public static readonly Gen<FindNodeMessage> FindNodeMessageGen =
        Gen.Select(GuidGen, KademliaIdGen, PublicKey, KademliaIdGen,
            (id, sender, pub, target) => new FindNodeMessage
            {
                MessageId = id,
                SenderId = sender,
                SenderPublicKey = pub,
                TargetId = target
            });

    public static readonly Gen<StoreMessage> StoreMessageGen =
        from id in GuidGen
        from sender in KademliaIdGen
        from pub in PublicKey
        from key in KademliaIdGen
        from value in BytesUpTo(2048)
        from ttl in Gen.UInt[1, 86400]
        from ts in Timestamp
        from sigPub in PublicKey
        from sig in Signature64
        select new StoreMessage
        {
            MessageId = id,
            SenderId = sender,
            SenderPublicKey = pub,
            Key = key,
            Value = value,
            TtlSeconds = ttl,
            Timestamp = ts,
            SigningPublicKey = sigPub,
            Signature = sig
        };

    public static readonly Gen<StoreOfflineMessageMessage> StoreOfflineMessageGen =
        Gen.Select(
            GuidGen, KademliaIdGen, PublicKey, PublicKey, BytesUpTo(8192),
            Timestamp, PublicKey, Signature64,
            (id, sender, pub, recipient, payload, ts, sigPub, sig) => new StoreOfflineMessageMessage
            {
                MessageId = id,
                SenderId = sender,
                SenderPublicKey = pub,
                RecipientPublicKey = recipient,
                EncryptedMessage = payload,
                Timestamp = ts,
                SigningPublicKey = sigPub,
                Signature = sig
            });

    public static readonly Gen<GetOfflineMessagesMessage> GetOfflineMessagesGen =
        Gen.Select(
            GuidGen, KademliaIdGen, PublicKey, PublicKey, PublicKey,
            Timestamp, Signature64,
            (id, sender, pub, recipient, sigPub, ts, sig) => new GetOfflineMessagesMessage
            {
                MessageId = id,
                SenderId = sender,
                SenderPublicKey = pub,
                RecipientPublicKey = recipient,
                SigningPublicKey = sigPub,
                Timestamp = ts,
                Signature = sig
            });

    public static readonly Gen<OnionMessageWrapper> OnionMessageWrapperGen =
        Gen.Select(
            GuidGen, KademliaIdGen, PublicKey, BytesUpTo(32 * 1024),
            (id, sender, pub, payload) => new OnionMessageWrapper
            {
                MessageId = id,
                SenderId = sender,
                SenderPublicKey = pub,
                EncryptedPayload = payload
            });

    public static readonly Gen<OnionLayer> OnionLayerGen =
        Gen.Select(PublicKey, Gen.Byte.Array[12], BytesUpTo(8192),
            (pub, nonce, ct) => new OnionLayer
            {
                EphemeralPublicKey = pub,
                Nonce = nonce,
                Ciphertext = ct
            });
}
