using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using NSec.Cryptography;
using Susurri.Modules.DHT.Core.Kademlia;
using Susurri.Modules.DHT.Core.Onion;

namespace Susurri.Modules.DHT.Core.Services;

/// <summary>
/// Manages encrypted file transfers over onion routes.
///
/// Protocol flow:
///   1. Sender calls <see cref="SendFileAsync"/> which sends a FileTransferRequest
///   2. Recipient receives the request via <see cref="OnTransferRequested"/> event
///   3. Recipient calls <see cref="AcceptTransferAsync"/> or <see cref="RejectTransferAsync"/>
///   4. On accept, sender streams chunks via onion routing
///   5. Recipient reassembles and verifies SHA-256 hash
///   6. <see cref="OnTransferCompleted"/> fires with the received file data
/// </summary>
public sealed class FileTransferService : IDisposable
{
    private readonly KademliaDhtNode _dhtNode;
    private readonly OnionRouter _router;
    private readonly ILogger<FileTransferService> _logger;
    private readonly Key? _signingKey;
    private readonly Timer _janitor;

    /// <summary>
    /// Maximum chunk data size in bytes. Chosen to fit within the 16KB message padding block
    /// after accounting for serialization overhead (envelope + headers + signature ~ 182 bytes).
    /// </summary>
    public const int DefaultChunkSize = 15_800;

    /// <summary>
    /// Hard cap on file size to prevent memory-exhaustion attacks. 100 MB.
    /// </summary>
    public const long MaxFileSize = 100L * 1024 * 1024;

    /// <summary>
    /// Maximum filename length in characters.
    /// </summary>
    public const int MaxFileNameLength = 255;

    /// <summary>
    /// How long an incomplete transfer is kept before being cleaned up.
    /// </summary>
    public static readonly TimeSpan TransferTimeout = TimeSpan.FromMinutes(30);

    /// <summary>
    /// How many concurrent transfers a single sender pubkey can have in-flight.
    /// </summary>
    public const int MaxConcurrentTransfersPerSender = 5;

    private const int PathLength = 3;

    private readonly ConcurrentDictionary<Guid, OutgoingTransfer> _outgoing = new();
    private readonly ConcurrentDictionary<Guid, IncomingTransfer> _incoming = new();

    /// <summary>
    /// Fired when a file transfer request is received. The handler should call
    /// AcceptTransferAsync or RejectTransferAsync.
    /// </summary>
    public event Func<FileTransferInfo, Task>? OnTransferRequested;

    /// <summary>
    /// Fired when a file transfer completes successfully with the assembled file data.
    /// </summary>
    public event Func<CompletedTransfer, Task>? OnTransferCompleted;

    /// <summary>
    /// Fired when transfer progress updates (chunk received/sent).
    /// </summary>
    public event Func<TransferProgress, Task>? OnTransferProgress;

    /// <summary>
    /// Fired when a transfer fails.
    /// </summary>
    public event Func<Guid, string, Task>? OnTransferFailed;

    public byte[] LocalPublicKey => _dhtNode.EncryptionPublicKey;
    public byte[] LocalSigningPublicKey => _dhtNode.SigningPublicKey;

    public FileTransferService(
        KademliaDhtNode dhtNode,
        OnionRouter router,
        ILogger<FileTransferService> logger,
        Key? signingKey = null)
    {
        _dhtNode = dhtNode;
        _router = router;
        _logger = logger;
        _signingKey = signingKey;

        _router.OnFileTransferReceived += HandleFileTransferMessageAsync;

        _janitor = new Timer(_ => SweepStaleTransfers(), null,
            TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }

    private bool _disposed;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _janitor.Dispose();
    }

    /// <summary>
    /// Validates a filename for use in a file transfer. Rejects path traversal,
    /// control characters, NUL bytes, leading dot, and over-long names.
    /// </summary>
    public static bool IsValidFileName(string fileName)
    {
        if (string.IsNullOrEmpty(fileName))
            return false;
        if (fileName.Length > MaxFileNameLength)
            return false;
        if (fileName[0] == '.')
            return false;
        if (fileName.Contains('/') || fileName.Contains('\\'))
            return false;
        if (fileName.Contains('\0'))
            return false;
        foreach (var c in fileName)
        {
            if (char.IsControl(c))
                return false;
        }
        return true;
    }

