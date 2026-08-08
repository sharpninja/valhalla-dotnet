using System.Buffers;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace SharpNinja.Valhalla.Generation.Differential;

/// <summary>
/// Provider-neutral semantic representation used to compare managed and official Valhalla graphs.
/// Build-specific metadata remains present for receipts but is not routing semantics.
/// </summary>
public sealed record ValhallaSemanticGraphSnapshot(
    string GraphFormatVersion,
    ulong DatasetId,
    string InputIdentity,
    ulong BuildId,
    DateTimeOffset CreatedAtUtc,
    string OutputChecksum,
    IReadOnlyList<ValhallaSemanticTileSnapshot> Tiles);

public sealed record ValhallaSemanticTileSnapshot(
    string TileId,
    IReadOnlyDictionary<string, string> RoutingFields,
    IReadOnlyDictionary<string, IReadOnlyList<ValhallaSemanticRecord>> Sections);

public sealed record ValhallaSemanticRecord(
    string Identity,
    IReadOnlyDictionary<string, string> Fields);

public sealed record ValhallaSemanticDifference(
    string Path,
    string? Expected,
    string? Actual);

/// <summary>
/// Compares every emitted semantic field while intentionally normalizing build ID, generation
/// timestamp, and output checksum. Those three values identify a concrete build artifact but do
/// not alter the routing semantics represented by the extracted snapshot.
/// </summary>
public sealed class ValhallaSemanticGraphComparator
{
    public IReadOnlyList<ValhallaSemanticDifference> Compare(
        ValhallaSemanticGraphSnapshot expected,
        ValhallaSemanticGraphSnapshot actual)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(actual);

        List<ValhallaSemanticDifference> differences = [];
        CompareValue(
            "$.graphFormatVersion",
            expected.GraphFormatVersion,
            actual.GraphFormatVersion,
            differences);
        CompareValue("$.datasetId", expected.DatasetId, actual.DatasetId, differences);
        CompareValue(
            "$.inputIdentity",
            expected.InputIdentity,
            actual.InputIdentity,
            differences);

