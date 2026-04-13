using System.Net;
using Microsoft.Extensions.Logging;
using NSec.Cryptography;
using Susurri.Modules.DHT.Core.Kademlia.Protocol;
using Susurri.Modules.DHT.Core.Kademlia.Storage;
using Susurri.Modules.DHT.Core.Network;
using Susurri.Shared.Abstractions.Logging;

namespace Susurri.Modules.DHT.Core.Kademlia;

/// <summary>
/// Encapsulates DHT offline-message storage and retrieval, including the
/// signature-and-timestamp authentication required by both endpoints.
/// Extracted from <see cref="KademliaDhtNode"/> so the security-sensitive
/// auth surface is in one place.
/// </summary>
internal sealed class OfflineMessageService
{
    private static readonly TimeSpan TimestampWindow = TimeSpan.FromMinutes(5);
    private const int K = 20;

    private readonly IDhtStorage _storage;
    private readonly RateLimiter _pubkeyRateLimiter;
    private readonly KademliaId _localId;
    private readonly byte[] _encryptionPublicKey;
    private readonly Key? _signingKey;
    private readonly byte[] _signingPublicKey;
    private readonly Func<IPEndPoint, KademliaMessage, Task<KademliaMessage?>> _sendRequest;
    private readonly Func<KademliaId, Task<IReadOnlyList<KademliaNode>>> _findClosestNodes;
    private readonly ILogger _logger;

    public OfflineMessageService(
        IDhtStorage storage,
        RateLimiter pubkeyRateLimiter,
        KademliaId localId,
        byte[] encryptionPublicKey,
        Key? signingKey,
        byte[] signingPublicKey,
        Func<IPEndPoint, KademliaMessage, Task<KademliaMessage?>> sendRequest,
        Func<KademliaId, Task<IReadOnlyList<KademliaNode>>> findClosestNodes,
        ILogger logger)
    {
        _storage = storage;
        _pubkeyRateLimiter = pubkeyRateLimiter;
        _localId = localId;
        _encryptionPublicKey = encryptionPublicKey;
        _signingKey = signingKey;
        _signingPublicKey = signingPublicKey;
        _sendRequest = sendRequest;
        _findClosestNodes = findClosestNodes;
        _logger = logger;
    }

    public async Task StoreOfflineAsync(byte[] recipientPublicKey, byte[] encryptedMessage)
    {
        if (_signingKey == null)
            throw new InvalidOperationException("Signing key is required to store offline messages");

        var key = KademliaId.FromPublicKey(recipientPublicKey);
        _storage.StoreOfflineMessage(key, encryptedMessage);

        var closestNodes = await _findClosestNodes(key).ConfigureAwait(false);
        foreach (var node in closestNodes.Take(K / 2))
        {
            try
            {
                var request = CreateSignedStore(recipientPublicKey, encryptedMessage);
                await _sendRequest(node.EndPoint, request).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to store offline message on node {NodeId}", node.Id.ToString()[..16]);
            }
        }
    }

