using System.Collections.Concurrent;
using System.Security.Cryptography;
using NSec.Cryptography;

namespace Susurri.Modules.DHT.Core.Onion.Ratchet;

/// <summary>
/// Manages per-peer Double Ratchet sessions and the on-wire envelope. The first
/// message to a new peer carries an X3DH ephemeral public key ("bootstrap") the
/// recipient uses — together with its own static X25519 key — to derive the same
/// initial shared secret; from there the ratchet takes over. Sessions are held in
/// memory for the process lifetime (a restart simply re-bootstraps).
///
/// Limitation: if two peers send each other a first message simultaneously (both
/// initiate before either receives), their sessions won't match and those two
/// messages fail to decrypt until one side re-bootstraps. The common
/// one-initiator flow is unaffected.
/// </summary>
public sealed class RatchetSessionManager : IDisposable
{
    private static readonly KeyAgreementAlgorithm Dh = KeyAgreementAlgorithm.X25519;
    private static readonly KeyDerivationAlgorithm Hkdf = KeyDerivationAlgorithm.HkdfSha256;
    private const int MaxCiphertext = 64 * 1024;

    private readonly Key _identityKey;
    private readonly ConcurrentDictionary<string, Session> _sessions = new();
    private bool _disposed;

    public RatchetSessionManager(Key identityKey)
    {
        _identityKey = identityKey;
    }

    public byte[] Seal(byte[] peerPublicKey, byte[] plaintext)
    {
        var session = _sessions.GetOrAdd(Convert.ToHexString(peerPublicKey), _ => CreateInitiator(peerPublicKey));
        lock (session.Gate)
        {
            var (header, ciphertext) = session.Ratchet.Encrypt(plaintext);
            var bootstrap = session.Established ? Array.Empty<byte>() : session.Bootstrap;
            return SerializeEnvelope(bootstrap, header, ciphertext);
        }
    }

    public byte[] Open(byte[] senderPublicKey, byte[] envelope)
    {
        var (bootstrap, header, ciphertext) = DeserializeEnvelope(envelope);
        var session = _sessions.GetOrAdd(Convert.ToHexString(senderPublicKey), _ => CreateResponder(bootstrap));
        lock (session.Gate)
        {
            var plaintext = session.Ratchet.Decrypt(header, ciphertext);
            session.Established = true;
            return plaintext;
        }
    }

    private Session CreateInitiator(byte[] peerPublicKey)
    {
        using var ephemeral = Key.Create(Dh);
        var ephemeralPublic = ephemeral.PublicKey.Export(KeyBlobFormat.RawPublicKey);
        var sharedSecret = X3dh(ephemeral, peerPublicKey);

        return new Session
        {
            Ratchet = DoubleRatchet.CreateInitiator(sharedSecret, peerPublicKey),
            Bootstrap = ephemeralPublic,
            Established = false
        };
    }

    private Session CreateResponder(byte[] bootstrap)
    {
        if (bootstrap.Length != 32)
            throw new CryptographicException("Cannot open a new session without an X3DH bootstrap key");

        var sharedSecret = X3dh(_identityKey, bootstrap);
        return new Session
        {
            Ratchet = DoubleRatchet.CreateResponder(sharedSecret, _identityKey),
            Established = true
        };
    }

    private static byte[] X3dh(Key ourKey, byte[] peerPublicKey)
    {
        var peer = PublicKey.Import(Dh, peerPublicKey, KeyBlobFormat.RawPublicKey);
        using var shared = Dh.Agree(ourKey, peer)
            ?? throw new CryptographicException("X3DH agreement failed");
        return Hkdf.DeriveBytes(shared, ReadOnlySpan<byte>.Empty, "susurri-ratchet-x3dh-v1"u8, 32);
    }

    private static byte[] SerializeEnvelope(byte[] bootstrap, RatchetHeader header, byte[] ciphertext)
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);

        writer.Write((byte)bootstrap.Length);
        writer.Write(bootstrap);
        var headerBytes = header.Serialize();
        writer.Write((ushort)headerBytes.Length);
        writer.Write(headerBytes);
        writer.Write(ciphertext.Length);
        writer.Write(ciphertext);

        return ms.ToArray();
    }

    private static (byte[] Bootstrap, RatchetHeader Header, byte[] Ciphertext) DeserializeEnvelope(byte[] envelope)
    {
        using var ms = new MemoryStream(envelope);
        using var reader = new BinaryReader(ms);

        var bootstrapLen = reader.ReadByte();
        if (bootstrapLen != 0 && bootstrapLen != 32)
            throw new InvalidDataException($"Invalid bootstrap key length: {bootstrapLen}");
        var bootstrap = reader.ReadBytes(bootstrapLen);

        var headerLen = reader.ReadUInt16();
        var header = RatchetHeader.Deserialize(reader.ReadBytes(headerLen));

        var ciphertextLen = reader.ReadInt32();
        if (ciphertextLen < 0 || ciphertextLen > MaxCiphertext)
            throw new InvalidDataException($"Invalid ratchet ciphertext length: {ciphertextLen}");
        var ciphertext = reader.ReadBytes(ciphertextLen);

        return (bootstrap, header, ciphertext);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var session in _sessions.Values)
            session.Ratchet.Dispose();
        _sessions.Clear();
    }

    private sealed class Session
    {
        public DoubleRatchet Ratchet { get; init; } = null!;
        public byte[] Bootstrap { get; init; } = Array.Empty<byte>();
        public bool Established { get; set; }
        public object Gate { get; } = new();
    }
}
