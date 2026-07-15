using System.Security.Cryptography;
using NSec.Cryptography;

namespace Susurri.Modules.DHT.Core.Onion.Ratchet;

/// <summary>
/// Signal-style Double Ratchet: a per-peer session that derives a fresh message
/// key for every message from a symmetric chain, and periodically mixes in a new
/// X25519 DH exchange (the DH ratchet). This gives forward secrecy — a
/// compromised current key does not expose past messages, because chain keys are
/// one-way — and post-compromise security — the DH ratchet heals the session
/// after a key leak. Unlike the per-message ephemeral used at the onion layer,
/// the ratchet protects message content even if a peer's long-term key is later
/// compromised.
///
/// KDFs: root chain uses HKDF-SHA256 keyed by the DH output; the symmetric chain
/// uses HMAC-SHA256; messages are sealed with ChaCha20-Poly1305, binding the
/// header as associated data. Skipped message keys are retained (bounded) so
/// out-of-order delivery still decrypts.
/// </summary>
public sealed class DoubleRatchet : IDisposable
{
    private static readonly KeyAgreementAlgorithm Dh = KeyAgreementAlgorithm.X25519;
    private static readonly KeyDerivationAlgorithm Hkdf = KeyDerivationAlgorithm.HkdfSha256;
    private const int MaxSkip = 1000;

    private byte[] _rootKey;
    private Key _dhSelf;
    private byte[]? _dhRemote;
    private byte[]? _sendChainKey;
    private byte[]? _recvChainKey;
    private int _sendCount;
    private int _recvCount;
    private int _previousChainLength;
    private readonly Dictionary<string, byte[]> _skipped = new();
    private bool _ownsSelfKey;
    private bool _disposed;

    private DoubleRatchet(byte[] rootKey, Key dhSelf, bool ownsSelfKey)
    {
        _rootKey = rootKey;
        _dhSelf = dhSelf;
        _ownsSelfKey = ownsSelfKey;
    }

    /// <summary>
    /// Initiator side: seeds from the X3DH shared secret and the responder's
    /// initial ratchet public key (its static X25519 key).
    /// </summary>
    public static DoubleRatchet CreateInitiator(byte[] sharedSecret, byte[] remoteRatchetPublicKey)
    {
        var self = Key.Create(Dh);
        var ratchet = new DoubleRatchet((byte[])sharedSecret.Clone(), self, ownsSelfKey: true)
        {
            _dhRemote = (byte[])remoteRatchetPublicKey.Clone()
        };
        var (rk, ck) = KdfRootKey(ratchet._rootKey, self, remoteRatchetPublicKey);
        ratchet._rootKey = rk;
        ratchet._sendChainKey = ck;
        return ratchet;
    }

    /// <summary>
    /// Responder side: seeds from the same X3DH shared secret using the ratchet
    /// keypair whose public key the initiator used (the responder's static key).
    /// The key is borrowed, not owned — it is used for the first DH agreement but
    /// never disposed here, since it is the caller's long-lived identity key.
    /// </summary>
    public static DoubleRatchet CreateResponder(byte[] sharedSecret, Key ownRatchetKey)
        => new((byte[])sharedSecret.Clone(), ownRatchetKey, ownsSelfKey: false);

    public (RatchetHeader Header, byte[] Ciphertext) Encrypt(byte[] plaintext)
    {
        if (_sendChainKey == null)
            throw new InvalidOperationException("Ratchet has no sending chain yet");

        var (chainKey, messageKey) = KdfChainKey(_sendChainKey);
        _sendChainKey = chainKey;

        var header = new RatchetHeader
        {
            DhPublicKey = _dhSelf.PublicKey.Export(KeyBlobFormat.RawPublicKey),
            PreviousChainLength = _previousChainLength,
            MessageNumber = _sendCount
        };
        _sendCount++;

        var ciphertext = AeadEncrypt(messageKey, plaintext, header.Serialize());
        CryptographicOperations.ZeroMemory(messageKey);
        return (header, ciphertext);
    }

    public byte[] Decrypt(RatchetHeader header, byte[] ciphertext)
    {
        var skippedKey = SkippedKey(header.DhPublicKey, header.MessageNumber);
        if (_skipped.Remove(skippedKey, out var stored))
        {
            var plaintext = AeadDecrypt(stored, ciphertext, header.Serialize());
            CryptographicOperations.ZeroMemory(stored);
            return plaintext;
        }

        if (_dhRemote == null || !header.DhPublicKey.AsSpan().SequenceEqual(_dhRemote))
        {
            SkipMessageKeys(header.PreviousChainLength);
            DhRatchet(header.DhPublicKey);
        }

        SkipMessageKeys(header.MessageNumber);

        var (chainKey, messageKey) = KdfChainKey(_recvChainKey!);
        _recvChainKey = chainKey;
        _recvCount++;

        var result = AeadDecrypt(messageKey, ciphertext, header.Serialize());
        CryptographicOperations.ZeroMemory(messageKey);
        return result;
    }

