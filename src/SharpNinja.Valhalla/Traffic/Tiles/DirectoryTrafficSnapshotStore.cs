using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SharpNinja.Valhalla.Baldr;

namespace SharpNinja.Valhalla.Traffic.Tiles;

public sealed record TrafficSnapshotTileManifest(
    ulong TileId,
    uint DirectedEdgeCount,
    string RelativePath,
    long ByteLength,
    string Sha256);

public sealed record TrafficSnapshotManifest(
    string GraphSha256,
    string Version,
    TrafficSnapshotPolicy Policy,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    bool Complete,
    IReadOnlyList<TrafficSnapshotTileManifest> Tiles);

public sealed record TrafficSnapshotPairReference(
    TrafficSnapshotReference Enabled,
    TrafficSnapshotReference ClosureOnly,
    DateTimeOffset CohortCreatedAtUtc,
    string CohortId);

public sealed class TrafficSnapshotStoreException : IOException
{
    public TrafficSnapshotStoreException(TrafficSnapshotFailureCode code, string message)
        : base(message)
    {
        Code = code;
    }

    public TrafficSnapshotFailureCode Code { get; }
}

public interface ITrafficSnapshotLease : IAsyncDisposable
{
    TrafficSnapshotReference Snapshot { get; }

    GraphMemory? OpenTrafficMemory(GraphId tileId);
}

