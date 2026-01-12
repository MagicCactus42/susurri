using Shouldly;
using Susurri.Modules.DHT.Core.Kademlia;
using Xunit;

namespace Susurri.Tests.Unit.Kademlia;

public class KademliaIdTests
{
    [Fact]
    public void FromString_SameInput_ProducesSameId()
    {
        // Arrange
        const string input = "test_username";

        // Act
        var id1 = KademliaId.FromString(input);
        var id2 = KademliaId.FromString(input);

        // Assert
        id1.ShouldBe(id2);
    }

    [Fact]
    public void FromString_DifferentInput_ProducesDifferentIds()
    {
        // Arrange & Act
        var id1 = KademliaId.FromString("user1");
        var id2 = KademliaId.FromString("user2");

        // Assert
        id1.ShouldNotBe(id2);
    }

    [Fact]
    public void FromBytes_PreservesBytes()
    {
        // Arrange
        var bytes = new byte[32];
        new Random(42).NextBytes(bytes);

        // Act
        var id = KademliaId.FromBytes(bytes);

        // Assert
        id.Bytes.ToArray().ShouldBe(bytes);
    }

    [Fact]
    public void Random_ProducesUniqueIds()
    {
        // Act
        var id1 = KademliaId.Random();
        var id2 = KademliaId.Random();

        // Assert
        id1.ShouldNotBe(id2);
    }

    [Fact]
    public void DistanceTo_SameId_ReturnsZero()
    {
        // Arrange
        var id = KademliaId.Random();

        // Act
        var distance = id.DistanceTo(id);

        // Assert
        distance.GetHighestBitIndex().ShouldBe(-1); // All zeros
    }

    [Fact]
    public void DistanceTo_IsSymmetric()
    {
        // Arrange
        var id1 = KademliaId.Random();
        var id2 = KademliaId.Random();

        // Act
        var distance1 = id1.DistanceTo(id2);
        var distance2 = id2.DistanceTo(id1);

        // Assert
        distance1.ShouldBe(distance2);
    }

    [Fact]
    public void GetBucketIndex_SameId_ReturnsNegative()
    {
        // Arrange
        var id = KademliaId.Random();

        // Act
        var bucketIndex = id.GetBucketIndex(id);

        // Assert
        bucketIndex.ShouldBe(-1);
    }

    [Fact]
    public void GetBucketIndex_ReturnsDifferentBucketsForDifferentDistances()
    {
        // Arrange
        var localId = KademliaId.FromBytes(new byte[32]); // All zeros

        // Create IDs at different distances
        var nearId = KademliaId.FromBytes(new byte[32].Select((b, i) => i == 31 ? (byte)1 : b).ToArray());
        var farId = KademliaId.FromBytes(new byte[32].Select((b, i) => i == 0 ? (byte)128 : b).ToArray());

        // Act
        var nearBucket = localId.GetBucketIndex(nearId);
        var farBucket = localId.GetBucketIndex(farId);

        // Assert
        nearBucket.ShouldBeLessThan(farBucket);
    }

    [Fact]
    public void GetHighestBitIndex_AllZeros_ReturnsNegative()
    {
        // Arrange
        var id = KademliaId.FromBytes(new byte[32]);

        // Act
        var index = id.GetHighestBitIndex();

        // Assert
        index.ShouldBe(-1);
    }

    [Fact]
    public void GetHighestBitIndex_HighestBitSet_Returns255()
    {
        // Arrange
        var bytes = new byte[32];
        bytes[0] = 0x80; // 10000000 in first byte
        var id = KademliaId.FromBytes(bytes);

        // Act
        var index = id.GetHighestBitIndex();

        // Assert
        index.ShouldBe(255);
    }

    [Fact]
    public void GetHighestBitIndex_LowestBitSet_Returns0()
    {
        // Arrange
        var bytes = new byte[32];
        bytes[31] = 0x01; // 00000001 in last byte
        var id = KademliaId.FromBytes(bytes);

        // Act
        var index = id.GetHighestBitIndex();

        // Assert
        index.ShouldBe(0);
    }

    [Fact]
    public void CompareTo_LessThan_ReturnsNegative()
    {
        // Arrange
        var smaller = KademliaId.FromBytes(new byte[32].Select((_, i) => (byte)0).ToArray());
        var bytes = new byte[32];
        bytes[0] = 1;
        var larger = KademliaId.FromBytes(bytes);

        // Act
        var result = smaller.CompareTo(larger);

        // Assert
        result.ShouldBeLessThan(0);
    }

    [Fact]
    public void CompareTo_Equal_ReturnsZero()
    {
        // Arrange
        var id1 = KademliaId.FromString("same");
        var id2 = KademliaId.FromString("same");

        // Act
        var result = id1.CompareTo(id2);

        // Assert
        result.ShouldBe(0);
    }

    [Fact]
    public void ToString_ReturnsHexString()
    {
        // Arrange
        var bytes = new byte[32];
        bytes[0] = 0xAB;
        bytes[1] = 0xCD;
        var id = KademliaId.FromBytes(bytes);

        // Act
        var str = id.ToString();

        // Assert
        str.ShouldStartWith("abcd");
        str.Length.ShouldBe(64); // 32 bytes = 64 hex chars
    }

    [Fact]
    public void Equality_SameBytes_AreEqual()
    {
        // Arrange
        var bytes = new byte[32];
        new Random(42).NextBytes(bytes);

        var id1 = KademliaId.FromBytes(bytes);
        var id2 = KademliaId.FromBytes(bytes);

        // Assert
        (id1 == id2).ShouldBeTrue();
        id1.Equals(id2).ShouldBeTrue();
        id1.GetHashCode().ShouldBe(id2.GetHashCode());
    }

    [Fact]
    public void GetBit_ReturnsCorrectBits()
    {
        // Arrange
        var bytes = new byte[32];
        bytes[0] = 0b10101010; // Alternating bits
        var id = KademliaId.FromBytes(bytes);

        // Assert - first byte positions
        id.GetBit(0).ShouldBeTrue();  // 1
        id.GetBit(1).ShouldBeFalse(); // 0
        id.GetBit(2).ShouldBeTrue();  // 1
        id.GetBit(3).ShouldBeFalse(); // 0
    }
}
