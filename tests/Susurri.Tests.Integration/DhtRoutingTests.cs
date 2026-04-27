using Shouldly;

namespace Susurri.Tests.Integration;

[Collection("DhtIntegration")]
public class DhtRoutingTests
{
    [Fact]
    public async Task PublishPublicKey_Then_Lookup_RoundTrips_Across_Cluster()
    {
        await using var cluster = await DhtCluster.StartAsync(count: 5);

        var publisher = cluster.Nodes[0];
        var lookuper = cluster.Nodes[4];
        var username = $"alice-{Guid.NewGuid():N}";

        await publisher.PublishPublicKeyAsync(username);

        var record = await lookuper.LookupPublicKeyAsync(username);

        record.ShouldNotBeNull();
        record.EncryptionPublicKey.ShouldBe(publisher.EncryptionPublicKey);
        record.SigningPublicKey.ShouldBe(publisher.SigningPublicKey);
        record.VerifySignature().ShouldBeTrue("the published record should be self-signed");
    }

    [Fact]
    public async Task LookupPublicKey_Returns_Null_For_Unknown_Username()
    {
        await using var cluster = await DhtCluster.StartAsync(count: 3);

        var record = await cluster.Nodes[0].LookupPublicKeyAsync($"never-published-{Guid.NewGuid():N}");

        record.ShouldBeNull();
    }

    [Fact]
    public async Task Publication_Is_Visible_To_All_Cluster_Members()
    {
        await using var cluster = await DhtCluster.StartAsync(count: 4);

        var publisher = cluster.Nodes[0];
        var username = $"bob-{Guid.NewGuid():N}";
        await publisher.PublishPublicKeyAsync(username);

        // Every other node should find it (via local cache or via DHT).
        for (int i = 1; i < cluster.Nodes.Count; i++)
        {
            var record = await cluster.Nodes[i].LookupPublicKeyAsync(username);
            record.ShouldNotBeNull($"node[{i}] failed to look up {username}");
            record.EncryptionPublicKey.ShouldBe(publisher.EncryptionPublicKey);
        }
    }
}