    private void SkipMessageKeys(int until)
    {
        if (_recvChainKey == null)
            return;
        if (_recvCount + MaxSkip < until)
            throw new CryptographicException("Too many skipped messages");

        while (_recvCount < until)
        {
            var (chainKey, messageKey) = KdfChainKey(_recvChainKey);
            _recvChainKey = chainKey;
            _skipped[SkippedKey(_dhRemote!, _recvCount)] = messageKey;
            _recvCount++;
        }
    }

    private void DhRatchet(byte[] remotePublicKey)
    {
        _previousChainLength = _sendCount;
        _sendCount = 0;
        _recvCount = 0;
        _dhRemote = (byte[])remotePublicKey.Clone();

        var (rk1, recvChain) = KdfRootKey(_rootKey, _dhSelf, _dhRemote);
        _rootKey = rk1;
        _recvChainKey = recvChain;

        if (_ownsSelfKey)
            _dhSelf.Dispose();
        _dhSelf = Key.Create(Dh);
        _ownsSelfKey = true;

        var (rk2, sendChain) = KdfRootKey(_rootKey, _dhSelf, _dhRemote);
        _rootKey = rk2;
        _sendChainKey = sendChain;
    }

    private static (byte[] RootKey, byte[] ChainKey) KdfRootKey(byte[] rootKey, Key self, byte[] remotePublicKey)
    {
        var remote = PublicKey.Import(Dh, remotePublicKey, KeyBlobFormat.RawPublicKey);
        using var shared = Dh.Agree(self, remote)
            ?? throw new CryptographicException("Ratchet DH agreement failed");

        var okm = Hkdf.DeriveBytes(shared, rootKey, "susurri-ratchet-root-v1"u8, 64);
        return (okm[..32], okm[32..]);
    }

    private static (byte[] ChainKey, byte[] MessageKey) KdfChainKey(byte[] chainKey)
    {
        using var hmac = new HMACSHA256(chainKey);
        var messageKey = hmac.ComputeHash(new byte[] { 0x01 });
        var nextChain = hmac.ComputeHash(new byte[] { 0x02 });
        return (nextChain, messageKey);
    }

    private static byte[] AeadEncrypt(byte[] key, byte[] plaintext, byte[] associatedData)
    {
        var nonce = new byte[12];
        RandomNumberGenerator.Fill(nonce);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[16];

        using var aead = new System.Security.Cryptography.ChaCha20Poly1305(key);
        aead.Encrypt(nonce, plaintext, ciphertext, tag, associatedData);

        var output = new byte[12 + ciphertext.Length + 16];
        Buffer.BlockCopy(nonce, 0, output, 0, 12);
        Buffer.BlockCopy(ciphertext, 0, output, 12, ciphertext.Length);
        Buffer.BlockCopy(tag, 0, output, 12 + ciphertext.Length, 16);
        return output;
    }

    private static byte[] AeadDecrypt(byte[] key, byte[] sealedData, byte[] associatedData)
    {
        if (sealedData.Length < 28)
            throw new CryptographicException("Ratchet ciphertext too short");

        var nonce = sealedData.AsSpan(0, 12);
        var ciphertext = sealedData.AsSpan(12, sealedData.Length - 28);
        var tag = sealedData.AsSpan(sealedData.Length - 16, 16);

        var plaintext = new byte[ciphertext.Length];
        using var aead = new System.Security.Cryptography.ChaCha20Poly1305(key);
        aead.Decrypt(nonce, ciphertext, tag, plaintext, associatedData);
        return plaintext;
    }

    private static string SkippedKey(byte[] dhPublicKey, int messageNumber)
        => $"{Convert.ToHexString(dhPublicKey)}:{messageNumber}";

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_ownsSelfKey)
            _dhSelf.Dispose();
        CryptographicOperations.ZeroMemory(_rootKey);
        if (_sendChainKey != null) CryptographicOperations.ZeroMemory(_sendChainKey);
        if (_recvChainKey != null) CryptographicOperations.ZeroMemory(_recvChainKey);
        foreach (var mk in _skipped.Values) CryptographicOperations.ZeroMemory(mk);
        _skipped.Clear();
    }
}

public sealed class RatchetHeader
{
    public byte[] DhPublicKey { get; init; } = Array.Empty<byte>();
    public int PreviousChainLength { get; init; }
    public int MessageNumber { get; init; }

    public byte[] Serialize()
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);
        writer.Write((byte)DhPublicKey.Length);
        writer.Write(DhPublicKey);
        writer.Write(PreviousChainLength);
        writer.Write(MessageNumber);
        return ms.ToArray();
    }

    public static RatchetHeader Deserialize(byte[] data)
    {
        using var ms = new MemoryStream(data);
        using var reader = new BinaryReader(ms);
        var len = reader.ReadByte();
        if (len != 32)
            throw new InvalidDataException($"Invalid ratchet key length: {len}");
        var dh = reader.ReadBytes(32);
        var pn = reader.ReadInt32();
        var n = reader.ReadInt32();
        if (pn < 0 || n < 0)
            throw new InvalidDataException("Negative ratchet counter");
        return new RatchetHeader { DhPublicKey = dh, PreviousChainLength = pn, MessageNumber = n };
    }
}