        CompareTiles(expected.Tiles, actual.Tiles, differences);
        return differences;
    }

    private static void CompareTiles(
        IReadOnlyList<ValhallaSemanticTileSnapshot> expected,
        IReadOnlyList<ValhallaSemanticTileSnapshot> actual,
        List<ValhallaSemanticDifference> differences)
    {
        IReadOnlyDictionary<string, ValhallaSemanticTileSnapshot> expectedById =
            IndexUnique(expected, static tile => tile.TileId, "$.tiles.expected");
        IReadOnlyDictionary<string, ValhallaSemanticTileSnapshot> actualById =
            IndexUnique(actual, static tile => tile.TileId, "$.tiles.actual");

        foreach (string tileId in UnionKeys(expectedById.Keys, actualById.Keys))
        {
            string path = $"$.tiles['{EscapePathSegment(tileId)}']";
            bool hasExpected = expectedById.TryGetValue(tileId, out ValhallaSemanticTileSnapshot? expectedTile);
            bool hasActual = actualById.TryGetValue(tileId, out ValhallaSemanticTileSnapshot? actualTile);

            if (!hasExpected || !hasActual)
            {
                differences.Add(
                    new ValhallaSemanticDifference(
                        path,
                        hasExpected ? "<present>" : null,
                        hasActual ? "<present>" : null));
                continue;
            }

            CompareStringDictionary(
                $"{path}.routingFields",
                expectedTile!.RoutingFields,
                actualTile!.RoutingFields,
                differences);
            CompareSections(path, expectedTile.Sections, actualTile.Sections, differences);
        }
    }

    private static void CompareSections(
        string tilePath,
        IReadOnlyDictionary<string, IReadOnlyList<ValhallaSemanticRecord>> expected,
        IReadOnlyDictionary<string, IReadOnlyList<ValhallaSemanticRecord>> actual,
        List<ValhallaSemanticDifference> differences)
    {
        foreach (string sectionName in UnionKeys(expected.Keys, actual.Keys))
        {
            string path = $"{tilePath}.sections['{EscapePathSegment(sectionName)}']";
            bool hasExpected = expected.TryGetValue(
                sectionName,
                out IReadOnlyList<ValhallaSemanticRecord>? expectedRecords);
            bool hasActual = actual.TryGetValue(
                sectionName,
                out IReadOnlyList<ValhallaSemanticRecord>? actualRecords);

            if (!hasExpected || !hasActual)
            {
                differences.Add(
                    new ValhallaSemanticDifference(
                        path,
                        hasExpected ? "<present>" : null,
                        hasActual ? "<present>" : null));
                continue;
            }

            IReadOnlyDictionary<string, ValhallaSemanticRecord> expectedById =
                IndexUnique(expectedRecords!, static record => record.Identity, $"{path}.expected");
            IReadOnlyDictionary<string, ValhallaSemanticRecord> actualById =
                IndexUnique(actualRecords!, static record => record.Identity, $"{path}.actual");

            foreach (string recordId in UnionKeys(expectedById.Keys, actualById.Keys))
            {
                string recordPath = $"{path}['{EscapePathSegment(recordId)}']";
                bool hasExpectedRecord = expectedById.TryGetValue(
                    recordId,
                    out ValhallaSemanticRecord? expectedRecord);
                bool hasActualRecord = actualById.TryGetValue(
                    recordId,
                    out ValhallaSemanticRecord? actualRecord);

                if (!hasExpectedRecord || !hasActualRecord)
                {
                    differences.Add(
                        new ValhallaSemanticDifference(
                            recordPath,
                            hasExpectedRecord ? "<present>" : null,
                            hasActualRecord ? "<present>" : null));
                    continue;
                }

                CompareStringDictionary(
                    $"{recordPath}.fields",
                    expectedRecord!.Fields,
                    actualRecord!.Fields,
                    differences);
            }
        }
    }

    private static void CompareStringDictionary(
        string path,
        IReadOnlyDictionary<string, string> expected,
        IReadOnlyDictionary<string, string> actual,
        List<ValhallaSemanticDifference> differences)
    {
        foreach (string key in UnionKeys(expected.Keys, actual.Keys))
        {
            bool hasExpected = expected.TryGetValue(key, out string? expectedValue);
            bool hasActual = actual.TryGetValue(key, out string? actualValue);
            string valuePath = $"{path}['{EscapePathSegment(key)}']";

            if (!hasExpected || !hasActual)
            {
                differences.Add(
                    new ValhallaSemanticDifference(
                        valuePath,
                        hasExpected ? expectedValue : null,
                        hasActual ? actualValue : null));
                continue;
            }

            CompareValue(valuePath, expectedValue, actualValue, differences);
        }
    }

    private static IReadOnlyDictionary<string, T> IndexUnique<T>(
        IEnumerable<T> values,
        Func<T, string> keySelector,
        string path)
    {
        Dictionary<string, T> result = new(StringComparer.Ordinal);
        foreach (T value in values)
        {
            string key = keySelector(value);
            if (string.IsNullOrWhiteSpace(key))
            {
                throw new InvalidDataException($"{path} contains an empty identity.");
            }

            if (!result.TryAdd(key, value))
            {
                throw new InvalidDataException($"{path} contains duplicate identity '{key}'.");
            }
        }

        return result;
    }

    private static IEnumerable<string> UnionKeys(
        IEnumerable<string> expected,
        IEnumerable<string> actual)
    {
        SortedSet<string> keys = new(expected, StringComparer.Ordinal);
        keys.UnionWith(actual);
        return keys;
    }

    private static void CompareValue<T>(
        string path,
        T expected,
        T actual,
        List<ValhallaSemanticDifference> differences)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            differences.Add(
                new ValhallaSemanticDifference(
                    path,
                    expected?.ToString(),
                    actual?.ToString()));
        }
    }

    private static string EscapePathSegment(string value) =>
        value.Replace("'", "\\'", StringComparison.Ordinal);
}

