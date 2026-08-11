using SharpNinja.Valhalla.Baldr;
using SharpNinja.Valhalla.Generation.Roads.Frontier;
using SharpNinja.Valhalla.Generation.Storage;
using Xunit;

namespace SharpNinja.Valhalla.Generation.Tests.Roads;

public sealed class StableGraphIdentityIndexTests
{
    [Fact]
    public async Task BuildAsync_AssignsStableIdsByLevelTileGridOsmAndCanonicalOrder()
    {
        string root = CreateWorkingDirectory();
        try
        {
            GenerationGraphNodeCandidate[] candidates =
            [
                Candidate(30, 36.12650, -86.67750, gridId: 2, canonicalOrdinal: 0),
                Candidate(20, 36.12640, -86.67740, gridId: 1, canonicalOrdinal: 1),
                Candidate(10, 36.12630, -86.67730, gridId: 1, canonicalOrdinal: 2),
                Candidate(10, 36.12631, -86.67731, gridId: 1, canonicalOrdinal: 3),
            ];
            using IntermediateSequenceStore<GenerationGraphNodeCandidate> input =
                await CreateInputAsync(root, candidates);
            using StableGraphIdentityIndex index = await StableGraphIdentityIndex.BuildAsync(
                input,
                Options(root),
                TestContext.Current.CancellationToken);

            StableGraphNodeIdentity[] identities = ReadIdentities(index);
            Assert.Equal([10L, 10L, 20L, 30L], identities.Select(x => x.OsmNodeId));
            Assert.Equal([2L, 3L, 1L, 0L], identities.Select(x => x.CanonicalOrdinal));
            Assert.Equal([0U, 1U, 2U, 3U], identities.Select(x => x.GraphId.Id()));
            Assert.All(
                identities,
                identity => Assert.Equal(candidates[0].TileBase.TileValue(), identity.GraphId.TileValue()));
            Assert.True(index.TryGetGraphId(10, 2, out GraphId firstDuplicate));
            Assert.True(index.TryGetGraphId(10, 3, out GraphId secondDuplicate));
            Assert.Equal(0U, firstDuplicate.Id());
            Assert.Equal(1U, secondDuplicate.Id());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task BuildAsync_ReversedInputProducesIdenticalIdentityAndLookupManifests()
    {
        string firstRoot = CreateWorkingDirectory();
        string secondRoot = CreateWorkingDirectory();
        try
        {
            GenerationGraphNodeCandidate[] candidates =
            [
                Candidate(40, 36.12660, -86.67760, gridId: 3, canonicalOrdinal: 0),
                Candidate(30, 36.12650, -86.67750, gridId: 2, canonicalOrdinal: 1),
                Candidate(20, 36.12640, -86.67740, gridId: 1, canonicalOrdinal: 2),
                Candidate(10, 36.12630, -86.67730, gridId: 0, canonicalOrdinal: 3),
            ];
            using IntermediateSequenceStore<GenerationGraphNodeCandidate> firstInput =
                await CreateInputAsync(firstRoot, candidates);
            using IntermediateSequenceStore<GenerationGraphNodeCandidate> secondInput =
                await CreateInputAsync(secondRoot, candidates.Reverse().ToArray());
            using StableGraphIdentityIndex first = await StableGraphIdentityIndex.BuildAsync(
                firstInput,
                Options(firstRoot),
                TestContext.Current.CancellationToken);
            using StableGraphIdentityIndex second = await StableGraphIdentityIndex.BuildAsync(
                secondInput,
                Options(secondRoot),
                TestContext.Current.CancellationToken);

            Assert.Equal(
                first.IdentityManifest.ContentSha256,
                second.IdentityManifest.ContentSha256);
            Assert.Equal(
                first.LookupManifest.ContentSha256,
                second.LookupManifest.ContentSha256);
            Assert.Equal(ReadIdentities(first), ReadIdentities(second));
        }
        finally
        {
            Directory.Delete(firstRoot, recursive: true);
            Directory.Delete(secondRoot, recursive: true);
        }
    }

    [Fact]
    public async Task BuildAsync_AssignsIndependentLocalIdsPerTile()
    {
        string root = CreateWorkingDirectory();
        try
        {
            GenerationGraphNodeCandidate[] candidates =
            [
                Candidate(1, 36.12630, -86.67730, gridId: 0, canonicalOrdinal: 0),
                Candidate(2, 36.12640, -85.10000, gridId: 0, canonicalOrdinal: 1),
            ];
            Assert.NotEqual(candidates[0].TileBase.TileValue(), candidates[1].TileBase.TileValue());
            using IntermediateSequenceStore<GenerationGraphNodeCandidate> input =
                await CreateInputAsync(root, candidates);
            using StableGraphIdentityIndex index = await StableGraphIdentityIndex.BuildAsync(
                input,
                Options(root),
                TestContext.Current.CancellationToken);

            StableGraphNodeIdentity[] identities = ReadIdentities(index);
            Assert.Equal(2, identities.Length);
            Assert.All(identities, identity => Assert.Equal(0U, identity.GraphId.Id()));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static StableGraphNodeIdentity[] ReadIdentities(
        StableGraphIdentityIndex index) =>
        Enumerable.Range(0, checked((int)index.IdentityCount))
            .Select(ordinal => index.ReadIdentity(ordinal))
            .ToArray();

    private static GenerationGraphNodeCandidate Candidate(
        long osmNodeId,
        double latitude,
        double longitude,
        uint gridId,
        long canonicalOrdinal) =>
        GenerationGraphNodeCandidate.Create(
            new GenerationNodeRecord(
                osmNodeId,
                LatitudeE7: checked((int)Math.Round(latitude * 10_000_000)),
                LongitudeE7: checked((int)Math.Round(longitude * 10_000_000)),
                NodeSemanticFlags.None,
                TagReference: 0),
            level: TileHierarchy.Levels()[^1].Level,
            gridId,
            canonicalOrdinal);

    private static async ValueTask<IntermediateSequenceStore<GenerationGraphNodeCandidate>>
        CreateInputAsync(
            string root,
            IReadOnlyList<GenerationGraphNodeCandidate> candidates)
    {
        var input = new IntermediateSequenceStore<GenerationGraphNodeCandidate>(
            new IntermediateSequenceStoreOptions(
                root,
                "candidate-input-" + Guid.NewGuid().ToString("N"),
                IntermediateStorageMode.Auto,
                MemoryBudgetBytes: 1024,
                ScratchDiskBudgetBytes: 1024 * 1024,
                SegmentSizeBytes: 256));
        foreach (GenerationGraphNodeCandidate candidate in candidates)
        {
            input.Append(candidate);
        }

        await input.CompleteAsync(TestContext.Current.CancellationToken);
        return input;
    }

    private static StableGraphIdentityIndexOptions Options(string root) =>
        new(
            root,
            IntermediateStorageMode.Auto,
            MemoryBudgetBytes: 8 * 1024,
            ScratchDiskBudgetBytes: 32 * 1024 * 1024,
            SegmentSizeBytes: 256);

    private static string CreateWorkingDirectory()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            "valhalla-stable-graph-identity-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }
}