    private void SweepStaleTransfers()
    {
        var cutoff = DateTimeOffset.UtcNow - TransferTimeout;

        foreach (var kvp in _outgoing)
        {
            if (kvp.Value.StartedAt < cutoff && kvp.Value.Status != TransferStatus.Completed)
            {
                if (_outgoing.TryRemove(kvp.Key, out var stale))
                {
                    stale.Status = TransferStatus.Failed;
                    _logger.LogWarning("Outgoing transfer {TransferId} timed out", kvp.Key);
                    OnTransferFailed?.Invoke(kvp.Key, "Transfer timed out");
                }
            }
        }

        foreach (var kvp in _incoming)
        {
            if (kvp.Value.StartedAt < cutoff && kvp.Value.Status != TransferStatus.Completed)
            {
                if (_incoming.TryRemove(kvp.Key, out var stale))
                {
                    stale.Status = TransferStatus.Failed;
                    _logger.LogWarning("Incoming transfer {TransferId} timed out", kvp.Key);
                    OnTransferFailed?.Invoke(kvp.Key, "Transfer timed out");
                }
            }
        }
    }

    private bool SenderUnderConcurrencyCap(byte[] senderPublicKey)
    {
        if (senderPublicKey.Length == 0)
            return true;

        int active = 0;
        foreach (var t in _incoming.Values)
        {
            if (t.SenderPublicKey.AsSpan().SequenceEqual(senderPublicKey)
                && t.Status != TransferStatus.Completed
                && t.Status != TransferStatus.Failed)
            {
                active++;
            }
        }
        return active < MaxConcurrentTransfersPerSender;
    }

    /// <summary>
    /// Sends a file to a recipient identified by their encryption public key.
    /// Returns the transfer ID for tracking progress.
    /// </summary>
    public async Task<SendResult> SendFileAsync(
        string fileName,
        byte[] fileData,
        byte[] recipientPublicKey)
    {
        if (_signingKey == null)
            return new SendResult(false, null, "Cannot send files without a signing key");

        if (!IsValidFileName(fileName))
            return new SendResult(false, null, "Invalid file name");

        if (fileData.Length == 0)
            return new SendResult(false, null, "File is empty");

        if (fileData.Length > MaxFileSize)
            return new SendResult(false, null, $"File exceeds maximum size of {MaxFileSize} bytes");

        var transferId = Guid.NewGuid();
        var fileHash = SHA256.HashData(fileData);
        var chunkCount = (int)Math.Ceiling((double)fileData.Length / DefaultChunkSize);

        var transfer = new OutgoingTransfer
        {
            TransferId = transferId,
            FileName = fileName,
            FileData = fileData,
            FileHash = fileHash,
            RecipientPublicKey = recipientPublicKey,
            ChunkCount = Math.Max(chunkCount, 1),
            Status = TransferStatus.Requesting,
            StartedAt = DateTimeOffset.UtcNow
        };

        _outgoing[transferId] = transfer;

        // Send transfer request
        var request = CreateMessage<FileTransferRequest>(msg => msg with
        {
            TransferId = transferId,
            FileName = fileName,
            FileSize = fileData.Length,
            FileHash = fileHash,
            ChunkSize = DefaultChunkSize,
            ChunkCount = transfer.ChunkCount
        });

        try
        {
            await SendFileMessageAsync(request, recipientPublicKey).ConfigureAwait(false);
            _logger.LogInformation(
                "File transfer request {TransferId} sent: {FileName} ({Size} bytes, {Chunks} chunks)",
                transferId, fileName, fileData.Length, transfer.ChunkCount);

            return new SendResult(true, transferId, null);
        }
        catch (Exception ex)
        {
            _outgoing.TryRemove(transferId, out _);
            _logger.LogError(ex, "Failed to send file transfer request");
            return new SendResult(false, transferId, ex.Message);
        }
    }

    /// <summary>
    /// Accepts a pending incoming file transfer.
    /// </summary>
    public async Task AcceptTransferAsync(Guid transferId)
    {
        if (!_incoming.TryGetValue(transferId, out var transfer))
        {
            _logger.LogWarning("Cannot accept unknown transfer {TransferId}", transferId);
            return;
        }

        var accept = CreateMessage<FileTransferAccept>(msg => msg with
        {
            TransferId = transferId
        });

        transfer.Status = TransferStatus.Transferring;
        await SendFileMessageAsync(accept, transfer.SenderPublicKey).ConfigureAwait(false);
        _logger.LogInformation("Accepted file transfer {TransferId}", transferId);
    }

