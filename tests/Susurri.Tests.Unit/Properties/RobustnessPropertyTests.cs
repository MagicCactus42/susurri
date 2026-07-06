using CsCheck;
using Susurri.Modules.DHT.Core.Kademlia;
using Susurri.Modules.DHT.Core.Kademlia.Protocol;
using Susurri.Modules.DHT.Core.Onion;
using Susurri.Modules.DHT.Core.Services;
using Xunit;

namespace Susurri.Tests.Unit.Properties;

/// <summary>
/// Property: for arbitrary byte input, every Deserialize call either returns
/// a (possibly nonsensical but well-formed) object, OR throws one of a known
/// set of "graceful rejection" exception types. Anything else — NRE, IOOR,
/// OutOfMemoryException, StackOverflowException — is a parser bug and fails
/// the property.
///
/// We use a small fuzzing budget per test (200 random inputs each) which is
/// fast (&lt;1s typically) but still catches the obvious classes of parser
/// bugs: integer overflow, unbounded ReadBytes, type-tag dispatch errors.
/// Heavier coverage is the job of Phase 3.4's dedicated SharpFuzz harness.
/// </summary>
public class RobustnessPropertyTests
{
    /// <summary>
    /// Exceptions that count as "well-formed rejection". Anything outside
    /// this set propagates and fails the property.
    /// </summary>
    private static bool IsGracefulRejection(Exception ex) => ex is
        InvalidDataException
        or EndOfStreamException
        or OverflowException
        or ArgumentException
        or FormatException
        or IOException;

    /// <summary>Runs <paramref name="action"/> and rethrows on unexpected exceptions.</summary>
    private static void AssertGracefulOrSucceeds(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex) when (IsGracefulRejection(ex))
        {
            // Expected: parser refused malformed input cleanly.
        }
    }

    private static readonly Gen<byte[]> SmallBytes = Gen.Byte.Array[0, 256];
    private static readonly Gen<byte[]> MediumBytes = Gen.Byte.Array[0, 4096];
    private static readonly Gen<byte[]> LargeBytes = Gen.Byte.Array[0, 64 * 1024];

    [Fact]
    public void KademliaMessage_Deserialize_Rejects_Random_Bytes_Gracefully()
    {
        MediumBytes.Sample(
            bytes => AssertGracefulOrSucceeds(() => KademliaMessage.Deserialize(bytes)),
            iter: 200);
    }

    [Fact]
    public void OnionLayer_Deserialize_Rejects_Random_Bytes_Gracefully()
    {
        SmallBytes.Sample(
            bytes => AssertGracefulOrSucceeds(() => OnionLayer.Deserialize(bytes)),
            iter: 200);
    }

    [Fact]
    public void OnionLayerContent_Deserialize_Rejects_Random_Bytes_Gracefully()
    {
        SmallBytes.Sample(
            bytes => AssertGracefulOrSucceeds(() => OnionLayerContent.Deserialize(bytes)),
            iter: 200);
    }

    [Fact]
    public void ChatMessage_Deserialize_Rejects_Random_Bytes_Gracefully()
    {
        SmallBytes.Sample(
            bytes => AssertGracefulOrSucceeds(() => ChatMessage.Deserialize(bytes)),
            iter: 200);
    }

    [Fact]
    public void UserPublicKeyRecord_Deserialize_Rejects_Random_Bytes_Gracefully()
    {
        SmallBytes.Sample(
            bytes => AssertGracefulOrSucceeds(() => UserPublicKeyRecord.Deserialize(bytes)),
            iter: 200);
    }

    [Fact]
    public void RecipientPayload_Deserialize_Rejects_Random_Bytes_Gracefully()
    {
        MediumBytes.Sample(
            bytes => AssertGracefulOrSucceeds(() => RecipientPayload.Deserialize(bytes)),
            iter: 200);
    }

    [Fact]
    public void ReplyPath_Deserialize_Rejects_Random_Bytes_Gracefully()
    {
        SmallBytes.Sample(
            bytes => AssertGracefulOrSucceeds(() => ReplyPath.Deserialize(bytes)),
            iter: 200);
    }

    [Fact]
    public void ReplyTokenContent_Deserialize_Rejects_Random_Bytes_Gracefully()
    {
        SmallBytes.Sample(
            bytes => AssertGracefulOrSucceeds(() => ReplyTokenContent.Deserialize(bytes)),
            iter: 200);
    }

    [Fact]
    public void FileTransferMessage_Deserialize_Rejects_Random_Bytes_Gracefully()
    {
        MediumBytes.Sample(
            bytes => AssertGracefulOrSucceeds(() => FileTransferMessage.Deserialize(bytes)),
            iter: 200);
    }

    /// <summary>
    /// Stress: very-small inputs are the most likely to hit edge cases in
    /// length-prefixed deserializers (BinaryReader.ReadByte/ReadInt32 on
    /// near-empty streams).
    /// </summary>
    [Fact]
    public void Tiny_Inputs_Across_All_Parsers_Are_Handled_Gracefully()
    {
        Gen.Byte.Array[0, 16].Sample(bytes =>
        {
            AssertGracefulOrSucceeds(() => KademliaMessage.Deserialize(bytes));
            AssertGracefulOrSucceeds(() => OnionLayer.Deserialize(bytes));
            AssertGracefulOrSucceeds(() => ChatMessage.Deserialize(bytes));
            AssertGracefulOrSucceeds(() => UserPublicKeyRecord.Deserialize(bytes));
            AssertGracefulOrSucceeds(() => RecipientPayload.Deserialize(bytes));
            AssertGracefulOrSucceeds(() => ReplyPath.Deserialize(bytes));
            AssertGracefulOrSucceeds(() => ReplyTokenContent.Deserialize(bytes));
            AssertGracefulOrSucceeds(() => OnionLayerContent.Deserialize(bytes));
        }, iter: 100);
    }

    /// <summary>
    /// The KademliaMessage type-tag dispatch should reject any of the 256
    /// possible byte values that aren't valid MessageType enum members. This
    /// is a quick exhaustive-style check rather than random sampling.
    /// </summary>
    [Fact]
    public void KademliaMessage_Rejects_All_Unknown_Type_Tags()
    {
        // Build a header that's the right shape but with an out-of-range type.
        // 1 byte type + 4 byte networkId + 16 byte messageId + 32 byte senderId
        // + 2 byte senderPort + 1 byte pubkey-len + 0 byte pubkey = 56 bytes
        // (the deserializer's minimum-size guard).
        for (int t = 0; t < 256; t++)
        {
            var tag = (byte)t;
            // Skip values that ARE valid MessageType members — they take a different code path.
            if (Enum.IsDefined(typeof(MessageType), tag))
                continue;

            var header = new byte[56];
            header[0] = tag;
            // pubKeyLen byte at offset 55 stays 0 (no pubkey bytes follow).

            try
            {
                KademliaMessage.Deserialize(header);
                Assert.Fail($"Type tag 0x{tag:X2} should have been rejected but Deserialize succeeded");
            }
            catch (Exception ex) when (IsGracefulRejection(ex))
            {
                // Expected.
            }
        }
    }
}