public sealed record ValhallaRouteMatrixEntry(
    string CaseId,
    bool Succeeded,
    double DistanceMeters,
    double DurationSeconds,
    IReadOnlyList<string> DirectedEdgeIds);

public sealed record ValhallaRouteMetricTolerances(
    double MaximumDistanceDifferenceMeters,
    double MaximumDurationDifferenceSeconds,
    double MaximumRelativeDifference);

public sealed class ValhallaRouteMatrixComparator
{
    public IReadOnlyList<ValhallaSemanticDifference> Compare(
        IReadOnlyList<ValhallaRouteMatrixEntry> expected,
        IReadOnlyList<ValhallaRouteMatrixEntry> actual,
        ValhallaRouteMetricTolerances tolerances)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(actual);
        ArgumentNullException.ThrowIfNull(tolerances);
        ValidateTolerances(tolerances);

        IReadOnlyDictionary<string, ValhallaRouteMatrixEntry> expectedById =
            IndexRoutes(expected, "$.routes.expected");
        IReadOnlyDictionary<string, ValhallaRouteMatrixEntry> actualById =
            IndexRoutes(actual, "$.routes.actual");
        SortedSet<string> caseIds = new(expectedById.Keys, StringComparer.Ordinal);
        caseIds.UnionWith(actualById.Keys);

        List<ValhallaSemanticDifference> differences = [];
        foreach (string caseId in caseIds)
        {
            string path = $"$.routes['{caseId.Replace("'", "\\'", StringComparison.Ordinal)}']";
            bool hasExpected = expectedById.TryGetValue(caseId, out ValhallaRouteMatrixEntry? expectedRoute);
            bool hasActual = actualById.TryGetValue(caseId, out ValhallaRouteMatrixEntry? actualRoute);
            if (!hasExpected || !hasActual)
            {
                differences.Add(
                    new ValhallaSemanticDifference(
                        path,
                        hasExpected ? "<present>" : null,
                        hasActual ? "<present>" : null));
                continue;
            }

            if (expectedRoute!.Succeeded != actualRoute!.Succeeded)
            {
                differences.Add(
                    new ValhallaSemanticDifference(
                        $"{path}.succeeded",
                        expectedRoute.Succeeded.ToString(),
                        actualRoute.Succeeded.ToString()));
                continue;
            }

            if (!expectedRoute.Succeeded)
            {
                continue;
            }

            CompareMetric(
                $"{path}.distanceMeters",
                expectedRoute.DistanceMeters,
                actualRoute.DistanceMeters,
                tolerances.MaximumDistanceDifferenceMeters,
                tolerances.MaximumRelativeDifference,
                differences);
            CompareMetric(
                $"{path}.durationSeconds",
                expectedRoute.DurationSeconds,
                actualRoute.DurationSeconds,
                tolerances.MaximumDurationDifferenceSeconds,
                tolerances.MaximumRelativeDifference,
                differences);

            if (!expectedRoute.DirectedEdgeIds.SequenceEqual(
                    actualRoute.DirectedEdgeIds,
                    StringComparer.Ordinal))
            {
                differences.Add(
                    new ValhallaSemanticDifference(
                        $"{path}.directedEdgeIds",
                        string.Join(",", expectedRoute.DirectedEdgeIds),
                        string.Join(",", actualRoute.DirectedEdgeIds)));
            }
        }