public sealed class TrafficSnapshotStore
{
    private sealed class SharedStoreState
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public Dictionary<string, int> Pins { get; } = new(StringComparer.OrdinalIgnoreCase);
        public ConcurrentDictionary<string, byte> ActiveStagingDirectories { get; } =
            new(StringComparer.OrdinalIgnoreCase);
        public ConcurrentDictionary<string, int> PendingGenerations { get; } =
            new(StringComparer.OrdinalIgnoreCase);
    }

    private const string ManifestFileName = "manifest.json";
    private static readonly ConcurrentDictionary<string, SharedStoreState> SharedStates =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _gate;
    private readonly Dictionary<string, int> _pins;
    private readonly ConcurrentDictionary<string, byte> _activeStagingDirectories;
    private readonly ConcurrentDictionary<string, int> _pendingGenerations;
    private readonly int _maxRetainedGenerations;
    private readonly TimeProvider _timeProvider;

    public TrafficSnapshotStore(
        string rootDirectory,
        int maxRetainedGenerations = 3,
        TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        if (maxRetainedGenerations < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxRetainedGenerations));
        }

        RootDirectory = Path.GetFullPath(rootDirectory);
        SharedStoreState shared = SharedStates.GetOrAdd(
            RootDirectory,
            static _ => new SharedStoreState());
        _gate = shared.Gate;
        _pins = shared.Pins;
        _activeStagingDirectories = shared.ActiveStagingDirectories;
        _pendingGenerations = shared.PendingGenerations;
        _maxRetainedGenerations = maxRetainedGenerations;
        _timeProvider = timeProvider ?? TimeProvider.System;
        Directory.CreateDirectory(RootDirectory);
    }

    public string RootDirectory { get; }

    public string CreateStagingDirectory()
    {
        string staging = Path.Combine(RootDirectory, ".tmp-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
        if (!_activeStagingDirectories.TryAdd(staging, 0))
        {
            throw new IOException("Could not reserve a unique traffic snapshot staging directory.");
        }

        try
        {
            Directory.CreateDirectory(staging);
            return staging;
        }
        catch
        {
            _activeStagingDirectories.TryRemove(staging, out _);
            throw;
        }
    }

    internal void ReleaseStagingDirectory(string stagingDirectory)
    {
        string staging = Path.GetFullPath(stagingDirectory);
        _activeStagingDirectories.TryRemove(staging, out _);
    }

    internal void ReleasePendingGeneration(TrafficSnapshotReference? snapshot)
    {
        if (snapshot is null)
        {
            return;
        }

        string generation = Path.GetFullPath(snapshot.GenerationDirectory);
        while (_pendingGenerations.TryGetValue(generation, out int count))
        {
            if (count > 1)
            {
                if (_pendingGenerations.TryUpdate(generation, count - 1, count))
                {
                    return;
                }

                continue;
            }

            if (((ICollection<KeyValuePair<string, int>>)_pendingGenerations)
                .Remove(new KeyValuePair<string, int>(generation, count)))
            {
                return;
            }
        }
    }

    public Task<TrafficSnapshotReference> PublishAsync(
        string stagingDirectory,
        TrafficSnapshotManifest manifest,
        CancellationToken cancellationToken = default) =>
        PublishAsync(stagingDirectory, manifest, promoteCurrent: true, cancellationToken);

    internal async Task<TrafficSnapshotReference> PublishAsync(
        string stagingDirectory,
        TrafficSnapshotManifest manifest,
        bool promoteCurrent,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stagingDirectory);
        ArgumentNullException.ThrowIfNull(manifest);
        cancellationToken.ThrowIfCancellationRequested();

        string staging = Path.GetFullPath(stagingDirectory);
        if (!IsDescendant(RootDirectory, staging) || !Path.GetFileName(staging).StartsWith(".tmp-", StringComparison.Ordinal))
        {
            throw new ArgumentException("The staging directory must be a store-owned temporary generation.", nameof(stagingDirectory));
        }

        if (!_activeStagingDirectories.ContainsKey(staging))
        {
            throw new ArgumentException("The staging directory is not reserved by this store.", nameof(stagingDirectory));
        }

        try
        {
            await ValidateGenerationAsync(staging, manifest, cancellationToken).ConfigureAwait(false);
            string contentVersion = ComputeContentVersion(manifest);
            TrafficSnapshotManifest completed = manifest with
            {
                Version = contentVersion,
                Complete = true,
                Tiles = Array.AsReadOnly(manifest.Tiles.OrderBy(static tile => tile.TileId).ToArray()),
            };

            string manifestPath = Path.Combine(staging, ManifestFileName);
            await WriteManifestAsync(manifestPath, completed, cancellationToken).ConfigureAwait(false);
            await ValidateGenerationAsync(staging, completed, cancellationToken).ConfigureAwait(false);

            string graphRoot = GraphRoot(completed.GraphSha256);
            string generationsRoot = Path.Combine(graphRoot, "generations");
            Directory.CreateDirectory(generationsRoot);
            string destination = Path.Combine(generationsRoot, contentVersion);

            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                TrafficSnapshotManifest publishedManifest = completed;
                if (Directory.Exists(destination))
                {
                    Directory.Delete(staging, true);
                    publishedManifest = await ReadAndValidateManifestAsync(
                        ToReference(destination, completed),
                        cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    Directory.Move(staging, destination);
                }

                var reference = ToReference(destination, publishedManifest);
                if (promoteCurrent)
                {
                    await PromoteCurrentPointerAsync(graphRoot, reference, cancellationToken).ConfigureAwait(false);
                    await CleanupCoreAsync(completed.GraphSha256, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    _pendingGenerations.AddOrUpdate(
                        Path.GetFullPath(reference.GenerationDirectory),
                        1,
                        static (_, count) => checked(count + 1));
                }

                return reference;
            }
            finally
            {
                _gate.Release();
            }
        }
        finally
        {
            ReleaseStagingDirectory(staging);
        }
    }

    public Task PromoteCurrentPairAsync(
        TrafficSnapshotReference enabled,
        TrafficSnapshotReference closureOnly,
        CancellationToken cancellationToken = default) =>
        PromoteCurrentPairCoreAsync(enabled, closureOnly, cohortCreatedAtUtc: null, cancellationToken);

    internal Task PromoteCurrentPairAsync(
        TrafficSnapshotReference enabled,
        TrafficSnapshotReference closureOnly,
        DateTimeOffset cohortCreatedAtUtc,
        CancellationToken cancellationToken) =>
        PromoteCurrentPairCoreAsync(enabled, closureOnly, cohortCreatedAtUtc, cancellationToken);

    private async Task PromoteCurrentPairCoreAsync(
        TrafficSnapshotReference enabled,
        TrafficSnapshotReference closureOnly,
        DateTimeOffset? cohortCreatedAtUtc,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(enabled);
        ArgumentNullException.ThrowIfNull(closureOnly);
        ValidateReferenceIdentity(enabled);
        ValidateReferenceIdentity(closureOnly);
        if (enabled.Policy != TrafficSnapshotPolicy.Enabled
            || closureOnly.Policy != TrafficSnapshotPolicy.ClosureOnly)
        {
            throw new TrafficSnapshotStoreException(
                TrafficSnapshotFailureCode.Incomplete,
                "A current traffic pair requires enabled and closure-only policy generations.");
        }

        string graphSha = NormalizeSha256(enabled.GraphSha256);
        if (!string.Equals(
                graphSha,
                NormalizeSha256(closureOnly.GraphSha256),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new TrafficSnapshotStoreException(
                TrafficSnapshotFailureCode.GraphMismatch,
                "Traffic snapshot pair generations target different graphs.");
        }

        if (cohortCreatedAtUtc is null)
        {
            if (enabled.CreatedAtUtc != closureOnly.CreatedAtUtc)
            {
                throw new TrafficSnapshotStoreException(
                    TrafficSnapshotFailureCode.Incomplete,
                    "Traffic snapshot pair generations were not created by the same refresh cohort.");
            }

            cohortCreatedAtUtc = enabled.CreatedAtUtc;
        }

        if (cohortCreatedAtUtc == default)
        {
            throw new TrafficSnapshotStoreException(
                TrafficSnapshotFailureCode.Incomplete,
                "Traffic snapshot pair cohort time is missing.");
        }

        string cohortId = ComputePairCohortId(enabled, closureOnly, cohortCreatedAtUtc.Value);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (enabled.IsExpired(_timeProvider) || closureOnly.IsExpired(_timeProvider))
            {
                throw new TrafficSnapshotStoreException(
                    TrafficSnapshotFailureCode.Expired,
                    "A traffic snapshot pair generation is expired.");
            }

            TrafficSnapshotManifest enabledManifest =
                await ReadAndValidateManifestAsync(enabled, cancellationToken).ConfigureAwait(false);
            TrafficSnapshotManifest closureManifest =
                await ReadAndValidateManifestAsync(closureOnly, cancellationToken).ConfigureAwait(false);
            if (enabledManifest.Policy != TrafficSnapshotPolicy.Enabled
                || closureManifest.Policy != TrafficSnapshotPolicy.ClosureOnly)
            {
                throw new TrafficSnapshotStoreException(
                    TrafficSnapshotFailureCode.Incomplete,
                    "Traffic snapshot pair manifests have invalid policies.");
            }

            string graphRoot = GraphRoot(graphSha);
            await PromoteCurrentPairPointerAsync(
                graphRoot,
                new TrafficSnapshotPairReference(enabled, closureOnly, cohortCreatedAtUtc.Value, cohortId),
                cancellationToken).ConfigureAwait(false);
            await CleanupCoreAsync(graphSha, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ITrafficSnapshotLease> AcquireAsync(
        TrafficSnapshotReference snapshot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        cancellationToken.ThrowIfCancellationRequested();
        ValidateReferenceIdentity(snapshot);
        if (snapshot.IsExpired(_timeProvider))
        {
            throw new TrafficSnapshotStoreException(
                TrafficSnapshotFailureCode.Expired,
                "Traffic snapshot is expired.");
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            TrafficSnapshotManifest manifest = await ReadAndValidateManifestAsync(snapshot, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(manifest.Version, snapshot.Version, StringComparison.OrdinalIgnoreCase))
            {
                throw new TrafficSnapshotStoreException(
                    TrafficSnapshotFailureCode.Incomplete,
                    "Traffic snapshot version does not match its complete manifest.");
            }

            string key = LeaseKey(snapshot);
            _pins[key] = _pins.GetValueOrDefault(key) + 1;
            return new Lease(this, snapshot, manifest);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<TrafficSnapshotReference?> GetCurrentAsync(
        string graphSha256,
        TrafficSnapshotPolicy policy,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ReadCurrentReferenceCoreAsync(
                graphSha256,
                policy,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Reads, validates, and pins the current policy generation while holding the store gate, closing
    /// the cleanup race between an unpinned current-pointer read and a later acquisition.
    /// </summary>
    public async Task<ITrafficSnapshotLease?> AcquireCurrentAsync(
        string graphSha256,
        TrafficSnapshotPolicy policy,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            TrafficSnapshotReference? snapshot = await ReadCurrentReferenceCoreAsync(
                graphSha256,
                policy,
                cancellationToken).ConfigureAwait(false);
            if (snapshot is null)
            {
                return null;
            }

            TrafficSnapshotManifest manifest = await ReadAndValidateManifestAsync(
                snapshot,
                cancellationToken).ConfigureAwait(false);
            string key = LeaseKey(snapshot);
            _pins[key] = _pins.GetValueOrDefault(key) + 1;
            return new Lease(this, snapshot, manifest);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<TrafficSnapshotReference?> ReadCurrentReferenceCoreAsync(
        string graphSha256,
        TrafficSnapshotPolicy policy,
        CancellationToken cancellationToken)
    {
        string expectedGraphSha = NormalizeSha256(graphSha256);
        string graphRoot = GraphRoot(expectedGraphSha);
        string pairPath = CurrentPairPointerPath(graphRoot);
        string legacyPath = CurrentPointerPath(graphRoot, policy);
        if (!File.Exists(pairPath) && !File.Exists(legacyPath))
        {
            return null;
        }

        TrafficSnapshotReference snapshot;
        try
        {
            if (File.Exists(pairPath))
            {
                await using var pairStream = new FileStream(
                    pairPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    4096,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                TrafficSnapshotPairReference pair =
                    await JsonSerializer.DeserializeAsync<TrafficSnapshotPairReference>(
                        pairStream,
                        cancellationToken: cancellationToken).ConfigureAwait(false)
                    ?? throw new TrafficSnapshotStoreException(
                        TrafficSnapshotFailureCode.Incomplete,
                        "Current traffic snapshot pair pointer is empty.");
                if (pair.Enabled is null || pair.ClosureOnly is null)
                {
                    throw new TrafficSnapshotStoreException(
                        TrafficSnapshotFailureCode.Incomplete,
                        "Current traffic snapshot pair pointer is missing a policy reference.");
                }

                string expectedCohortId = pair.CohortCreatedAtUtc == default
                    ? string.Empty
                    : ComputePairCohortId(
                        pair.Enabled,
                        pair.ClosureOnly,
                        pair.CohortCreatedAtUtc);
                if (string.IsNullOrWhiteSpace(pair.CohortId)
                    || !string.Equals(pair.CohortId, expectedCohortId, StringComparison.Ordinal))
                {
                    throw new TrafficSnapshotStoreException(
                        TrafficSnapshotFailureCode.Incomplete,
                        "Current traffic snapshot pair cohort proof is invalid.");
                }

                ValidateReferenceIdentity(pair.Enabled);
                ValidateReferenceIdentity(pair.ClosureOnly);
                if (pair.Enabled.Policy != TrafficSnapshotPolicy.Enabled
                    || pair.ClosureOnly.Policy != TrafficSnapshotPolicy.ClosureOnly
                    || !string.Equals(pair.Enabled.GraphSha256, expectedGraphSha, StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(pair.ClosureOnly.GraphSha256, expectedGraphSha, StringComparison.OrdinalIgnoreCase))
                {
                    throw new TrafficSnapshotStoreException(
                        TrafficSnapshotFailureCode.Incomplete,
                        "Current traffic snapshot pair pointer is inconsistent.");
                }

                TrafficSnapshotReference other = policy == TrafficSnapshotPolicy.Enabled
                    ? pair.ClosureOnly
                    : pair.Enabled;
                await ReadAndValidateManifestAsync(other, cancellationToken).ConfigureAwait(false);
                snapshot = policy == TrafficSnapshotPolicy.Enabled
                    ? pair.Enabled
                    : pair.ClosureOnly;
            }
            else
            {
                await using var stream = new FileStream(
                    legacyPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    4096,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                snapshot = await JsonSerializer.DeserializeAsync<TrafficSnapshotReference>(
                    stream,
                    cancellationToken: cancellationToken).ConfigureAwait(false)
                    ?? throw new TrafficSnapshotStoreException(
                        TrafficSnapshotFailureCode.Incomplete,
                        "Current traffic snapshot pointer is empty.");
            }
        }
        catch (TrafficSnapshotStoreException)
        {
            throw;
        }
        catch (ArgumentException)
        {
            throw new TrafficSnapshotStoreException(
                TrafficSnapshotFailureCode.Incomplete,
                "Current traffic snapshot pointer contains invalid metadata.");
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            throw new TrafficSnapshotStoreException(
                TrafficSnapshotFailureCode.Unreadable,
                "Current traffic snapshot pointer is unreadable.");
        }
        if (!string.Equals(snapshot.GraphSha256, expectedGraphSha, StringComparison.OrdinalIgnoreCase))
        {
            throw new TrafficSnapshotStoreException(
                TrafficSnapshotFailureCode.GraphMismatch,
                "Current traffic snapshot pointer targets a different graph.");
        }

        ValidateReferenceIdentity(snapshot);
        if (snapshot.Policy != policy)
        {
            throw new TrafficSnapshotStoreException(
                TrafficSnapshotFailureCode.Incomplete,
                "Current traffic snapshot pointer has the wrong policy.");
        }

        if (snapshot.IsExpired(_timeProvider))
        {
            throw new TrafficSnapshotStoreException(
                TrafficSnapshotFailureCode.Expired,
                "Current traffic snapshot is expired.");
        }

        await ReadAndValidateManifestAsync(snapshot, cancellationToken).ConfigureAwait(false);
        return snapshot;
    }

    public async Task CleanupAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (string temporary in Directory.EnumerateDirectories(RootDirectory, ".tmp-*", SearchOption.TopDirectoryOnly))
            {
                cancellationToken.ThrowIfCancellationRequested();
                string normalizedTemporary = Path.GetFullPath(temporary);
                if (!_activeStagingDirectories.ContainsKey(normalizedTemporary))
                {
                    Directory.Delete(normalizedTemporary, true);
                }
            }

            string graphs = Path.Combine(RootDirectory, "graphs");
            if (!Directory.Exists(graphs))
            {
                return;
            }

            foreach (string graphRoot in Directory.EnumerateDirectories(graphs))
            {
                cancellationToken.ThrowIfCancellationRequested();
                string graphSha;
                try
                {
                    graphSha = NormalizeSha256(Path.GetFileName(graphRoot));
                }
                catch (TrafficSnapshotStoreException)
                {
                    continue;
                }

                foreach (string abandonedPointer in Directory.EnumerateFiles(
                             graphRoot,
                             "current-*.json.tmp-*",
                             SearchOption.TopDirectoryOnly))
                {
                    File.Delete(abandonedPointer);
                }

                await CleanupCoreAsync(graphSha, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<TrafficSnapshotManifest> ReadAndValidateManifestAsync(
        TrafficSnapshotReference snapshot,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(snapshot.GenerationDirectory))
        {
            throw new TrafficSnapshotStoreException(TrafficSnapshotFailureCode.Missing, "Traffic snapshot generation is missing.");
        }

        string path = Path.Combine(snapshot.GenerationDirectory, ManifestFileName);
        if (!File.Exists(path))
        {
            throw new TrafficSnapshotStoreException(TrafficSnapshotFailureCode.Incomplete, "Traffic snapshot manifest is missing.");
        }

        TrafficSnapshotManifest manifest;
        try
        {
            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                8192,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            manifest = await JsonSerializer.DeserializeAsync<TrafficSnapshotManifest>(
                stream,
                cancellationToken: cancellationToken).ConfigureAwait(false)
                ?? throw new TrafficSnapshotStoreException(
                    TrafficSnapshotFailureCode.Incomplete,
                    "Traffic snapshot manifest is empty.");
        }
        catch (TrafficSnapshotStoreException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            throw new TrafficSnapshotStoreException(TrafficSnapshotFailureCode.Unreadable, "Traffic snapshot manifest is unreadable.");
        }

        if (!manifest.Complete)
        {
            throw new TrafficSnapshotStoreException(TrafficSnapshotFailureCode.Incomplete, "Traffic snapshot is not complete.");
        }

        if (!string.Equals(manifest.Version, snapshot.Version, StringComparison.OrdinalIgnoreCase))
        {
            throw new TrafficSnapshotStoreException(
                TrafficSnapshotFailureCode.Incomplete,
                "Traffic snapshot version does not match its complete manifest.");
        }

        if (!string.Equals(manifest.GraphSha256, snapshot.GraphSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new TrafficSnapshotStoreException(TrafficSnapshotFailureCode.GraphMismatch, "Traffic snapshot graph fingerprint does not match.");
        }

        if (manifest.Policy != snapshot.Policy || manifest.ExpiresAtUtc != snapshot.ExpiresAtUtc)
        {
            throw new TrafficSnapshotStoreException(
                TrafficSnapshotFailureCode.Incomplete,
                "Traffic snapshot reference does not match manifest policy or immutable validity.");
        }

        await ValidateGenerationAsync(snapshot.GenerationDirectory, manifest, cancellationToken).ConfigureAwait(false);
        return manifest;
    }

    private static async Task ValidateGenerationAsync(
        string directory,
        TrafficSnapshotManifest manifest,
        CancellationToken cancellationToken)
    {
        _ = NormalizeSha256(manifest.GraphSha256);
        if (manifest.ExpiresAtUtc <= manifest.CreatedAtUtc
            || manifest.Tiles is null)
        {
            throw new TrafficSnapshotStoreException(TrafficSnapshotFailureCode.Incomplete, "Traffic snapshot manifest metadata is incomplete.");
        }

        if (manifest.Tiles.GroupBy(static tile => tile.TileId).Any(static group => group.Count() != 1)
            || manifest.Tiles.GroupBy(
                    static tile => tile.RelativePath,
                    StringComparer.OrdinalIgnoreCase)
                .Any(static group => group.Count() != 1))
        {
            throw new TrafficSnapshotStoreException(
                TrafficSnapshotFailureCode.Incomplete,
                "Traffic snapshot manifest contains duplicate tile identities or paths.");
        }

        if (manifest.Complete
            && !string.Equals(manifest.Version, ComputeContentVersion(manifest), StringComparison.OrdinalIgnoreCase))
        {
            throw new TrafficSnapshotStoreException(
                TrafficSnapshotFailureCode.Incomplete,
                "Traffic snapshot manifest version is not its content address.");
        }

        foreach (TrafficSnapshotTileManifest tile in manifest.Tiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string fullPath = Path.GetFullPath(Path.Combine(directory, tile.RelativePath));
            string canonicalPath = Path.GetFullPath(Path.Combine(
                directory,
                GraphTile.FileSuffix(new GraphId(tile.TileId).TileBase())));
            if (!IsDescendant(directory, fullPath)
                || !string.Equals(fullPath, canonicalPath, StringComparison.OrdinalIgnoreCase)
                || !File.Exists(fullPath))
            {
                throw new TrafficSnapshotStoreException(
                    TrafficSnapshotFailureCode.Incomplete,
                    "Traffic snapshot tile is missing or is not stored at its canonical graph-tile path.");
            }

            var fileInfo = new FileInfo(fullPath);
            long expectedLength = checked(TrafficTile.HeaderSize + ((long)tile.DirectedEdgeCount * TrafficTile.SpeedSize));
            if (tile.ByteLength != expectedLength || fileInfo.Length != expectedLength)
            {
                throw new TrafficSnapshotStoreException(TrafficSnapshotFailureCode.Incomplete, "Traffic snapshot tile length is invalid.");
            }

            string hash = await ComputeFileSha256Async(fullPath, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(hash, tile.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new TrafficSnapshotStoreException(TrafficSnapshotFailureCode.Incomplete, "Traffic snapshot tile checksum is invalid.");
            }

            byte[] headerBytes = new byte[TrafficTile.HeaderSize];
            await using var stream = new FileStream(
                fullPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                TrafficTile.HeaderSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            try
            {
                await stream.ReadExactlyAsync(headerBytes, cancellationToken).ConfigureAwait(false);
            }
            catch (EndOfStreamException)
            {
                throw new TrafficSnapshotStoreException(
                    TrafficSnapshotFailureCode.Incomplete,
                    "Traffic snapshot tile header is truncated.");
            }

            ulong headerTileId = BinaryPrimitives.ReadUInt64LittleEndian(headerBytes.AsSpan(0, 8));
            uint headerCount = BinaryPrimitives.ReadUInt32LittleEndian(headerBytes.AsSpan(16, 4));
            uint headerVersion = BinaryPrimitives.ReadUInt32LittleEndian(headerBytes.AsSpan(20, 4));
            if (headerTileId != tile.TileId
                || headerCount != tile.DirectedEdgeCount
                || headerVersion != TrafficTileConstants.TrafficTileVersion)
            {
                throw new TrafficSnapshotStoreException(TrafficSnapshotFailureCode.Incomplete, "Traffic snapshot tile header metadata is invalid.");
            }
        }
    }

    private static async Task PromoteCurrentPairPointerAsync(
        string graphRoot,
        TrafficSnapshotPairReference pair,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(graphRoot);
        string pointer = CurrentPairPointerPath(graphRoot);
        string temporary = pointer + ".tmp-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        await using (var stream = new FileStream(
            temporary,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            4096,
            FileOptions.Asynchronous | FileOptions.WriteThrough))
        {
            await JsonSerializer.SerializeAsync(stream, pair, cancellationToken: cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        File.Move(temporary, pointer, true);
    }

    private async Task PromoteCurrentPointerAsync(
        string graphRoot,
        TrafficSnapshotReference snapshot,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(graphRoot);
        string pointer = CurrentPointerPath(graphRoot, snapshot.Policy);
        string temporary = pointer + ".tmp-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        await using (var stream = new FileStream(
            temporary,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            4096,
            FileOptions.Asynchronous | FileOptions.WriteThrough))
        {
            await JsonSerializer.SerializeAsync(stream, snapshot, cancellationToken: cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        File.Move(temporary, pointer, true);
        string pairPointer = CurrentPairPointerPath(graphRoot);
        if (File.Exists(pairPointer))
        {
            File.Delete(pairPointer);
        }
    }

    private async Task CleanupCoreAsync(string graphSha256, CancellationToken cancellationToken)
    {
        string generations = Path.Combine(GraphRoot(graphSha256), "generations");
        if (!Directory.Exists(generations))
        {
            return;
        }

        HashSet<string> protectedDirectories = await ReadProtectedCurrentDirectoriesAsync(
            GraphRoot(graphSha256),
            generations,
            cancellationToken).ConfigureAwait(false);
        var completed = new List<(string Directory, DateTimeOffset CreatedAtUtc)>();
        foreach (string directory in Directory.EnumerateDirectories(generations))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string manifestPath = Path.Combine(directory, ManifestFileName);
            if (!File.Exists(manifestPath))
            {
                if (!_pins.ContainsKey(directory)
                    && !_pendingGenerations.ContainsKey(Path.GetFullPath(directory))
                    && !protectedDirectories.Contains(Path.GetFullPath(directory)))
                {
                    Directory.Delete(directory, true);
                }

                continue;
            }

            try
            {
                await using var stream = File.OpenRead(manifestPath);
                TrafficSnapshotManifest? manifest = await JsonSerializer.DeserializeAsync<TrafficSnapshotManifest>(
                    stream,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                if (manifest?.Complete == true)
                {
                    completed.Add((directory, manifest.CreatedAtUtc));
                }
            }
            catch (JsonException)
            {
                if (!_pins.ContainsKey(directory)
                    && !_pendingGenerations.ContainsKey(Path.GetFullPath(directory))
                    && !protectedDirectories.Contains(Path.GetFullPath(directory)))
                {
                    Directory.Delete(directory, true);
                }
            }
        }

        foreach ((string directory, _) in completed
                     .OrderByDescending(static item => item.CreatedAtUtc)
                     .Skip(_maxRetainedGenerations))
        {
            if (!_pins.ContainsKey(directory)
                    && !_pendingGenerations.ContainsKey(Path.GetFullPath(directory))
                    && !protectedDirectories.Contains(Path.GetFullPath(directory)))
            {
                Directory.Delete(directory, true);
            }
        }
    }

    private static async Task<HashSet<string>> ReadProtectedCurrentDirectoriesAsync(
        string graphRoot,
        string generationsRoot,
        CancellationToken cancellationToken)
    {
        var protectedDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string pairPointer = CurrentPairPointerPath(graphRoot);
        if (File.Exists(pairPointer))
        {
            try
            {
                await using var pairStream = File.OpenRead(pairPointer);
                TrafficSnapshotPairReference? pair =
                    await JsonSerializer.DeserializeAsync<TrafficSnapshotPairReference>(
                        pairStream,
                        cancellationToken: cancellationToken).ConfigureAwait(false);
                if (pair?.Enabled is not null && pair.ClosureOnly is not null)
                {
                    foreach (TrafficSnapshotReference snapshot in new[] { pair.Enabled, pair.ClosureOnly })
                    {
                        string generation = Path.GetFullPath(snapshot.GenerationDirectory);
                        if (IsDescendant(generationsRoot, generation))
                        {
                            protectedDirectories.Add(generation);
                        }
                    }
                }
            }
            catch (Exception exception) when (exception is IOException
                                                  or JsonException
                                                  or UnauthorizedAccessException
                                                  or ArgumentException)
            {
                // Retention still preserves recent completed generations when a pointer is malformed.
            }
        }

        foreach (TrafficSnapshotPolicy policy in Enum.GetValues<TrafficSnapshotPolicy>())
        {
            string pointer = CurrentPointerPath(graphRoot, policy);
            if (!File.Exists(pointer))
            {
                continue;
            }

            try
            {
                await using var stream = File.OpenRead(pointer);
                TrafficSnapshotReference? snapshot = await JsonSerializer.DeserializeAsync<TrafficSnapshotReference>(
                    stream,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                if (snapshot is not null)
                {
                    string generation = Path.GetFullPath(snapshot.GenerationDirectory);
                    if (IsDescendant(generationsRoot, generation))
                    {
                        protectedDirectories.Add(generation);
                    }
                }
            }
            catch (Exception exception) when (exception is IOException
                                                  or JsonException
                                                  or UnauthorizedAccessException
                                                  or ArgumentException)
            {
                // Cleanup must not turn a malformed pointer into permission to delete a generation.
            }
        }

        return protectedDirectories;
    }

    private void Release(TrafficSnapshotReference snapshot)
    {
        _gate.Wait();
        try
        {
            string key = LeaseKey(snapshot);
            if (!_pins.TryGetValue(key, out int count))
            {
                return;
            }

            if (count <= 1)
            {
                _pins.Remove(key);
            }
            else
            {
                _pins[key] = count - 1;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private void ValidateReferenceIdentity(TrafficSnapshotReference snapshot)
    {
        if (snapshot.Version.Length != 64
            || snapshot.Version.Any(static character => !Uri.IsHexDigit(character)))
        {
            throw new TrafficSnapshotStoreException(
                TrafficSnapshotFailureCode.Incomplete,
                "Traffic snapshot content version is invalid.");
        }

        string expected = Path.GetFullPath(Path.Combine(
            GraphRoot(snapshot.GraphSha256),
            "generations",
            snapshot.Version));
        if (!string.Equals(expected, snapshot.GenerationDirectory, StringComparison.OrdinalIgnoreCase)
            || !IsDescendant(RootDirectory, expected))
        {
            throw new TrafficSnapshotStoreException(
                TrafficSnapshotFailureCode.GraphMismatch,
                "Traffic snapshot generation does not belong to this store and graph.");
        }
    }

    private string GraphRoot(string graphSha256) =>
        Path.Combine(RootDirectory, "graphs", NormalizeSha256(graphSha256));

    private static string NormalizeSha256(string value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Length != 64
            || value.Any(static character => !Uri.IsHexDigit(character)))
        {
            throw new TrafficSnapshotStoreException(
                TrafficSnapshotFailureCode.Incomplete,
                "Graph SHA-256 must be exactly 64 hexadecimal characters.");
        }

        return value.ToUpperInvariant();
    }

    private static string CurrentPairPointerPath(string graphRoot) =>
        Path.Combine(graphRoot, "current-pair.json");

    private static string CurrentPointerPath(string graphRoot, TrafficSnapshotPolicy policy) =>
        Path.Combine(graphRoot, policy == TrafficSnapshotPolicy.Enabled ? "current-enabled.json" : "current-closure-only.json");

    private static string LeaseKey(TrafficSnapshotReference snapshot) => snapshot.GenerationDirectory;

    private static TrafficSnapshotReference ToReference(string destination, TrafficSnapshotManifest manifest) =>
        new(
            manifest.GraphSha256,
            manifest.Version,
            destination,
            manifest.CreatedAtUtc,
            manifest.ExpiresAtUtc,
            manifest.Policy);

    private static string ComputePairCohortId(
        TrafficSnapshotReference enabled,
        TrafficSnapshotReference closureOnly,
        DateTimeOffset cohortCreatedAtUtc)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, enabled.GraphSha256.ToUpperInvariant());
        Append(hash, enabled.Version.ToLowerInvariant());
        Append(hash, closureOnly.Version.ToLowerInvariant());
        Append(hash, cohortCreatedAtUtc.UtcTicks.ToString(CultureInfo.InvariantCulture));
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static string ComputeContentVersion(TrafficSnapshotManifest manifest)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, manifest.GraphSha256.ToUpperInvariant());
        Append(hash, ((int)manifest.Policy).ToString(CultureInfo.InvariantCulture));
        // Creation is an observation timestamp, not traffic identity. Event-derived validity is
        // immutable and remains part of the address so reused content never changes expiration.
        Append(hash, manifest.ExpiresAtUtc.UtcTicks.ToString(CultureInfo.InvariantCulture));
        foreach (TrafficSnapshotTileManifest tile in manifest.Tiles.OrderBy(static item => item.TileId))
        {
            Append(hash, tile.TileId.ToString("X16", CultureInfo.InvariantCulture));
            Append(hash, tile.DirectedEdgeCount.ToString(CultureInfo.InvariantCulture));
            Append(hash, tile.Sha256.ToUpperInvariant());
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static void Append(IncrementalHash hash, string value) =>
        hash.AppendData(Encoding.UTF8.GetBytes(value));

    private static async Task WriteManifestAsync(
        string path,
        TrafficSnapshotManifest manifest,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            8192,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        await JsonSerializer.SerializeAsync(stream, manifest, cancellationToken: cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<string> ComputeFileSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            8192,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash);
    }

    private static bool IsDescendant(string root, string path)
    {
        string normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string normalizedPath = Path.GetFullPath(path);
        return normalizedPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class Lease : ITrafficSnapshotLease
    {
        private readonly TrafficSnapshotStore _owner;
        private readonly IReadOnlyDictionary<ulong, TrafficSnapshotTileManifest> _tiles;
        private int _disposed;

        public Lease(
            TrafficSnapshotStore owner,
            TrafficSnapshotReference snapshot,
            TrafficSnapshotManifest manifest)
        {
            _owner = owner;
            Snapshot = snapshot;
            _tiles = manifest.Tiles.ToDictionary(static tile => tile.TileId);
        }

        public TrafficSnapshotReference Snapshot { get; }

        public GraphMemory? OpenTrafficMemory(GraphId tileId)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            ulong canonical = tileId.TileBase().Value;
            if (!_tiles.TryGetValue(canonical, out TrafficSnapshotTileManifest? tile))
            {
                return null;
            }

            string path = Path.GetFullPath(Path.Combine(Snapshot.GenerationDirectory, tile.RelativePath));
            byte[] bytes = File.ReadAllBytes(path);
            long expectedLength = checked(TrafficTile.HeaderSize + ((long)tile.DirectedEdgeCount * TrafficTile.SpeedSize));
            if (bytes.LongLength != expectedLength)
            {
                throw new TrafficSnapshotStoreException(TrafficSnapshotFailureCode.Incomplete, "Traffic snapshot tile length changed after acquisition.");
            }

            string sha = Convert.ToHexString(SHA256.HashData(bytes));
            if (!string.Equals(sha, tile.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new TrafficSnapshotStoreException(
                    TrafficSnapshotFailureCode.Incomplete,
                    "Traffic snapshot tile checksum changed after acquisition.");
            }

            return new OwnedTrafficGraphMemory(bytes);
        }

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _owner.Release(Snapshot);
            }

            return ValueTask.CompletedTask;
        }
    }

    private sealed class OwnedTrafficGraphMemory : GraphMemory
    {
        public OwnedTrafficGraphMemory(byte[] bytes)
            : base(bytes, 0, bytes.LongLength)
        {
        }
    }
}

public static class GraphFingerprint
{
    public static async Task<string> ComputeSha256Async(
        string graphTileDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(graphTileDirectory);
        string root = Path.GetFullPath(graphTileDirectory);
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException(root);
        }

        string[] files = Directory.EnumerateFiles(root, "*.gph", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(root, "*.gph.gz", SearchOption.AllDirectories))
            .OrderBy(static path => path, StringComparer.Ordinal)
            .ToArray();
        if (files.Length == 0)
        {
            throw new InvalidDataException("The graph contains no Valhalla tiles.");
        }

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (string file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string relative = Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/');
            hash.AppendData(Encoding.UTF8.GetBytes(relative));
            await using var stream = new FileStream(
                file,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                8192,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            byte[] buffer = new byte[8192];
            int read;
            while ((read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) != 0)
            {
                hash.AppendData(buffer, 0, read);
            }
        }

        return Convert.ToHexString(hash.GetHashAndReset());
    }
}

public sealed class DirectoryValhallaTrafficTileWriter : IValhallaTrafficSnapshotPairWriter
{
    private readonly TrafficSnapshotStore _store;
    private readonly TimeProvider _timeProvider;

    public DirectoryValhallaTrafficTileWriter(
        TrafficSnapshotStore store,
        TimeProvider? timeProvider = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public Task<ValhallaTrafficWriteResult> WriteAsync(
        IReadOnlyList<ValhallaTrafficEdgeUpdate> updates,
        ValhallaTrafficWriteOptions options,
        CancellationToken cancellationToken) =>
        WriteCoreAsync(updates, options, promoteCurrent: true, cancellationToken);

    public async Task<ValhallaTrafficSnapshotPairWriteResult> WritePairAsync(
        IReadOnlyList<ValhallaTrafficEdgeUpdate> enabledUpdates,
        ValhallaTrafficWriteOptions enabledOptions,
        IReadOnlyList<ValhallaTrafficEdgeUpdate> closureOnlyUpdates,
        ValhallaTrafficWriteOptions closureOnlyOptions,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(enabledUpdates);
        ArgumentNullException.ThrowIfNull(enabledOptions);
        ArgumentNullException.ThrowIfNull(closureOnlyUpdates);
        ArgumentNullException.ThrowIfNull(closureOnlyOptions);
        cancellationToken.ThrowIfCancellationRequested();
        DateTimeOffset cohortCreatedAtUtc = _timeProvider.GetUtcNow();

        ValhallaTrafficWriteResult enabled = await WriteCoreAsync(
            enabledUpdates,
            enabledOptions with { Policy = TrafficSnapshotPolicy.Enabled },
            promoteCurrent: false,
            cancellationToken).ConfigureAwait(false);
        if (!enabled.Succeeded || enabled.Snapshot is null)
        {
            return new ValhallaTrafficSnapshotPairWriteResult(
                enabled,
                PairFailure("The enabled traffic generation was not created."));
        }

        ValhallaTrafficWriteResult? closureOnly = null;
        try
        {
            closureOnly = await WriteCoreAsync(
                closureOnlyUpdates,
                closureOnlyOptions with { Policy = TrafficSnapshotPolicy.ClosureOnly },
                promoteCurrent: false,
                cancellationToken).ConfigureAwait(false);
            if (!closureOnly.Succeeded || closureOnly.Snapshot is null)
            {
                return new ValhallaTrafficSnapshotPairWriteResult(enabled, closureOnly);
            }

            await _store.PromoteCurrentPairAsync(
                enabled.Snapshot,
                closureOnly.Snapshot,
                cohortCreatedAtUtc,
                cancellationToken).ConfigureAwait(false);
            return new ValhallaTrafficSnapshotPairWriteResult(enabled, closureOnly);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is TrafficSnapshotStoreException
                                          or InvalidDataException
                                          or IOException
                                          or UnauthorizedAccessException)
        {
            return new ValhallaTrafficSnapshotPairWriteResult(
                enabled,
                PairFailure(exception.Message));
        }
        finally
        {
            _store.ReleasePendingGeneration(enabled.Snapshot);
            _store.ReleasePendingGeneration(closureOnly?.Snapshot);
        }
    }

    private static ValhallaTrafficWriteResult PairFailure(string message) =>
        new(
            false,
            0,
            new[]
            {
                new TrafficProviderDiagnostic(
                    "ValhallaTrafficPairPublishFailed",
                    "valhalla",
                    TrafficFeedKind.Composite,
                    message,
                    "[traffic-pair-writer]"),
            });

    private async Task<ValhallaTrafficWriteResult> WriteCoreAsync(
        IReadOnlyList<ValhallaTrafficEdgeUpdate> updates,
        ValhallaTrafficWriteOptions options,
        bool promoteCurrent,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(updates);
        ArgumentNullException.ThrowIfNull(options);
        string? staging = null;
        try
        {
            if (string.IsNullOrWhiteSpace(options.GraphTileDirectory)
                || string.IsNullOrWhiteSpace(options.GraphSha256))
            {
                throw new TrafficSnapshotStoreException(
                    TrafficSnapshotFailureCode.GraphMismatch,
                    "Graph directory and SHA-256 are required.");
            }

            string actualGraphSha = await GraphFingerprint.ComputeSha256Async(
                options.GraphTileDirectory,
                cancellationToken).ConfigureAwait(false);
            if (!string.Equals(actualGraphSha, options.GraphSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new TrafficSnapshotStoreException(
                    TrafficSnapshotFailureCode.GraphMismatch,
                    "The traffic generation graph SHA-256 does not match the target graph.");
            }

            staging = _store.CreateStagingDirectory();
            DateTimeOffset created = options.CreatedAtUtc ?? _timeProvider.GetUtcNow();
            DateTimeOffset expires = options.ExpiresAtUtc ?? created.AddMinutes(2);
            var tileManifests = new List<TrafficSnapshotTileManifest>();
            int effectiveUpdateCount = 0;

            foreach (IGrouping<ulong, ValhallaTrafficEdgeUpdate> tileGroup in updates
                         .Where(static update => update.DirectionResolved)
                         .Select(CanonicalizeStorageIdentity)
                         .GroupBy(static update => new GraphId(update.TileId).TileBase().Value)
                         .OrderBy(static group => group.Key))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var graphId = new GraphId(tileGroup.Key);
                GraphTile graphTile = GraphTile.Create(options.GraphTileDirectory, graphId)
                    ?? throw new TrafficSnapshotStoreException(
                        TrafficSnapshotFailureCode.GraphMismatch,
                        "A traffic update references a graph tile that does not exist.");
                uint edgeCount = graphTile.DirectedEdgeCount();
                // Native Valhalla LastUpdate is the provider observation/snapshot creation time.
                // Generation expiry remains manifest metadata and is never encoded as LastUpdate.
                byte[] bytes = CreateEmptyTile(graphId.Value, edgeCount, created);

                foreach (IGrouping<uint, ValhallaTrafficEdgeUpdate> edgeGroup in tileGroup
                             .GroupBy(static update => update.DirectedEdgeIndex)
                             .OrderBy(static group => group.Key))
                {
                    if (edgeGroup.Key >= edgeCount)
                    {
                        throw new TrafficSnapshotStoreException(
                            TrafficSnapshotFailureCode.GraphMismatch,
                            "A traffic update references a directed edge outside the graph tile.");
                    }

                    ValhallaTrafficEdgeUpdate selected = ComposeEdgeUpdates(edgeGroup);

                    DirectedEdge graphEdge = graphTile.DirectedEdge((int)edgeGroup.Key);
                    TrafficSpeed speed = EncodeSpeed(selected, graphEdge.Length, graphEdge.Speed);
                    BinaryPrimitives.WriteUInt64LittleEndian(
                        bytes.AsSpan(TrafficTile.HeaderSize + (checked((int)edgeGroup.Key) * TrafficTile.SpeedSize), TrafficTile.SpeedSize),
                        speed.RawBits);
                    effectiveUpdateCount++;
                }

                string relativePath = GraphTile.FileSuffix(graphId);
                string path = Path.Combine(staging, relativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await File.WriteAllBytesAsync(path, bytes, cancellationToken).ConfigureAwait(false);
                string sha = Convert.ToHexString(SHA256.HashData(bytes));
                tileManifests.Add(new TrafficSnapshotTileManifest(
                    graphId.Value,
                    edgeCount,
                    relativePath,
                    bytes.LongLength,
                    sha));
            }

            var manifest = new TrafficSnapshotManifest(
                actualGraphSha,
                string.Empty,
                options.Policy,
                created,
                expires,
                false,
                Array.AsReadOnly(tileManifests.ToArray()));
            TrafficSnapshotReference snapshot = await _store.PublishAsync(
                staging,
                manifest,
                promoteCurrent,
                cancellationToken).ConfigureAwait(false);
            staging = null;
            return new ValhallaTrafficWriteResult(
                true,
                effectiveUpdateCount,
                Array.Empty<TrafficProviderDiagnostic>())
            {
                Snapshot = snapshot,
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is TrafficSnapshotStoreException
                                          or InvalidDataException
                                          or IOException
                                          or UnauthorizedAccessException)
        {
            var writeFailure = new TrafficProviderDiagnostic(
                "ValhallaTileWriteFailed",
                "valhalla",
                TrafficFeedKind.Composite,
                exception.Message,
                "[traffic-tile-writer]");
            TrafficProviderDiagnostic[] diagnostics =
                exception is TrafficSnapshotStoreException storeException
                    ?
                    [
                        new TrafficProviderDiagnostic(
                            storeException.Code.ToString(),
                            "valhalla",
                            TrafficFeedKind.Composite,
                            exception.Message,
                            "[traffic-tile-writer]"),
                        writeFailure,
                    ]
                    : [writeFailure];

            return new ValhallaTrafficWriteResult(false, 0, diagnostics);
        }
        finally
        {
            if (staging is not null)
            {
                _store.ReleaseStagingDirectory(staging);
                if (Directory.Exists(staging))
                {
                    Directory.Delete(staging, true);
                }
            }
        }
    }

    private static byte[] CreateEmptyTile(
        ulong tileId,
        uint directedEdgeCount,
        DateTimeOffset contentValidityUtc)
    {
        long length = checked(TrafficTile.HeaderSize + ((long)directedEdgeCount * TrafficTile.SpeedSize));
        if (length > int.MaxValue)
        {
            throw new InvalidDataException("Traffic tile is too large.");
        }

        byte[] bytes = new byte[length];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(0, 8), tileId);
        BinaryPrimitives.WriteUInt64LittleEndian(
            bytes.AsSpan(8, 8),
            checked((ulong)contentValidityUtc.ToUnixTimeSeconds()));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16, 4), directedEdgeCount);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(20, 4), TrafficTileConstants.TrafficTileVersion);
        for (uint index = 0; index < directedEdgeCount; index++)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(
                bytes.AsSpan(TrafficTile.HeaderSize + (checked((int)index) * TrafficTile.SpeedSize), TrafficTile.SpeedSize),
                TrafficSpeed.Invalid.RawBits);
        }

        return bytes;
    }

    private static ValhallaTrafficEdgeUpdate CanonicalizeStorageIdentity(
        ValhallaTrafficEdgeUpdate update)
    {
        var canonical = new GraphId(update.CanonicalDirectedEdgeId);
        return update with
        {
            TileId = canonical.TileBase().Value,
            DirectedEdgeIndex = checked((uint)canonical.Id()),
            GraphDirectedEdgeId = canonical.Value,
        };
    }

    private static ValhallaTrafficEdgeUpdate ComposeEdgeUpdates(
        IEnumerable<ValhallaTrafficEdgeUpdate> updates)
    {
        ValhallaTrafficEdgeUpdate[] ranked = updates
            .OrderByDescending(static update => update.Confidence)
            .ThenBy(static update => update.ProviderId, StringComparer.Ordinal)
            .ThenBy(static update => update.SourceEventId, StringComparer.Ordinal)
            .ToArray();

        ValhallaTrafficEdgeUpdate selected = ranked
            .FirstOrDefault(static update =>
                update.CurrentSpeedKph.HasValue || update.DelaySeconds is > 0)
            ?? ranked[0];

        return selected with
        {
            Closed = ranked.Any(static update => update.Closed),
            HasIncident = ranked.Any(static update => update.HasIncident),
        };
    }

    private static TrafficSpeed EncodeSpeed(
        ValhallaTrafficEdgeUpdate update,
        uint edgeLengthMeters,
        uint graphFreeFlowSpeedKph)
    {
        if (update.Closed)
        {
            return new TrafficSpeed(0, 0, 0, 0, 255, 255, 63, 63, 63, update.HasIncident);
        }

        double? speedKph = update.CurrentSpeedKph;
        if (speedKph is null
            && update.DelaySeconds is > 0
            && update.FreeFlowSpeedKph is > 0
            && edgeLengthMeters > 0)
        {
            double freeFlowSeconds = edgeLengthMeters / (update.FreeFlowSpeedKph.Value / 3.6d);
            speedKph = (edgeLengthMeters / (freeFlowSeconds + update.DelaySeconds.Value)) * 3.6d;
        }

        if (speedKph is null && update.HasIncident)
        {
            // Native incident presence is carried on a valid traffic record. Preserve graph speed
            // when the incident feed has no delay/speed rather than dropping the incident bit.
            speedKph = update.FreeFlowSpeedKph is > 0
                ? update.FreeFlowSpeedKph.Value
                : Math.Max(2d, graphFreeFlowSpeedKph);
        }

        if (speedKph is null)
        {
            return TrafficSpeed.Invalid;
        }

        uint raw = checked((uint)Math.Clamp(
            Math.Round(speedKph.Value / 2d, MidpointRounding.AwayFromZero),
            1d,
            126d));
        return new TrafficSpeed(raw, raw, raw, raw, 255, 255, 0, 0, 0, update.HasIncident);
    }
}