    public async Task<IReadOnlyList<byte[]>> GetOfflineAsync()
    {
        var key = KademliaId.FromPublicKey(_encryptionPublicKey);
        var messages = new List<byte[]>();
        messages.AddRange(_storage.GetOfflineMessages(key));

        if (_signingKey == null)
        {
            _logger.LogWarning("Cannot fetch remote offline messages: no signing key available for authentication");
            return messages;
        }

        var closestNodes = await _findClosestNodes(key).ConfigureAwait(false);
        foreach (var node in closestNodes.Take(K / 2))
        {
            try
            {
                var request = CreateSignedGet();
                var response = await _sendRequest(node.EndPoint, request).ConfigureAwait(false) as OfflineMessagesResponseMessage;
                if (response != null)
                {
                    messages.AddRange(response.Messages);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to get offline messages from node {NodeId}", node.Id.ToString()[..16]);
            }
        }

        return messages;
    }

    public StoreResponseMessage HandleStore(StoreOfflineMessageMessage msg)
    {
        if (!VerifyStoreAuth(msg))
        {
            _logger.LogWarning("Rejected unauthenticated StoreOfflineMessage from {Sender}",
                msg.SenderId.ToString()[..16]);

            return new StoreResponseMessage
            {
                SenderId = _localId,
                SenderPublicKey = _encryptionPublicKey,
                InResponseTo = msg.MessageId,
                Success = false,
                Error = "Authentication required"
            };
        }

        if (!_pubkeyRateLimiter.IsAllowed(Convert.ToHexString(msg.SigningPublicKey)))
        {
            _logger.LogWarning("Rate limited StoreOfflineMessage from signing key {KeyFingerprint}",
                LogRedaction.KeyFingerprint(msg.SigningPublicKey));

            return new StoreResponseMessage
            {
                SenderId = _localId,
                SenderPublicKey = _encryptionPublicKey,
                InResponseTo = msg.MessageId,
                Success = false,
                Error = "Rate limited"
            };
        }

        try
        {
            var key = KademliaId.FromPublicKey(msg.RecipientPublicKey);
            _storage.StoreOfflineMessage(key, msg.EncryptedMessage);

            return new StoreResponseMessage
            {
                SenderId = _localId,
                SenderPublicKey = _encryptionPublicKey,
                InResponseTo = msg.MessageId,
                Success = true
            };
        }
        catch (Exception ex)
        {
            return new StoreResponseMessage
            {
                SenderId = _localId,
                SenderPublicKey = _encryptionPublicKey,
                InResponseTo = msg.MessageId,
                Success = false,
                Error = ex.Message
            };
        }
    }

    public OfflineMessagesResponseMessage HandleGet(GetOfflineMessagesMessage msg)
    {
        if (!VerifyGetAuth(msg))
        {
            _logger.LogWarning("Rejected unauthenticated or invalid GetOfflineMessages request from {SenderId}",
                msg.SenderId.ToString()[..16]);

            return new OfflineMessagesResponseMessage
            {
                SenderId = _localId,
                SenderPublicKey = _encryptionPublicKey,
                InResponseTo = msg.MessageId,
                Messages = new List<byte[]>()
            };
        }

        var key = KademliaId.FromPublicKey(msg.RecipientPublicKey);
        var messages = _storage.GetOfflineMessages(key);

        return new OfflineMessagesResponseMessage
        {
            SenderId = _localId,
            SenderPublicKey = _encryptionPublicKey,
            InResponseTo = msg.MessageId,
            Messages = messages.ToList()
        };
    }

    private bool VerifyStoreAuth(StoreOfflineMessageMessage msg)
    {
        if (msg.SigningPublicKey.Length == 0 || msg.Signature.Length == 0)
            return false;

        if (!MessageReplayCache.IsTimestampFresh(msg.Timestamp, TimestampWindow))
        {
            _logger.LogWarning("StoreOfflineMessage has stale timestamp (Δ={Delta}s)",
                Math.Abs(DateTimeOffset.UtcNow.ToUnixTimeSeconds() - msg.Timestamp));
            return false;
        }

        try
        {
            var signingPubKey = PublicKey.Import(
                SignatureAlgorithm.Ed25519,
                msg.SigningPublicKey,
                KeyBlobFormat.RawPublicKey);

            return SignatureAlgorithm.Ed25519.Verify(signingPubKey, msg.GetSignableData(), msg.Signature);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "StoreOfflineMessage signature verification failed");
            return false;
        }
    }

    private bool VerifyGetAuth(GetOfflineMessagesMessage msg)
    {
        if (msg.SigningPublicKey.Length == 0 || msg.Signature.Length == 0)
            return false;

        if (!MessageReplayCache.IsTimestampFresh(msg.Timestamp, TimestampWindow))
        {
            var delta = Math.Abs(DateTimeOffset.UtcNow.ToUnixTimeSeconds() - msg.Timestamp);
            _logger.LogWarning("GetOfflineMessages request has stale timestamp (delta={Delta}s)", delta);
            return false;
        }

        try
        {
            var signingPubKey = PublicKey.Import(
                SignatureAlgorithm.Ed25519,
                msg.SigningPublicKey,
                KeyBlobFormat.RawPublicKey);

            return SignatureAlgorithm.Ed25519.Verify(
                signingPubKey, msg.GetSignableData(), msg.Signature);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to verify GetOfflineMessages signature");
            return false;
        }
    }

    private StoreOfflineMessageMessage CreateSignedStore(byte[] recipientPublicKey, byte[] encryptedMessage)
    {
        var draft = new StoreOfflineMessageMessage
        {
            SenderId = _localId,
            SenderPublicKey = _encryptionPublicKey,
            RecipientPublicKey = recipientPublicKey,
            EncryptedMessage = encryptedMessage,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            SigningPublicKey = _signingPublicKey
        };

        var signature = SignatureAlgorithm.Ed25519.Sign(_signingKey!, draft.GetSignableData());

        return new StoreOfflineMessageMessage
        {
            MessageId = draft.MessageId,
            SenderId = draft.SenderId,
            SenderPublicKey = draft.SenderPublicKey,
            RecipientPublicKey = draft.RecipientPublicKey,
            EncryptedMessage = draft.EncryptedMessage,
            Timestamp = draft.Timestamp,
            SigningPublicKey = draft.SigningPublicKey,
            Signature = signature
        };
    }

    private GetOfflineMessagesMessage CreateSignedGet()
    {
        var draft = new GetOfflineMessagesMessage
        {
            SenderId = _localId,
            SenderPublicKey = _encryptionPublicKey,
            RecipientPublicKey = _encryptionPublicKey,
            SigningPublicKey = _signingPublicKey,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        };

        var signature = SignatureAlgorithm.Ed25519.Sign(_signingKey!, draft.GetSignableData());

        return new GetOfflineMessagesMessage
        {
            MessageId = draft.MessageId,
            SenderId = draft.SenderId,
            SenderPublicKey = draft.SenderPublicKey,
            RecipientPublicKey = draft.RecipientPublicKey,
            SigningPublicKey = draft.SigningPublicKey,
            Timestamp = draft.Timestamp,
            Signature = signature
        };
    }
}
