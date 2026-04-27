using Shouldly;

namespace Susurri.Tests.Integration;

[Collection("DhtIntegration")]
public class DhtBootstrapTests
{
    [Fact]
    public async Task Cluster_Of_Five_Nodes_Converges_Within_Timeout()
    {
        await using var cluster = await DhtCluster.StartAsync(count: 5);

        // After convergence each node should know all the others.
        foreach (var node in cluster.Nodes)
        {
            node.KnownNodes.ShouldBeGreaterThanOrEqualTo(4);
        }
    }

    [Fact]
    public async Task Two_Node_Cluster_Bootstraps()
    {
        await using var cluster = await DhtCluster.StartAsync(count: 2);

        cluster.Nodes[0].KnownNodes.ShouldBeGreaterThanOrEqualTo(1);
        cluster.Nodes[1].KnownNodes.ShouldBeGreaterThanOrEqualTo(1);
    }

    [Fact]
    public async Task Each_Node_Has_Distinct_LocalId()
    {
        await using var cluster = await DhtCluster.StartAsync(count: 5);

        var ids = cluster.Nodes.Select(n => n.LocalId).ToList();
        ids.Distinct().Count().ShouldBe(ids.Count);
    }
}