        return differences;
    }

    private static IReadOnlyDictionary<string, ValhallaRouteMatrixEntry> IndexRoutes(
        IEnumerable<ValhallaRouteMatrixEntry> routes,
        string path)
    {
        Dictionary<string, ValhallaRouteMatrixEntry> result = new(StringComparer.Ordinal);
        foreach (ValhallaRouteMatrixEntry route in routes)
        {
            if (string.IsNullOrWhiteSpace(route.CaseId))
            {
                throw new InvalidDataException($"{path} contains an empty case identity.");
            }

            if (!result.TryAdd(route.CaseId, route))
            {
                throw new InvalidDataException(
                    $"{path} contains duplicate case identity '{route.CaseId}'.");
            }
        }

        return result;
    }

    private static void CompareMetric(
        string path,
        double expected,
        double actual,
        double maximumAbsoluteDifference,
        double maximumRelativeDifference,
        List<ValhallaSemanticDifference> differences)
    {
        if (!double.IsFinite(expected) || !double.IsFinite(actual))
        {
            if (!expected.Equals(actual))
            {
                differences.Add(
                    new ValhallaSemanticDifference(
                        path,
                        expected.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
                        actual.ToString("R", System.Globalization.CultureInfo.InvariantCulture)));
            }

            return;
        }

        double absoluteDifference = Math.Abs(expected - actual);
        double denominator = Math.Max(Math.Abs(expected), double.Epsilon);
        double relativeDifference = absoluteDifference / denominator;
        if (absoluteDifference > maximumAbsoluteDifference &&
            relativeDifference > maximumRelativeDifference)
        {
            differences.Add(
                new ValhallaSemanticDifference(
                    path,
                    expected.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
                    actual.ToString("R", System.Globalization.CultureInfo.InvariantCulture)));
        }
    }

    private static void ValidateTolerances(ValhallaRouteMetricTolerances tolerances)
    {
        if (!double.IsFinite(tolerances.MaximumDistanceDifferenceMeters) ||
            tolerances.MaximumDistanceDifferenceMeters < 0 ||
            !double.IsFinite(tolerances.MaximumDurationDifferenceSeconds) ||
            tolerances.MaximumDurationDifferenceSeconds < 0 ||
            !double.IsFinite(tolerances.MaximumRelativeDifference) ||
            tolerances.MaximumRelativeDifference < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(tolerances),
                "Route metric tolerances must be finite and nonnegative.");
        }
    }
}

public interface IValhallaSemanticGraphReader
{
    ValueTask<ValhallaSemanticGraphSnapshot> ReadAsync(
        string artifactPath,
        CancellationToken cancellationToken = default);
}

public sealed record BidirectionalTileCompatibilityReport(
    IReadOnlyList<ValhallaSemanticDifference> ManagedArtifactDifferences,
    IReadOnlyList<ValhallaSemanticDifference> OfficialArtifactDifferences)
{
    public bool IsCompatible =>
        ManagedArtifactDifferences.Count == 0 &&
        OfficialArtifactDifferences.Count == 0;
}

public sealed class BidirectionalTileCompatibilityHarness
{
    public async ValueTask<BidirectionalTileCompatibilityReport> VerifyAsync(
        string managedArtifactPath,
        string officialArtifactPath,
        IValhallaSemanticGraphReader managedReader,
        IValhallaSemanticGraphReader officialReader,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(managedArtifactPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(officialArtifactPath);
        ArgumentNullException.ThrowIfNull(managedReader);
        ArgumentNullException.ThrowIfNull(officialReader);

        cancellationToken.ThrowIfCancellationRequested();
        ValhallaSemanticGraphSnapshot managedReference =
            await managedReader.ReadAsync(managedArtifactPath, cancellationToken)
                .ConfigureAwait(false);
        ValhallaSemanticGraphSnapshot officialReadingManaged =
            await officialReader.ReadAsync(managedArtifactPath, cancellationToken)
                .ConfigureAwait(false);
        ValhallaSemanticGraphSnapshot officialReference =
            await officialReader.ReadAsync(officialArtifactPath, cancellationToken)
                .ConfigureAwait(false);
        ValhallaSemanticGraphSnapshot managedReadingOfficial =
            await managedReader.ReadAsync(officialArtifactPath, cancellationToken)
                .ConfigureAwait(false);

        ValhallaSemanticGraphComparator comparator = new();
        return new BidirectionalTileCompatibilityReport(
            comparator.Compare(managedReference, officialReadingManaged),
            comparator.Compare(officialReference, managedReadingOfficial));
    }
}

public sealed class GenerationOutputTreeHasher
{
    private const int BufferSize = 1024 * 1024;

