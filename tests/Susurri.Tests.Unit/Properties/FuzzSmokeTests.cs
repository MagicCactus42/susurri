using Shouldly;
using Susurri.Tests.Fuzz;
using Xunit;

namespace Susurri.Tests.Unit.Properties;

/// <summary>
/// Cheap regression guard: invokes every fuzz target from <see cref="FuzzTargets"/>
/// with 1000 random inputs each, fails if any target throws an exception
/// outside the graceful-rejection whitelist. This catches obvious parser
/// breakage between PRs without depending on AFL/SharpFuzz infrastructure
/// (those run nightly via the susurri-fuzz CLI per Phase 5 plan).
/// </summary>
public class FuzzSmokeTests
{
    private const int IterationsPerTarget = 1000;
    private const int MaxInputBytes = 8192;

    [Theory]
    [InlineData("kademlia")]
    [InlineData("onion-layer")]
    [InlineData("onion-content")]
    [InlineData("chat")]
    [InlineData("pubkey-record")]
    [InlineData("file-transfer")]
    [InlineData("recipient")]
    [InlineData("reply-path")]
    [InlineData("reply-token")]
    [InlineData("group-key")]
    [InlineData("wrapped-group-key")]
    [InlineData("group-message")]
    [InlineData("encrypted-group-message")]
    public void Target_Survives_Random_Bytes(string targetName)
    {
        FuzzTargets.All.ShouldContainKey(targetName);
        var target = FuzzTargets.All[targetName];

        // Deterministic seed per target so failures reproduce locally.
        var rng = new Random(targetName.GetHashCode(StringComparison.Ordinal));

        for (int i = 0; i < IterationsPerTarget; i++)
        {
            var len = rng.Next(0, MaxInputBytes);
            var bytes = new byte[len];
            rng.NextBytes(bytes);

            try
            {
                target(bytes);
            }
            catch (Exception ex) when (FuzzTargets.IsGracefulRejection(ex))
            {
                // Expected.
            }
            catch (Exception ex)
            {
                throw new Xunit.Sdk.XunitException(
                    $"Target '{targetName}' threw unexpected {ex.GetType().FullName} on iteration {i}: {ex.Message}");
            }
        }
    }
}
