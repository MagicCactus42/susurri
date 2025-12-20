using System.Net;
using Shouldly;
using Susurri.Modules.DHT.Core.Kademlia;
using Xunit;

namespace Susurri.Tests.Unit.Kademlia;

public class RoutingTableTests
{
    private static KademliaNode CreateTestNode(int seed = 0)
    {
        var pubKey = new byte[32];
        new Random(seed).NextBytes(pubKey);
        var id = KademliaId.FromPublicKey(pubKey);
        return new KademliaNode(id, pubKey, new IPEndPoint(IPAddress.Loopback, 8000 + seed));
    }

    [Fact]
    public void TryAddNode_NewNode_ReturnsAdded()
    {
        // Arrange
        var localId = KademliaId.Random();
        var table = new RoutingTable(localId);
        var node = CreateTestNode();

        // Act
        var result = table.TryAddNode(node);

        // Assert
        result.ShouldBe(AddNodeResult.Added);
        table.TotalNodes.ShouldBe(1);
    }

    [Fact]
    public void TryAddNode_SameAsLocal_ReturnsUpdated()
    {
        // Arrange
        var pubKey = new byte[32];
        new Random(42).NextBytes(pubKey);
        var localId = KademliaId.FromPublicKey(pubKey);
        var table = new RoutingTable(localId);
        var sameNode = new KademliaNode(localId, pubKey, new IPEndPoint(IPAddress.Loopback, 8000));

        // Act
        var result = table.TryAddNode(sameNode);

        // Assert
        result.ShouldBe(AddNodeResult.Updated);
        table.TotalNodes.ShouldBe(0); // Local node not stored
    }

    [Fact]
    public void RemoveNode_ExistingNode_ReturnsTrue()
    {
        // Arrange
        var localId = KademliaId.Random();
        var table = new RoutingTable(localId);
        var node = CreateTestNode();
        table.TryAddNode(node);

        // Act
        var result = table.RemoveNode(node.Id);

        // Assert
        result.ShouldBeTrue();
        table.TotalNodes.ShouldBe(0);
    }

    [Fact]
    public void FindClosestNodes_ReturnsNodesOrderedByDistance()
    {
        // Arrange
        var localId = KademliaId.Random();
        var table = new RoutingTable(localId);

        // Add several nodes
        for (int i = 0; i < 10; i++)
        {
            table.TryAddNode(CreateTestNode(i));
        }

        var targetId = KademliaId.Random();

        // Act
        var closest = table.FindClosestNodes(targetId, 5);

        // Assert
        closest.Count.ShouldBeLessThanOrEqualTo(5);

        // Verify ordering by distance
        for (int i = 0; i < closest.Count - 1; i++)
        {
            var dist1 = closest[i].Id.DistanceTo(targetId);
            var dist2 = closest[i + 1].Id.DistanceTo(targetId);
            dist1.CompareTo(dist2).ShouldBeLessThanOrEqualTo(0);
        }
    }

    [Fact]
    public void FindClosestNodes_EmptyTable_ReturnsEmptyList()
    {
        // Arrange
        var localId = KademliaId.Random();
        var table = new RoutingTable(localId);
        var targetId = KademliaId.Random();

        // Act
        var closest = table.FindClosestNodes(targetId);

        // Assert
        closest.Count.ShouldBe(0);
    }

    [Fact]
    public void ContainsNode_ExistingNode_ReturnsTrue()
    {
        // Arrange
        var localId = KademliaId.Random();
        var table = new RoutingTable(localId);
        var node = CreateTestNode();
        table.TryAddNode(node);

        // Act & Assert
        table.ContainsNode(node.Id).ShouldBeTrue();
    }

    [Fact]
    public void ContainsNode_NonExistingNode_ReturnsFalse()
    {
        // Arrange
        var localId = KademliaId.Random();
        var table = new RoutingTable(localId);
        var node = CreateTestNode();

        // Act & Assert
        table.ContainsNode(node.Id).ShouldBeFalse();
    }

    [Fact]
    public void GetAllNodes_ReturnsAllAddedNodes()
    {
        // Arrange
        var localId = KademliaId.Random();
        var table = new RoutingTable(localId);

        var nodes = Enumerable.Range(0, 5).Select(CreateTestNode).ToList();
        foreach (var node in nodes)
        {
            table.TryAddNode(node);
        }

        // Act
        var allNodes = table.GetAllNodes();

        // Assert
        allNodes.Count.ShouldBe(5);
    }

    [Fact]
    public void GetRandomNodes_ReturnsRequestedCount()
    {
        // Arrange
        var localId = KademliaId.Random();
        var table = new RoutingTable(localId);

        for (int i = 0; i < 10; i++)
        {
            table.TryAddNode(CreateTestNode(i));
        }

        // Act
        var randomNodes = table.GetRandomNodes(3);

        // Assert
        randomNodes.Count.ShouldBe(3);
    }

    [Fact]
    public void GetRandomNodes_MoreThanAvailable_ReturnsAll()
    {
        // Arrange
        var localId = KademliaId.Random();
        var table = new RoutingTable(localId);

        for (int i = 0; i < 3; i++)
        {
            table.TryAddNode(CreateTestNode(i));
        }

        // Act
        var randomNodes = table.GetRandomNodes(10);

        // Assert
        randomNodes.Count.ShouldBe(3);
    }

    [Fact]
    public void MarkNodeSeen_UpdatesNode()
    {
        // Arrange
        var localId = KademliaId.Random();
        var table = new RoutingTable(localId);
        var node = CreateTestNode();
        table.TryAddNode(node);

        // Act - should not throw
        table.MarkNodeSeen(node.Id);

        // Assert
        table.ContainsNode(node.Id).ShouldBeTrue();
    }

    [Fact]
    public void GetBucket_ReturnsCorrectBucket()
    {
        // Arrange
        var localId = KademliaId.Random();
        var table = new RoutingTable(localId);
        var node = CreateTestNode();

        // Act
        var bucket = table.GetBucket(node.Id);

        // Assert
        bucket.ShouldNotBeNull();
    }

    [Fact]
    public void GetRandomNode_EmptyTable_ReturnsNull()
    {
        // Arrange
        var localId = KademliaId.Random();
        var table = new RoutingTable(localId);

        // Act
        var randomNode = table.GetRandomNode();

        // Assert
        randomNode.ShouldBeNull();
    }

    [Fact]
    public void GetRandomNode_NonEmptyTable_ReturnsNode()
    {
        // Arrange
        var localId = KademliaId.Random();
        var table = new RoutingTable(localId);
        table.TryAddNode(CreateTestNode());

        // Act
        var randomNode = table.GetRandomNode();

        // Assert
        randomNode.ShouldNotBeNull();
    }
}
