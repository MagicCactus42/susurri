using System.Net;
using Shouldly;
using Susurri.Modules.DHT.Core.Kademlia;
using Xunit;

namespace Susurri.Tests.Unit.Kademlia;

public class KBucketTests
{
    private static KademliaNode CreateTestNode(int seed = 0)
    {
        var pubKey = new byte[32];
        new Random(seed).NextBytes(pubKey);
        var id = KademliaId.FromPublicKey(pubKey);
        return new KademliaNode(id, pubKey, new IPEndPoint(IPAddress.Loopback, 8000 + seed));
    }

    [Fact]
    public void TryAdd_EmptyBucket_ReturnsAdded()
    {
        // Arrange
        var bucket = new KBucket(0, k: 20);
        var node = CreateTestNode();

        // Act
        var result = bucket.TryAdd(node);

        // Assert
        result.ShouldBe(AddNodeResult.Added);
        bucket.Count.ShouldBe(1);
    }

    [Fact]
    public void TryAdd_ExistingNode_ReturnsUpdated()
    {
        // Arrange
        var bucket = new KBucket(0, k: 20);
        var node = CreateTestNode();
        bucket.TryAdd(node);

        // Act
        var result = bucket.TryAdd(node);

        // Assert
        result.ShouldBe(AddNodeResult.Updated);
        bucket.Count.ShouldBe(1); // Still only one node
    }

    [Fact]
    public void TryAdd_FullBucket_ReturnsBucketFull()
    {
        // Arrange
        var bucket = new KBucket(0, k: 3); // Small bucket for testing

        for (int i = 0; i < 3; i++)
        {
            bucket.TryAdd(CreateTestNode(i));
        }

        // Act
        var result = bucket.TryAdd(CreateTestNode(100));

        // Assert
        result.ShouldBe(AddNodeResult.BucketFull);
        bucket.Count.ShouldBe(3);
    }

    [Fact]
    public void IsFull_NotFull_ReturnsFalse()
    {
        // Arrange
        var bucket = new KBucket(0, k: 20);
        bucket.TryAdd(CreateTestNode());

        // Assert
        bucket.IsFull.ShouldBeFalse();
    }

    [Fact]
    public void IsFull_Full_ReturnsTrue()
    {
        // Arrange
        var bucket = new KBucket(0, k: 2);
        bucket.TryAdd(CreateTestNode(1));
        bucket.TryAdd(CreateTestNode(2));

        // Assert
        bucket.IsFull.ShouldBeTrue();
    }

    [Fact]
    public void GetOldestNode_ReturnsFirstAdded()
    {
        // Arrange
        var bucket = new KBucket(0, k: 20);
        var firstNode = CreateTestNode(1);
        var secondNode = CreateTestNode(2);

        bucket.TryAdd(firstNode);
        bucket.TryAdd(secondNode);

        // Act
        var oldest = bucket.GetOldestNode();

        // Assert
        oldest.ShouldNotBeNull();
        oldest.Id.ShouldBe(firstNode.Id);
    }

    [Fact]
    public void ReplaceOldest_ReplacesFirstNode()
    {
        // Arrange
        var bucket = new KBucket(0, k: 2);
        var oldNode = CreateTestNode(1);
        var middleNode = CreateTestNode(2);
        var newNode = CreateTestNode(3);

        bucket.TryAdd(oldNode);
        bucket.TryAdd(middleNode);

        // Act
        var result = bucket.ReplaceOldest(newNode);

        // Assert
        result.ShouldBeTrue();
        bucket.Contains(oldNode.Id).ShouldBeFalse();
        bucket.Contains(newNode.Id).ShouldBeTrue();
    }

    [Fact]
    public void Remove_ExistingNode_ReturnsTrue()
    {
        // Arrange
        var bucket = new KBucket(0, k: 20);
        var node = CreateTestNode();
        bucket.TryAdd(node);

        // Act
        var result = bucket.Remove(node.Id);

        // Assert
        result.ShouldBeTrue();
        bucket.Count.ShouldBe(0);
    }

    [Fact]
    public void Remove_NonExistingNode_ReturnsFalse()
    {
        // Arrange
        var bucket = new KBucket(0, k: 20);
        var node = CreateTestNode();

        // Act
        var result = bucket.Remove(node.Id);

        // Assert
        result.ShouldBeFalse();
    }

    [Fact]
    public void GetNodes_ReturnsAllNodes()
    {
        // Arrange
        var bucket = new KBucket(0, k: 20);
        var node1 = CreateTestNode(1);
        var node2 = CreateTestNode(2);
        var node3 = CreateTestNode(3);

        bucket.TryAdd(node1);
        bucket.TryAdd(node2);
        bucket.TryAdd(node3);

        // Act
        var nodes = bucket.GetNodes();

        // Assert
        nodes.Count.ShouldBe(3);
    }

    [Fact]
    public void MarkSeen_MovesNodeToEnd()
    {
        // Arrange
        var bucket = new KBucket(0, k: 20);
        var node1 = CreateTestNode(1);
        var node2 = CreateTestNode(2);

        bucket.TryAdd(node1);
        bucket.TryAdd(node2);

        // Act - mark first node as seen (should move to end)
        bucket.MarkSeen(node1.Id);

        // Assert - oldest should now be node2
        var oldest = bucket.GetOldestNode();
        oldest!.Id.ShouldBe(node2.Id);
    }

    [Fact]
    public void Contains_ExistingNode_ReturnsTrue()
    {
        // Arrange
        var bucket = new KBucket(0, k: 20);
        var node = CreateTestNode();
        bucket.TryAdd(node);

        // Act & Assert
        bucket.Contains(node.Id).ShouldBeTrue();
    }

    [Fact]
    public void Contains_NonExistingNode_ReturnsFalse()
    {
        // Arrange
        var bucket = new KBucket(0, k: 20);
        var node = CreateTestNode();

        // Act & Assert
        bucket.Contains(node.Id).ShouldBeFalse();
    }
}
