using System.Security.Cryptography;
using Susurri.Modules.DHT.Core.Services;
using Xunit;

namespace Susurri.Tests.Unit.Services;

public class FileTransferServiceTests
{
    #region Chunking Logic Tests

    [Theory]
    [InlineData(100, 1)]       // Small file = 1 chunk
    [InlineData(15800, 1)]     // Exactly one chunk
    [InlineData(15801, 2)]     // Just over one chunk
    [InlineData(31600, 2)]     // Exactly two chunks
    [InlineData(100000, 7)]    // ~6.33 chunks rounds up
    [InlineData(1048576, 67)]  // 1 MB
    public void ChunkCount_CalculatedCorrectly(int fileSize, int expectedChunks)
    {
        var chunkCount = (int)Math.Ceiling((double)fileSize / FileTransferService.DefaultChunkSize);
        Assert.Equal(expectedChunks, chunkCount);
    }

    [Fact]
    public void DefaultChunkSize_IsReasonable()
    {
        // Must be positive and less than the padding block max payload
        Assert.True(FileTransferService.DefaultChunkSize > 0);
        Assert.True(FileTransferService.DefaultChunkSize <= 16000);
    }

    #endregion

    #region File Reassembly Tests

    [Fact]
    public void FileReassembly_SmallFile_ProducesCorrectData()
    {
        var originalData = new byte[100];
        Random.Shared.NextBytes(originalData);

        // Simulate chunking
        var chunkSize = FileTransferService.DefaultChunkSize;
        var chunkCount = (int)Math.Ceiling((double)originalData.Length / chunkSize);
        var chunks = new Dictionary<int, byte[]>();

        for (int i = 0; i < chunkCount; i++)
        {
            var offset = i * chunkSize;
            var length = Math.Min(chunkSize, originalData.Length - offset);
            var chunk = new byte[length];
            Array.Copy(originalData, offset, chunk, 0, length);
            chunks[i] = chunk;
        }

        // Reassemble
        using var ms = new MemoryStream();
        for (int i = 0; i < chunkCount; i++)
            ms.Write(chunks[i]);
        var reassembled = ms.ToArray();

        Assert.Equal(originalData, reassembled);
    }

    [Fact]
    public void FileReassembly_LargeFile_ProducesCorrectData()
    {
        // 100 KB file
        var originalData = new byte[100_000];
        Random.Shared.NextBytes(originalData);

        var chunkSize = FileTransferService.DefaultChunkSize;
        var chunkCount = (int)Math.Ceiling((double)originalData.Length / chunkSize);
        var chunks = new Dictionary<int, byte[]>();

        for (int i = 0; i < chunkCount; i++)
        {
            var offset = i * chunkSize;
            var length = Math.Min(chunkSize, originalData.Length - offset);
            var chunk = new byte[length];
            Array.Copy(originalData, offset, chunk, 0, length);
            chunks[i] = chunk;
        }

        using var ms = new MemoryStream();
        for (int i = 0; i < chunkCount; i++)
            ms.Write(chunks[i]);
        var reassembled = ms.ToArray();

        Assert.Equal(originalData, reassembled);
        Assert.Equal(SHA256.HashData(originalData), SHA256.HashData(reassembled));
    }

    [Fact]
    public void FileReassembly_OutOfOrderChunks_ProducesCorrectData()
    {
        var originalData = new byte[50_000];
        Random.Shared.NextBytes(originalData);

        var chunkSize = FileTransferService.DefaultChunkSize;
        var chunkCount = (int)Math.Ceiling((double)originalData.Length / chunkSize);
        var chunks = new Dictionary<int, byte[]>();

        // Add chunks in reverse order
        for (int i = chunkCount - 1; i >= 0; i--)
        {
            var offset = i * chunkSize;
            var length = Math.Min(chunkSize, originalData.Length - offset);
            var chunk = new byte[length];
            Array.Copy(originalData, offset, chunk, 0, length);
            chunks[i] = chunk;
        }

        // Reassemble in correct order
        using var ms = new MemoryStream();
        for (int i = 0; i < chunkCount; i++)
            ms.Write(chunks[i]);
        var reassembled = ms.ToArray();

        Assert.Equal(originalData, reassembled);
    }

    #endregion

    #region Hash Verification Tests

    [Fact]
    public void SHA256Hash_MatchesAfterReassembly()
    {
        var originalData = new byte[75_000];
        Random.Shared.NextBytes(originalData);
        var originalHash = SHA256.HashData(originalData);

        var chunkSize = FileTransferService.DefaultChunkSize;
        var chunkCount = (int)Math.Ceiling((double)originalData.Length / chunkSize);

        using var ms = new MemoryStream();
        for (int i = 0; i < chunkCount; i++)
        {
            var offset = i * chunkSize;
            var length = Math.Min(chunkSize, originalData.Length - offset);
            ms.Write(originalData, offset, length);
        }

        var reassembledHash = SHA256.HashData(ms.ToArray());
        Assert.Equal(originalHash, reassembledHash);
    }

    [Fact]
    public void SHA256Hash_DiffersForCorruptedData()
    {
        var originalData = new byte[1000];
        Random.Shared.NextBytes(originalData);
        var originalHash = SHA256.HashData(originalData);

        // Corrupt one byte
        var corruptedData = (byte[])originalData.Clone();
        corruptedData[500] ^= 0xFF;

        var corruptedHash = SHA256.HashData(corruptedData);
        Assert.NotEqual(originalHash, corruptedHash);
    }

    #endregion

    #region TransferStatus and Info Tests

    [Fact]
    public void TransferProgress_Percentage_CalculatedCorrectly()
    {
        var progress = new TransferProgress
        {
            TransferId = Guid.NewGuid(),
            ChunksCompleted = 5,
            TotalChunks = 10
        };
        Assert.Equal(50.0, progress.Percentage);
    }

    [Fact]
    public void TransferProgress_ZeroChunks_ReturnsZeroPercentage()
    {
        var progress = new TransferProgress
        {
            TransferId = Guid.NewGuid(),
            ChunksCompleted = 0,
            TotalChunks = 0
        };
        Assert.Equal(0.0, progress.Percentage);
    }

    [Fact]
    public void TransferProgress_Complete_Returns100Percent()
    {
        var progress = new TransferProgress
        {
            TransferId = Guid.NewGuid(),
            ChunksCompleted = 7,
            TotalChunks = 7
        };
        Assert.Equal(100.0, progress.Percentage);
    }

    [Fact]
    public void TransferStatus_HasExpectedValues()
    {
        Assert.Equal(0, (int)TransferStatus.Requesting);
        Assert.Equal(1, (int)TransferStatus.Transferring);
        Assert.Equal(2, (int)TransferStatus.Completed);
        Assert.Equal(3, (int)TransferStatus.Failed);
    }

    [Fact]
    public void TransferDirection_HasExpectedValues()
    {
        Assert.Equal(0, (int)TransferDirection.Incoming);
        Assert.Equal(1, (int)TransferDirection.Outgoing);
    }

    #endregion
}
