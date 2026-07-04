using System.Security.Cryptography;
using NSec.Cryptography;
using Susurri.Shared.Abstractions.Security;

namespace Susurri.Modules.DHT.Core.Onion.GroupChat;

public static class GroupRatchet
{
    private static readonly AeadAlgorithm Aead = AeadAlgorithm.ChaCha20Poly1305;

    public static byte[] DeriveMessageKey(byte[] chainKey, byte[] groupKey)
    {
        var key = new byte[32];
        HKDF.DeriveKey(HashAlgorithmName.SHA256, chainKey, key, groupKey, HkdfContexts.GroupMessageKey);
        return key;
    }

    public static byte[] AdvanceChain(byte[] chainKey)
    {
        var next = new byte[32];
        HKDF.DeriveKey(HashAlgorithmName.SHA256, chainKey, next, ReadOnlySpan<byte>.Empty, HkdfContexts.GroupSenderChain);
        return next;
    }

    public static byte[] GenerateNonce()
    {
        var nonce = new byte[Aead.NonceSize];
        RandomNumberGenerator.Fill(nonce);
        return nonce;
    }

    public static byte[] Encrypt(byte[] messageKey, byte[] nonce, byte[] associatedData, byte[] plaintext)
    {
        using var key = Key.Import(Aead, messageKey, KeyBlobFormat.RawSymmetricKey);
        return Aead.Encrypt(key, nonce, associatedData, plaintext);
    }

    public static byte[] Decrypt(byte[] messageKey, byte[] nonce, byte[] associatedData, byte[] ciphertext)
    {
        using var key = Key.Import(Aead, messageKey, KeyBlobFormat.RawSymmetricKey);
        var plaintext = Aead.Decrypt(key, nonce, associatedData, ciphertext);
        if (plaintext == null)
            throw new CryptographicException("Group message decryption failed");
        return plaintext;
    }
}

public sealed record GroupSendKeys(byte[] MessageKey, int Generation, uint Iteration, int KeyVersion, byte[] ChainKeySnapshot);

public sealed class GroupRatchetManager : IDisposable
{
    private const int MaxSkippedKeys = 512;
    private const uint RedistributeEvery = 16;
    public const uint SenderEpochMessages = 256;
    private static readonly TimeSpan SenderEpochAge = TimeSpan.FromHours(24);
    private const byte StateVersion = 2;

    private readonly object _gate = new();
    private readonly Dictionary<Guid, SenderState> _senders = new();
    private readonly Dictionary<string, ReceiverState> _receivers = new();
    private readonly byte[]? _storageKey;
    private readonly string? _statePath;
    private bool _disposed;

    public GroupRatchetManager(byte[]? localStoreKey, byte[] localPublicKey)
    {
        if (localStoreKey == null)
            return;

        _storageKey = LocalEncryption.DeriveSubkey(localStoreKey, HkdfContexts.LocalGroupRatchet);

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var directory = Path.Combine(appData, "Susurri", "groups");
        Directory.CreateDirectory(directory);
        LocalEncryption.RestrictDirectory(directory);
        _statePath = Path.Combine(directory,
            $"{Convert.ToHexString(localPublicKey)[..16].ToLowerInvariant()}.grs");

        Load();
    }

    public GroupSendKeys PrepareSend(GroupInfo group)
    {
        lock (_gate)
        {
            var state = EnsureSenderLocked(group);
            var snapshot = (byte[])state.ChainKey.Clone();
            var iteration = state.Iteration;

            var messageKey = GroupRatchet.DeriveMessageKey(state.ChainKey, group.Key.SymmetricKey);
            var next = GroupRatchet.AdvanceChain(state.ChainKey);
            CryptographicOperations.ZeroMemory(state.ChainKey);
            state.ChainKey = next;
            state.Iteration = iteration + 1;

            SaveLocked();
            return new GroupSendKeys(messageKey, state.Generation, iteration, state.KeyVersion, snapshot);
        }
    }

    public bool NeedsDistribution(GroupInfo group, byte[] memberPublicKey)
    {
        lock (_gate)
        {
            var state = EnsureSenderLocked(group);
            if (!state.Distributed.TryGetValue(Convert.ToHexString(memberPublicKey), out var lastIteration))
                return true;
            return state.Iteration - lastIteration >= RedistributeEvery;
        }
    }