    /// <summary>
    /// Rejects a pending incoming file transfer.
    /// </summary>
    public async Task RejectTransferAsync(Guid transferId, string reason = "Rejected by user")
    {
        if (!_incoming.TryRemove(transferId, out var transfer))
        {
            _logger.LogWarning("Cannot reject unknown transfer {TransferId}", transferId);
            return;
        }

        var reject = CreateMessage<FileTransferReject>(msg => msg with
        {
            TransferId = transferId,
            Reason = reason
        });

        await SendFileMessageAsync(reject, transfer.SenderPublicKey).ConfigureAwait(false);
        _logger.LogInformation("Rejected file transfer {TransferId}: {Reason}", transferId, reason);
    }

    public IReadOnlyList<FileTransferInfo> GetActiveTransfers()
    {
        var transfers = new List<FileTransferInfo>();

        foreach (var t in _outgoing.Values)
        {
            transfers.Add(new FileTransferInfo
            {
                TransferId = t.TransferId,
                FileName = t.FileName,
                FileSize = t.FileData.Length,
                ChunkCount = t.ChunkCount,
                ChunksTransferred = t.ChunksSent,
                Direction = TransferDirection.Outgoing,
                Status = t.Status
            });
        }

        foreach (var t in _incoming.Values)
        {
            transfers.Add(new FileTransferInfo
            {
                TransferId = t.TransferId,
                FileName = t.FileName,
                FileSize = t.FileSize,
                ChunkCount = t.ChunkCount,
                ChunksTransferred = t.ReceivedChunks.Count,
                Direction = TransferDirection.Incoming,
                Status = t.Status
            });
        }

        return transfers;
    }

    private async Task HandleFileTransferMessageAsync(FileTransferMessage message, ReplyPath replyPath)
    {
        switch (message)
        {
            case FileTransferRequest request:
                await HandleTransferRequestAsync(request).ConfigureAwait(false);
                break;

            case FileTransferAccept accept:
                await HandleTransferAcceptAsync(accept).ConfigureAwait(false);
                break;

            case FileTransferReject reject:
                HandleTransferReject(reject);
                break;

            case FileChunkMessage chunk:
                await HandleChunkAsync(chunk).ConfigureAwait(false);
                break;

            case FileTransferComplete complete:
                await HandleTransferCompleteAsync(complete).ConfigureAwait(false);
                break;
        }
    }

    private async Task HandleTransferRequestAsync(FileTransferRequest request)
    {
        if (!IsValidFileName(request.FileName))
        {
            _logger.LogWarning("Rejected file transfer request {TransferId}: invalid filename",
                request.TransferId);
            await SendRejectAsync(request, "Invalid filename").ConfigureAwait(false);
            return;
        }

        if (request.FileSize <= 0 || request.FileSize > MaxFileSize)
        {
            _logger.LogWarning("Rejected file transfer request {TransferId}: invalid file size {Size}",
                request.TransferId, request.FileSize);
            await SendRejectAsync(request, "Invalid file size").ConfigureAwait(false);
            return;
        }

        if (request.ChunkSize <= 0 || request.ChunkSize > DefaultChunkSize)
        {
            _logger.LogWarning("Rejected file transfer request {TransferId}: invalid chunk size {Size}",
                request.TransferId, request.ChunkSize);
            await SendRejectAsync(request, "Invalid chunk size").ConfigureAwait(false);
            return;
        }

        long expectedChunkCount = (request.FileSize + request.ChunkSize - 1) / request.ChunkSize;
        if (request.ChunkCount != expectedChunkCount || request.ChunkCount <= 0)
        {
            _logger.LogWarning("Rejected file transfer request {TransferId}: chunk count mismatch",
                request.TransferId);
            await SendRejectAsync(request, "Chunk count mismatch").ConfigureAwait(false);
            return;
        }

        if (!SenderUnderConcurrencyCap(request.SenderPublicKey))
        {
            _logger.LogWarning("Rejected file transfer request {TransferId}: sender exceeds concurrency cap",
                request.TransferId);
            await SendRejectAsync(request, "Too many concurrent transfers").ConfigureAwait(false);
            return;
        }

        _logger.LogInformation(
            "Received file transfer request {TransferId}: {FileName} ({Size} bytes, {Chunks} chunks)",
            request.TransferId, request.FileName, request.FileSize, request.ChunkCount);

        var transfer = new IncomingTransfer
        {
            TransferId = request.TransferId,
            FileName = request.FileName,
            FileSize = request.FileSize,
            FileHash = request.FileHash,
            ChunkSize = request.ChunkSize,
            ChunkCount = request.ChunkCount,
            SenderPublicKey = request.SenderPublicKey,
            Status = TransferStatus.Requesting,
            StartedAt = DateTimeOffset.UtcNow
        };

        _incoming[request.TransferId] = transfer;

        if (OnTransferRequested != null)
        {
            await OnTransferRequested(new FileTransferInfo
            {
                TransferId = request.TransferId,
                FileName = request.FileName,
                FileSize = request.FileSize,
                ChunkCount = request.ChunkCount,
                Direction = TransferDirection.Incoming,
                Status = TransferStatus.Requesting
            }).ConfigureAwait(false);
        }
    }

