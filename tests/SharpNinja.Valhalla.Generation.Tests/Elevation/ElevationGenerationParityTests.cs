using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SharpNinja.Valhalla.Baldr;
using SharpNinja.Valhalla.Generation.Elevation;
using Xunit;

namespace SharpNinja.Valhalla.Generation.Tests.Elevation;

public sealed class ElevationGenerationParityTests
{
    [Fact]
    public async Task ManagedElevation_MatchesOfficialSemanticFixture()
    {
        string fixtureRoot = FindRepositoryArtifact(
            "tests",
            "SharpNinja.Valhalla.Generation.Tests",
            "Fixtures",
            "Elevation",
            "monaco-official-383-base");
        string root = Path.Combine(
            Path.GetTempPath(),
            $"valhalla-managed-elevation-{Guid.NewGuid():N}");
        string tileDirectory = Path.Combine(root, "tiles");
        string elevationDirectory = Path.Combine(root, "elevation");
        Directory.CreateDirectory(root);

        try
        {
            CopyDirectory(Path.Combine(fixtureRoot, "tiles"), tileDirectory);
            Directory.CreateDirectory(elevationDirectory);
            string hgtPath = Path.Combine(elevationDirectory, "N43E007.hgt");
            await WriteSyntheticHgtAsync(
                hgtPath,
                TestContext.Current.CancellationToken);

            IElevationDatasetBuilder builder = new ManagedElevationDatasetBuilder();
            ElevationDatasetBuildResult result = await builder.BuildAsync(
                new ElevationDatasetBuildRequest(
                    tileDirectory,
                    elevationDirectory,
                    MaxDegreeOfParallelism: 4,
                    ScratchDiskBudgetBytes: 128 * 1024 * 1024,
                    DeterministicOutput: true),
                TestContext.Current.CancellationToken);

            Assert.Equal(4, result.TileCount);
            Assert.Equal(5_447, result.NodeCount);
            Assert.Equal(6_612, result.UniqueEdgeInfoCount);
            Assert.Equal(1_625, result.EncodedElevationCount);
            Assert.True(result.BytesWritten > 0);
            Assert.True(result.PeakConcurrency is >= 1 and <= 4);

            string officialTileDirectory = Path.Combine(
                fixtureRoot,
                "official-elevated-tiles");
            Dictionary<(uint Level, uint TileId), string> expected =
                ReadExpectedHashes(Path.Combine(fixtureRoot, "fixture.json"));
            Dictionary<(uint Level, uint TileId), string> official =
                ComputeSemanticHashes(officialTileDirectory);

            Assert.Equal(expected, official);
            AssertElevationSemanticParity(officialTileDirectory, tileDirectory);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    internal static async Task WriteSyntheticHgtAsync(
        string path,
        CancellationToken cancellationToken)
    {
        const int dimension = 3_601;
        byte[] rowBuffer = new byte[dimension * sizeof(short)];
        await using FileStream stream = new(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            rowBuffer.Length,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        for (int row = 0; row < dimension; row++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (int column = 0; column < dimension; column++)
            {
                short height = checked((short)(
                    100 + Math.Floor((column + (3_600 - row)) / 20.0)));
                int offset = column * sizeof(short);
                rowBuffer[offset] = (byte)((height >> 8) & 0xff);
                rowBuffer[offset + 1] = (byte)(height & 0xff);
            }

            await stream.WriteAsync(rowBuffer, cancellationToken);
        }
    }

    internal static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (string directory in Directory.EnumerateDirectories(
                     source,
                     "*",
                     SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(
                destination,
                Path.GetRelativePath(source, directory)));
        }

        foreach (string file in Directory.EnumerateFiles(
                     source,
                     "*",
                     SearchOption.AllDirectories))
        {
            File.Copy(
                file,
                Path.Combine(destination, Path.GetRelativePath(source, file)));
        }
    }

    private static Dictionary<(uint Level, uint TileId), string> ReadExpectedHashes(
        string manifestPath)
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(manifestPath));
        return document.RootElement
            .GetProperty("officialSemanticHashes")
            .EnumerateArray()
            .ToDictionary(
                item => (
                    item.GetProperty("level").GetUInt32(),
                    item.GetProperty("tileId").GetUInt32()),
                item => item.GetProperty("sha256").GetString()!,
                EqualityComparer<(uint Level, uint TileId)>.Default);
    }