    public void MarkDistributed(GroupInfo group, byte[] memberPublicKey, uint iteration)
    {
        lock (_gate)
        {
            var state = EnsureSenderLocked(group);
            state.Distributed[Convert.ToHexString(memberPublicKey)] = iteration;
            SaveLocked();
        }
    }

    public void AcceptDistribution(GroupInfo group, GroupSenderKeyDistribution distribution)
    {
        if (distribution.KeyVersion != group.Key.Version)
            return;

        lock (_gate)
        {
            var key = ReceiverKey(distribution.GroupId, distribution.SenderPublicKey);
            if (_receivers.TryGetValue(key, out var existing) && distribution.Generation <= existing.Generation)
                return;

            if (_receivers.Remove(key, out var replaced))
            {
                CryptographicOperations.ZeroMemory(replaced.ChainKey);
                foreach (var skipped in replaced.Skipped.Values)
                    CryptographicOperations.ZeroMemory(skipped);
            }

            _receivers[key] = new ReceiverState
            {
                ChainKey = (byte[])distribution.ChainKey.Clone(),
                Iteration = distribution.Iteration,
                Generation = distribution.Generation,
                KeyVersion = distribution.KeyVersion
            };
            SaveLocked();
        }
    }

    public byte[]? TryTakeMessageKey(GroupInfo group, byte[] senderPublicKey, int generation, uint iteration, int keyVersion)
    {
        if (keyVersion != group.Key.Version)
            return null;

        lock (_gate)
        {
            if (!_receivers.TryGetValue(ReceiverKey(group.GroupId, senderPublicKey), out var state))
                return null;

            if (state.Generation != generation || state.KeyVersion != keyVersion)
                return null;

            if (iteration < state.Iteration)
            {
                if (state.Skipped.Remove(iteration, out var skippedKey))
                {
                    SaveLocked();
                    return skippedKey;
                }
                return null;
            }

            if (iteration - state.Iteration > MaxSkippedKeys)
                return null;

            while (state.Iteration < iteration)
            {
                state.Skipped[state.Iteration] = GroupRatchet.DeriveMessageKey(state.ChainKey, group.Key.SymmetricKey);
                AdvanceLocked(state);
                if (state.Skipped.Count > MaxSkippedKeys)
                {
                    var oldest = state.Skipped.Keys.Min();
                    if (state.Skipped.Remove(oldest, out var evicted))
                        CryptographicOperations.ZeroMemory(evicted);
                }
            }

            var messageKey = GroupRatchet.DeriveMessageKey(state.ChainKey, group.Key.SymmetricKey);
            AdvanceLocked(state);
            SaveLocked();
            return messageKey;
        }
    }

    private static void AdvanceLocked(ReceiverState state)
    {
        var next = GroupRatchet.AdvanceChain(state.ChainKey);
        CryptographicOperations.ZeroMemory(state.ChainKey);
        state.ChainKey = next;
        state.Iteration++;
    }

    private SenderState EnsureSenderLocked(GroupInfo group)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        if (_senders.TryGetValue(group.GroupId, out var state) &&
            state.KeyVersion == group.Key.Version &&
            state.Iteration < SenderEpochMessages &&
            now - state.CreatedAt < (long)SenderEpochAge.TotalSeconds)
            return state;

        var previousGeneration = state?.Generation ?? 0;
        if (state != null)
            CryptographicOperations.ZeroMemory(state.ChainKey);

        var chainKey = new byte[32];
        RandomNumberGenerator.Fill(chainKey);