    private async Task SendRejectAsync(FileTransferRequest request, string reason)
    {
        if (_signingKey == null)
            return;

        try
        {
            var reject = CreateMessage<FileTransferReject>(msg => msg with
            {
                TransferId = request.TransferId,
                Reason = reason
            });
            await SendFileMessageAsync(reject, request.SenderPublicKey).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to send reject for transfer {TransferId}", request.TransferId);
        }
    }

    private async Task HandleTransferAcceptAsync(FileTransferAccept accept)
    {
        if (!_outgoing.TryGetValue(accept.TransferId, out var transfer))
        {
            _logger.LogWarning("Received accept for unknown transfer {TransferId}", accept.TransferId);
            return;
        }

        _logger.LogInformation("Transfer {TransferId} accepted, starting chunk send", accept.TransferId);
        transfer.Status = TransferStatus.Transferring;

        // Send all chunks
        await SendChunksAsync(transfer).ConfigureAwait(false);
    }

    private void HandleTransferReject(FileTransferReject reject)
    {
        if (_outgoing.TryRemove(reject.TransferId, out var transfer))
        {
            transfer.Status = TransferStatus.Failed;
            _logger.LogInformation("Transfer {TransferId} rejected: {Reason}",
                reject.TransferId, reject.Reason);

            OnTransferFailed?.Invoke(reject.TransferId, reject.Reason);
        }
    }

    private async Task HandleChunkAsync(FileChunkMessage chunk)
    {
        if (!_incoming.TryGetValue(chunk.TransferId, out var transfer))
        {
            _logger.LogWarning("Received chunk for unknown transfer {TransferId}", chunk.TransferId);
            return;
        }

        if (transfer.Status != TransferStatus.Transferring)
        {
            _logger.LogWarning("Received chunk for transfer {TransferId} not in transferring state",
                chunk.TransferId);
            return;
        }

        if (!chunk.SenderPublicKey.AsSpan().SequenceEqual(transfer.SenderPublicKey))
        {
            _logger.LogWarning("Chunk for transfer {TransferId} from wrong sender; ignoring",
                chunk.TransferId);
            return;
        }

        if (chunk.ChunkIndex < 0 || chunk.ChunkIndex >= transfer.ChunkCount)
        {
            _logger.LogWarning("Invalid chunk index {Index} for transfer {TransferId}",
                chunk.ChunkIndex, chunk.TransferId);
            return;
        }

        if (chunk.Data.Length > transfer.ChunkSize)
        {
            _logger.LogWarning("Chunk {Index} for transfer {TransferId} exceeds chunk size",
                chunk.ChunkIndex, chunk.TransferId);
            return;
        }

        // Reject duplicate chunks (replay or sender bug)
        if (transfer.ReceivedChunks.ContainsKey(chunk.ChunkIndex))
        {
            _logger.LogDebug("Ignoring duplicate chunk {Index} for transfer {TransferId}",
                chunk.ChunkIndex, chunk.TransferId);
            return;
        }

        // Validate cumulative byte count never exceeds the advertised file size
        long projectedTotal = transfer.ReceivedBytes + chunk.Data.Length;
        if (projectedTotal > transfer.FileSize)
        {
            _logger.LogWarning("Transfer {TransferId} aborted: cumulative bytes exceed declared FileSize",
                chunk.TransferId);
            transfer.Status = TransferStatus.Failed;
            _incoming.TryRemove(chunk.TransferId, out _);
            OnTransferFailed?.Invoke(chunk.TransferId, "Sender exceeded declared file size");
            return;
        }

        transfer.ReceivedBytes = projectedTotal;
        transfer.ReceivedChunks[chunk.ChunkIndex] = chunk.Data;

        _logger.LogDebug("Received chunk {Index}/{Total} for transfer {TransferId}",
            chunk.ChunkIndex + 1, transfer.ChunkCount, chunk.TransferId);

        if (OnTransferProgress != null)
        {
            await OnTransferProgress(new TransferProgress
            {
                TransferId = chunk.TransferId,
                ChunksCompleted = transfer.ReceivedChunks.Count,
                TotalChunks = transfer.ChunkCount
            }).ConfigureAwait(false);
        }
    }