    public async ValueTask<string> ComputeSha256Async(
        string rootDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        string fullRoot = Path.GetFullPath(rootDirectory);
        if (!Directory.Exists(fullRoot))
        {
            throw new DirectoryNotFoundException(
                $"Generation output directory '{fullRoot}' does not exist.");
        }

        string[] files = Directory.GetFiles(fullRoot, "*", SearchOption.AllDirectories);
        Array.Sort(
            files,
            (left, right) => StringComparer.Ordinal.Compare(
                NormalizeRelativePath(fullRoot, left),
                NormalizeRelativePath(fullRoot, right)));

        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        byte[] lengthBuffer = new byte[sizeof(long)];

        try
        {
            foreach (string file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                FileAttributes attributes = File.GetAttributes(file);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidDataException(
                        $"Generation output tree cannot contain a reparse point: '{file}'.");
                }

                string relativePath = NormalizeRelativePath(fullRoot, file);
                byte[] pathBytes = Encoding.UTF8.GetBytes(relativePath);
                BinaryPrimitives.WriteInt64LittleEndian(lengthBuffer, pathBytes.LongLength);
                hash.AppendData(lengthBuffer);
                hash.AppendData(pathBytes);

                FileInfo before = new(file);
                long expectedLength = before.Length;
                BinaryPrimitives.WriteInt64LittleEndian(lengthBuffer, expectedLength);
                hash.AppendData(lengthBuffer);

                long bytesRead = 0;
                await using FileStream stream = new(
                    file,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    BufferSize,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                while (true)
                {
                    int read = await stream.ReadAsync(
                            buffer.AsMemory(0, buffer.Length),
                            cancellationToken)
                        .ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }

                    hash.AppendData(buffer.AsSpan(0, read));
                    bytesRead += read;
                }

                if (bytesRead != expectedLength || new FileInfo(file).Length != expectedLength)
                {
                    throw new IOException(
                        $"Generation output file changed while hashing: '{file}'.");
                }
            }

            return Convert.ToHexString(hash.GetHashAndReset());
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: false);
        }
    }

    private static string NormalizeRelativePath(string rootDirectory, string filePath) =>
        Path.GetRelativePath(rootDirectory, filePath)
            .Replace(Path.DirectorySeparatorChar, '/')
            .Replace(Path.AltDirectorySeparatorChar, '/');
}

public enum ValhallaGenerationStage
{
    ValidateRequest = 0,
    BuildAdmins = 10,
    BuildTimeZones = 20,
    BuildElevationIndex = 30,
    IngestOsm = 40,
    BuildWays = 50,
    BuildNodes = 60,
    BuildEdges = 70,
    EnhanceGraph = 80,
    BuildRestrictions = 90,
    BuildHierarchy = 100,
    BuildShortcuts = 110,
    BuildTransit = 120,
    BuildBikeShare = 130,
    ApplyPredictedSpeeds = 140,
    ValidateGraph = 150,
    BuildTileExtract = 160,
    Publish = 170,
}

public enum ValhallaGenerationFailureCode
{
    Unknown = 0,
    Configuration = 1,
    InvalidInput = 2,
    IncompatibleCheckpoint = 3,
    Validation = 4,
    ResourceExhaustion = 5,
    Canceled = 6,
    InputOutput = 7,
    UpstreamParity = 8,
}

public sealed record ValhallaGenerationFailure(
    ValhallaGenerationFailureCode Code,
    string Message,
    ValhallaGenerationStage? Stage = null);

public sealed record ValhallaGenerationStageReceipt(
    ValhallaGenerationStage Stage,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset EndedAtUtc,
    string InputIdentity,
    string OutputIdentity,
    long RecordsProcessed,
    long BytesRead,
    long BytesWritten,
    int MaximumConcurrency,
    long AllocatedBytes,
    long PeakWorkingSetBytes,
    long ScratchDiskHighWaterMarkBytes,
    string CheckpointIdentity,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<ValhallaGenerationFailure> Failures,
    IReadOnlyDictionary<string, string> OutputHashes)
{
    public TimeSpan Duration => EndedAtUtc - StartedAtUtc;
}