        state = new SenderState
        {
            ChainKey = chainKey,
            Iteration = 0,
            Generation = Math.Max((int)now, previousGeneration + 1),
            KeyVersion = group.Key.Version,
            CreatedAt = now
        };
        _senders[group.GroupId] = state;
        SaveLocked();
        return state;
    }

    private static string ReceiverKey(Guid groupId, byte[] senderPublicKey)
        => $"{groupId:N}:{Convert.ToHexString(senderPublicKey)}";

    private void Load()
    {
        if (_statePath == null || _storageKey == null || !File.Exists(_statePath))
            return;

        try
        {
            var payload = File.ReadAllBytes(_statePath);
            var data = LocalEncryption.Decrypt(_storageKey, payload);

            using var ms = new MemoryStream(data);
            using var reader = new BinaryReader(ms);

            if (reader.ReadByte() != StateVersion)
                return;

            var senderCount = reader.ReadInt32();
            for (var i = 0; i < senderCount; i++)
            {
                var groupId = new Guid(reader.ReadBytes(16));
                var state = new SenderState
                {
                    KeyVersion = reader.ReadInt32(),
                    Generation = reader.ReadInt32(),
                    CreatedAt = reader.ReadInt64(),
                    Iteration = reader.ReadUInt32(),
                    ChainKey = reader.ReadBytes(32)
                };
                var distributedCount = reader.ReadInt32();
                for (var j = 0; j < distributedCount; j++)
                {
                    var member = Convert.ToHexString(reader.ReadBytes(32));
                    state.Distributed[member] = reader.ReadUInt32();
                }
                _senders[groupId] = state;
            }

            var receiverCount = reader.ReadInt32();
            for (var i = 0; i < receiverCount; i++)
            {
                var groupId = new Guid(reader.ReadBytes(16));
                var senderPublicKey = reader.ReadBytes(32);
                var state = new ReceiverState
                {
                    KeyVersion = reader.ReadInt32(),
                    Generation = reader.ReadInt32(),
                    Iteration = reader.ReadUInt32(),
                    ChainKey = reader.ReadBytes(32)
                };
                var skippedCount = reader.ReadInt32();
                for (var j = 0; j < skippedCount; j++)
                {
                    var iteration = reader.ReadUInt32();
                    state.Skipped[iteration] = reader.ReadBytes(32);
                }
                _receivers[ReceiverKey(groupId, senderPublicKey)] = state;
            }
        }
        catch
        {
            _senders.Clear();
            _receivers.Clear();
        }
    }

    private void SaveLocked()
    {
        if (_statePath == null || _storageKey == null)
            return;

        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);

        writer.Write(StateVersion);
        writer.Write(_senders.Count);
        foreach (var (groupId, state) in _senders)
        {
            writer.Write(groupId.ToByteArray());
            writer.Write(state.KeyVersion);
            writer.Write(state.Generation);
            writer.Write(state.CreatedAt);
            writer.Write(state.Iteration);
            writer.Write(state.ChainKey);
            writer.Write(state.Distributed.Count);
            foreach (var (member, iteration) in state.Distributed)
            {
                writer.Write(Convert.FromHexString(member));
                writer.Write(iteration);
            }
        }

        writer.Write(_receivers.Count);
        foreach (var (key, state) in _receivers)
        {
            var separator = key.IndexOf(':');
            writer.Write(Guid.ParseExact(key[..separator], "N").ToByteArray());
            writer.Write(Convert.FromHexString(key[(separator + 1)..]));
            writer.Write(state.KeyVersion);
            writer.Write(state.Generation);
            writer.Write(state.Iteration);
            writer.Write(state.ChainKey);
            writer.Write(state.Skipped.Count);
            foreach (var (iteration, skippedKey) in state.Skipped)
            {
                writer.Write(iteration);
                writer.Write(skippedKey);
            }
        }

        writer.Flush();
        var plaintext = ms.ToArray();
        try
        {
            File.WriteAllBytes(_statePath, LocalEncryption.Encrypt(_storageKey, plaintext));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        lock (_gate)
        {
            foreach (var state in _senders.Values)
                CryptographicOperations.ZeroMemory(state.ChainKey);
            foreach (var state in _receivers.Values)
            {
                CryptographicOperations.ZeroMemory(state.ChainKey);
                foreach (var skipped in state.Skipped.Values)
                    CryptographicOperations.ZeroMemory(skipped);
            }
            _senders.Clear();
            _receivers.Clear();
            if (_storageKey != null)
                CryptographicOperations.ZeroMemory(_storageKey);
        }
    }

    private sealed class SenderState
    {
        public byte[] ChainKey { get; set; } = Array.Empty<byte>();
        public uint Iteration { get; set; }
        public int Generation { get; init; }
        public int KeyVersion { get; init; }
        public long CreatedAt { get; init; }
        public Dictionary<string, uint> Distributed { get; } = new();
    }

    private sealed class ReceiverState
    {
        public byte[] ChainKey { get; set; } = Array.Empty<byte>();
        public uint Iteration { get; set; }
        public int Generation { get; init; }
        public int KeyVersion { get; init; }
        public Dictionary<uint, byte[]> Skipped { get; } = new();
    }
}