    private async Task HandleTransferCompleteAsync(FileTransferComplete complete)
    {
        if (!_incoming.TryRemove(complete.TransferId, out var transfer))
        {
            _logger.LogWarning("Received complete for unknown transfer {TransferId}", complete.TransferId);
            return;
        }

        // Check if we have all chunks
        if (transfer.ReceivedChunks.Count != transfer.ChunkCount)
        {
            _logger.LogWarning(
                "Transfer {TransferId} completed but missing chunks: {Received}/{Total}",
                complete.TransferId, transfer.ReceivedChunks.Count, transfer.ChunkCount);

            transfer.Status = TransferStatus.Failed;
            OnTransferFailed?.Invoke(complete.TransferId, "Missing chunks");
            return;
        }

        // Reassemble file
        var fileData = ReassembleFile(transfer);

        // Verify hash
        var actualHash = SHA256.HashData(fileData);
        if (!actualHash.SequenceEqual(transfer.FileHash))
        {
            _logger.LogWarning("Transfer {TransferId} hash mismatch", complete.TransferId);
            transfer.Status = TransferStatus.Failed;
            OnTransferFailed?.Invoke(complete.TransferId, "File hash verification failed");
            return;
        }

        transfer.Status = TransferStatus.Completed;

        _logger.LogInformation("File transfer {TransferId} completed: {FileName} ({Size} bytes)",
            complete.TransferId, transfer.FileName, fileData.Length);

        if (OnTransferCompleted != null)
        {
            await OnTransferCompleted(new CompletedTransfer
            {
                TransferId = complete.TransferId,
                FileName = transfer.FileName,
                FileData = fileData,
                SenderPublicKey = transfer.SenderPublicKey
            }).ConfigureAwait(false);
        }
    }

    private async Task SendChunksAsync(OutgoingTransfer transfer)
    {
        try
        {
            for (int i = 0; i < transfer.ChunkCount; i++)
            {
                var offset = i * DefaultChunkSize;
                var length = Math.Min(DefaultChunkSize, transfer.FileData.Length - offset);
                var chunkData = new byte[length];
                Array.Copy(transfer.FileData, offset, chunkData, 0, length);

                var chunk = CreateMessage<FileChunkMessage>(msg => msg with
                {
                    TransferId = transfer.TransferId,
                    ChunkIndex = i,
                    Data = chunkData
                });

                await SendFileMessageAsync(chunk, transfer.RecipientPublicKey).ConfigureAwait(false);
                transfer.ChunksSent = i + 1;

                if (OnTransferProgress != null)
                {
                    await OnTransferProgress(new TransferProgress
                    {
                        TransferId = transfer.TransferId,
                        ChunksCompleted = transfer.ChunksSent,
                        TotalChunks = transfer.ChunkCount
                    }).ConfigureAwait(false);
                }

                _logger.LogDebug("Sent chunk {Index}/{Total} for transfer {TransferId}",
                    i + 1, transfer.ChunkCount, transfer.TransferId);
            }

            // Send completion message
            var complete = CreateMessage<FileTransferComplete>(msg => msg with
            {
                TransferId = transfer.TransferId
            });

            await SendFileMessageAsync(complete, transfer.RecipientPublicKey).ConfigureAwait(false);
            transfer.Status = TransferStatus.Completed;

            _logger.LogInformation("File transfer {TransferId} completed sending: {FileName}",
                transfer.TransferId, transfer.FileName);

            _outgoing.TryRemove(transfer.TransferId, out _);
        }
        catch (Exception ex)
        {
            transfer.Status = TransferStatus.Failed;
            _logger.LogError(ex, "Failed to send chunks for transfer {TransferId}", transfer.TransferId);
            OnTransferFailed?.Invoke(transfer.TransferId, ex.Message);
        }
    }