    private static void AssertElevationSemanticParity(
        string officialTileDirectory,
        string managedTileDirectory)
    {
        GraphReader officialReader = new(
            new GraphReader.Config { TileDir = officialTileDirectory });
        GraphReader managedReader = new(
            new GraphReader.Config { TileDir = managedTileDirectory });
        GraphId[] officialTileIds = officialReader.GetTileSet()
            .OrderBy(id => id.Level())
            .ThenBy(id => id.Tileid())
            .ToArray();
        GraphId[] managedTileIds = managedReader.GetTileSet()
            .OrderBy(id => id.Level())
            .ThenBy(id => id.Tileid())
            .ToArray();

        Assert.Equal(officialTileIds, managedTileIds);
        foreach (GraphId tileId in officialTileIds)
        {
            GraphTile official = officialReader.GetGraphTile(tileId)!;
            GraphTile managed = managedReader.GetGraphTile(tileId)!;
            Assert.Equal(official.Header().HasElevation(), managed.Header().HasElevation());
            Assert.Equal(official.NodeCount(), managed.NodeCount());
            Assert.Equal(official.DirectedEdgeCount(), managed.DirectedEdgeCount());

            string relativePath = GraphTile.FileSuffix(tileId.TileBase());
            Assert.Equal(
                new FileInfo(Path.Combine(officialTileDirectory, relativePath)).Length,
                new FileInfo(Path.Combine(managedTileDirectory, relativePath)).Length);

            for (int nodeIndex = 0; nodeIndex < official.NodeCount(); nodeIndex++)
            {
                Assert.Equal(
                    official.Node(nodeIndex).Elevation(),
                    managed.Node(nodeIndex).Elevation());
            }

            var comparedEdgeInfoOffsets = new HashSet<ulong>();
            for (int edgeIndex = 0; edgeIndex < official.DirectedEdgeCount(); edgeIndex++)
            {
                DirectedEdge officialEdge = official.DirectedEdge(edgeIndex);
                DirectedEdge managedEdge = managed.DirectedEdge(edgeIndex);
                Assert.Equal(officialEdge.EdgeInfoOffset, managedEdge.EdgeInfoOffset);
                Assert.Equal(officialEdge.WeightedGrade, managedEdge.WeightedGrade);

                // Spherical resampling uses the platform libm implementation. Flat or nearly flat
                // intervals can land on opposite sides of the integer-percent slope boundary on
                // Linux and Windows. The official and managed outputs differ by at most one stored
                // percentage point while all sampled and encoded elevations remain exact.
                Assert.InRange(
                    Math.Abs(officialEdge.MaxUpSlope() - managedEdge.MaxUpSlope()),
                    0,
                    1);
                Assert.InRange(
                    Math.Abs(officialEdge.MaxDownSlope() - managedEdge.MaxDownSlope()),
                    0,
                    1);

                if (!comparedEdgeInfoOffsets.Add(officialEdge.EdgeInfoOffset))
                {
                    continue;
                }

                EdgeInfo officialEdgeInfo = official.EdgeInfo(officialEdge);
                EdgeInfo managedEdgeInfo = managed.EdgeInfo(managedEdge);
                Assert.Equal(officialEdgeInfo.MeanElevation, managedEdgeInfo.MeanElevation);
                Assert.Equal(officialEdgeInfo.HasElevation, managedEdgeInfo.HasElevation);
                List<sbyte> officialEncoded = officialEdgeInfo.EncodedElevation(
                    officialEdge.Length,
                    out double officialInterval);
                List<sbyte> managedEncoded = managedEdgeInfo.EncodedElevation(
                    managedEdge.Length,
                    out double managedInterval);
                Assert.Equal(officialInterval, managedInterval);
                Assert.Equal(officialEncoded, managedEncoded);
            }
        }
    }

    private static Dictionary<(uint Level, uint TileId), string> ComputeSemanticHashes(
        string tileDirectory)
    {
        GraphReader reader = new(new GraphReader.Config { TileDir = tileDirectory });
        var result = new Dictionary<(uint Level, uint TileId), string>();
        foreach (GraphId tileId in reader.GetTileSet()
                     .OrderBy(id => id.Level())
                     .ThenBy(id => id.Tileid()))
        {
            GraphTile tile = reader.GetGraphTile(tileId)!;
            var canonical = new StringBuilder();
            canonical.Append("tile=").Append(tileId.Tileid()).Append(';')
                .Append("level=").Append(tileId.Level()).Append(';')
                .Append("has=").Append(tile.Header().HasElevation()).AppendLine();

            for (int nodeIndex = 0; nodeIndex < tile.NodeCount(); nodeIndex++)
            {
                canonical.Append("n|").Append(nodeIndex).Append('|')
                    .Append(tile.Node(nodeIndex).Elevation().ToString(
                        "R",
                        CultureInfo.InvariantCulture))
                    .AppendLine();
            }

            var seenOffsets = new HashSet<ulong>();
            for (int edgeIndex = 0; edgeIndex < tile.DirectedEdgeCount(); edgeIndex++)
            {
                DirectedEdge edge = tile.DirectedEdge(edgeIndex);
                canonical.Append("e|").Append(edgeIndex).Append('|')
                    .Append(edge.EdgeInfoOffset).Append('|')
                    .Append(edge.WeightedGrade).Append('|')
                    .Append(edge.MaxUpSlope()).Append('|')
                    .Append(edge.MaxDownSlope()).AppendLine();
                if (!seenOffsets.Add(edge.EdgeInfoOffset))
                {
                    continue;
                }

                EdgeInfo edgeInfo = tile.EdgeInfo(edge);
                List<sbyte> encoded = edgeInfo.EncodedElevation(
                    edge.Length,
                    out double interval);
                canonical.Append("i|").Append(edge.EdgeInfoOffset).Append('|')
                    .Append(edgeInfo.MeanElevation.ToString(
                        "R",
                        CultureInfo.InvariantCulture))
                    .Append('|').Append(edgeInfo.HasElevation).Append('|')
                    .Append(interval.ToString("R", CultureInfo.InvariantCulture))
                    .Append('|').AppendJoin(',', encoded).AppendLine();
            }

            result.Add(
                (tileId.Level(), tileId.Tileid()),
                Convert.ToHexString(SHA256.HashData(
                    Encoding.UTF8.GetBytes(canonical.ToString()))));
        }

        return result;
    }

    private static string FindRepositoryArtifact(params string[] parts)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string candidate = Path.Combine([directory.FullName, .. parts]);
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        return Path.Combine(parts);
    }
}
