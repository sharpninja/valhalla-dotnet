using SharpNinja.Valhalla.Traffic.Tiles;

namespace SharpNinja.Valhalla.Tests.Traffic;

public sealed class TrafficSnapshotPairPointerHostileBehaviorTests
{
    [Fact]
    public async Task GetCurrentAsync_PairWithNullMember_ReturnsTypedIncompleteFailure()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            "SharpNinja.Valhalla.Tests",
            "null-pair-pointer",
            Guid.NewGuid().ToString("N"));
        const string graphSha256 = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
        string graphRoot = Path.Combine(root, "graphs", graphSha256);
        Directory.CreateDirectory(graphRoot);

        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(graphRoot, "current-pair.json"),
                """{"Enabled":null,"ClosureOnly":null}""",
                TestContext.Current.CancellationToken);

            var store = new TrafficSnapshotStore(root);
            TrafficSnapshotStoreException exception =
                await Assert.ThrowsAsync<TrafficSnapshotStoreException>(
                    async () => await store.GetCurrentAsync(
                        graphSha256,
                        TrafficSnapshotPolicy.Enabled,
                        TestContext.Current.CancellationToken));

            Assert.Equal(TrafficSnapshotFailureCode.Incomplete, exception.Code);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