    private async Task SendFileMessageAsync(FileTransferMessage message, byte[] recipientPublicKey)
    {
        var path = _dhtNode.GetRandomNodesForPath(PathLength);
        if (path.Count == 0)
            throw new InvalidOperationException("No peers available for routing");

        var payload = message.Serialize();
        await _router.SendRawAsync(payload, recipientPublicKey, path).ConfigureAwait(false);
    }

    private T CreateMessage<T>(Func<T, T> configure) where T : FileTransferMessage, new()
    {
        var message = new T
        {
            SenderPublicKey = LocalPublicKey,
            SenderSigningPublicKey = LocalSigningPublicKey,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            MessageId = Guid.NewGuid()
        };

        message = configure(message);

        if (_signingKey != null)
        {
            message.Sign(_signingKey);
        }

        return message;
    }

    private static byte[] ReassembleFile(IncomingTransfer transfer)
    {
        using var ms = new MemoryStream((int)transfer.FileSize);

        for (int i = 0; i < transfer.ChunkCount; i++)
        {
            if (transfer.ReceivedChunks.TryGetValue(i, out var chunk))
            {
                ms.Write(chunk);
            }
        }

        return ms.ToArray();
    }

    private sealed class OutgoingTransfer
    {
        public Guid TransferId { get; init; }
        public string FileName { get; init; } = string.Empty;
        public byte[] FileData { get; init; } = Array.Empty<byte>();
        public byte[] FileHash { get; init; } = Array.Empty<byte>();
        public byte[] RecipientPublicKey { get; init; } = Array.Empty<byte>();
        public int ChunkCount { get; init; }
        public int ChunksSent { get; set; }
        public TransferStatus Status { get; set; }
        public DateTimeOffset StartedAt { get; init; }
    }

    private sealed class IncomingTransfer
    {
        public Guid TransferId { get; init; }
        public string FileName { get; init; } = string.Empty;
        public long FileSize { get; init; }
        public byte[] FileHash { get; init; } = Array.Empty<byte>();
        public int ChunkSize { get; init; }
        public int ChunkCount { get; init; }
        public byte[] SenderPublicKey { get; init; } = Array.Empty<byte>();
        public ConcurrentDictionary<int, byte[]> ReceivedChunks { get; } = new();
        public long ReceivedBytes { get; set; }
        public TransferStatus Status { get; set; }
        public DateTimeOffset StartedAt { get; init; }
    }
}

public sealed class FileTransferInfo
{
    public Guid TransferId { get; init; }
    public string FileName { get; init; } = string.Empty;
    public long FileSize { get; init; }
    public int ChunkCount { get; init; }
    public int ChunksTransferred { get; init; }
    public TransferDirection Direction { get; init; }
    public TransferStatus Status { get; init; }
}

public sealed class CompletedTransfer
{
    public Guid TransferId { get; init; }
    public string FileName { get; init; } = string.Empty;
    public byte[] FileData { get; init; } = Array.Empty<byte>();
    public byte[] SenderPublicKey { get; init; } = Array.Empty<byte>();
}

public sealed class TransferProgress
{
    public Guid TransferId { get; init; }
    public int ChunksCompleted { get; init; }
    public int TotalChunks { get; init; }
    public double Percentage => TotalChunks > 0 ? (double)ChunksCompleted / TotalChunks * 100 : 0;
}

public enum TransferDirection
{
    Incoming,
    Outgoing
}

public enum TransferStatus
{
    Requesting,
    Transferring,
    Completed,
    Failed
}
