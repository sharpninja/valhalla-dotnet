using SharpNinja.Valhalla.Baldr;
using SharpNinja.Valhalla.Traffic.Tiles;

namespace SharpNinja.Valhalla;

/// <summary>
/// Version-aware provider for embedded graph readers. A traffic-aware async lease pins one immutable
/// traffic generation for the complete route lifetime; baseline callers retain the original
/// source-compatible <see cref="TryGetReader"/> surface.
/// </summary>
public sealed class EmbeddedValhallaGraphReaderFactory : IAsyncDisposable
{
    internal sealed class Entry
    {
        public Entry(
            string key,
            string tileDirectory,
            string? trafficVersion,
            GraphReader reader,
            ITrafficSnapshotLease? trafficLease)
        {
            Key = key;
            TileDirectory = tileDirectory;
            TrafficVersion = trafficVersion;
            Reader = reader;
            TrafficLease = trafficLease;
            Gate = new object();
        }

        public string Key { get; }
        public string TileDirectory { get; }
        public string? TrafficVersion { get; }
        public GraphReader Reader { get; }
        public ITrafficSnapshotLease? TrafficLease { get; }
        public object Gate { get; }
        public int ActiveLeases { get; set; }
        public bool Retired { get; set; }
        public bool Cleared { get; set; }
    }

    private const long DefaultMaxCacheSizeBytes = 268_435_456L;
    private readonly SemaphoreSlim _sync = new(1, 1);
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly TrafficSnapshotStore? _trafficStore;
    private readonly TimeProvider _timeProvider;
    private bool _disposed;
    private int _cacheClearCount;

    public EmbeddedValhallaGraphReaderFactory(
        TrafficSnapshotStore? trafficStore = null,
        TimeProvider? timeProvider = null)
    {
        _trafficStore = trafficStore;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public int CacheClearCount => Volatile.Read(ref _cacheClearCount);

    public readonly struct Lease
    {
        public Lease(GraphReader reader, object gate)
        {
            Reader = reader;
            Gate = gate;
        }

        public GraphReader Reader { get; }
        public object Gate { get; }
    }

    public sealed class AsyncLease : IAsyncDisposable
    {
        private readonly EmbeddedValhallaGraphReaderFactory _owner;
        private Entry? _entry;

        internal AsyncLease(EmbeddedValhallaGraphReaderFactory owner, Entry entry)
        {
            _owner = owner;
            _entry = entry;
            Reader = entry.Reader;
            Gate = entry.Gate;
            TrafficSnapshot = entry.TrafficLease?.Snapshot;
        }

        public GraphReader Reader { get; }
        public object Gate { get; }
        public TrafficSnapshotReference? TrafficSnapshot { get; }

        public ValueTask DisposeAsync()
        {
            Entry? entry = Interlocked.Exchange(ref _entry, null);
            return entry is null ? ValueTask.CompletedTask : _owner.ReleaseAsync(entry);
        }
    }

    public bool TryGetReader(string? tileDirectory, out Lease lease)
    {
        lease = default;
        if (!TryNormalizeTileDirectory(tileDirectory, out string key))
        {
            return false;
        }

        string entryKey = CreateEntryKey(key, null);
        _sync.Wait();
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_entries.TryGetValue(entryKey, out Entry? entry))
            {
                entry = CreateEntry(entryKey, key, null, null);
                if (!HasTiles(entry))
                {
                    return false;
                }

                _entries.Add(entryKey, entry);
            }

            lease = new Lease(entry.Reader, entry.Gate);
            return true;
        }
        finally
        {
            _sync.Release();
        }
    }

    public async ValueTask<AsyncLease> AcquireAsync(
        string? tileDirectory,
        TrafficSnapshotReference? trafficSnapshot = null,
        CancellationToken cancellationToken = default)
    {
        if (!TryNormalizeTileDirectory(tileDirectory, out string key))
        {
            throw new DirectoryNotFoundException("A valid Valhalla graph tile directory is required.");
        }

        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        ITrafficSnapshotLease? storeLease = null;
        try
        {
            if (trafficSnapshot is not null)
            {
                if (_trafficStore is null)
                {
                    throw new TrafficSnapshotStoreException(
                        TrafficSnapshotFailureCode.Missing,
                        "A traffic snapshot was requested without a configured snapshot store.");
                }

                if (trafficSnapshot.IsExpired(_timeProvider))
                {
                    throw new TrafficSnapshotStoreException(
                        TrafficSnapshotFailureCode.Expired,
                        "The requested traffic snapshot is expired.");
                }

                string graphSha = await GraphFingerprint.ComputeSha256Async(key, cancellationToken).ConfigureAwait(false);
                if (!string.Equals(graphSha, trafficSnapshot.GraphSha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new TrafficSnapshotStoreException(
                        TrafficSnapshotFailureCode.GraphMismatch,
                        "The requested traffic snapshot targets a different graph.");
                }

                storeLease = await _trafficStore.AcquireAsync(trafficSnapshot, cancellationToken).ConfigureAwait(false);
            }

            string version = trafficSnapshot?.Version ?? "baseline";
            string entryKey = CreateEntryKey(key, version);
            await _sync.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_entries.TryGetValue(entryKey, out Entry? cached))
                {
                    cached.ActiveLeases++;
                    if (storeLease is not null)
                    {
                        await storeLease.DisposeAsync().ConfigureAwait(false);
                        storeLease = null;
                    }

                    return new AsyncLease(this, cached);
                }

                Entry entry = CreateEntry(entryKey, key, trafficSnapshot?.Version, storeLease);
                storeLease = null;
                if (!HasTiles(entry))
                {
                    await DisposeEntryAsync(entry).ConfigureAwait(false);
                    throw new DirectoryNotFoundException("The Valhalla graph contains no readable tiles.");
                }

                Entry[] previousEntries = _entries.Values.Where(item =>
                        item.TrafficVersion is not null
                        && string.Equals(item.TileDirectory, key, StringComparison.Ordinal)
                        && !string.Equals(item.TrafficVersion, entry.TrafficVersion, StringComparison.Ordinal))
                    .ToArray();
                foreach (Entry previous in previousEntries)
                {
                    previous.Retired = true;
                    if (previous.ActiveLeases != 0)
                    {
                        continue;
                    }

                    ClearOnce(previous);
                    _entries.Remove(previous.Key);
                    await DisposeEntryAsync(previous).ConfigureAwait(false);
                }

                entry.ActiveLeases = 1;
                _entries.Add(entryKey, entry);
                return new AsyncLease(this, entry);
            }
            finally
            {
                _sync.Release();
            }
        }
        catch
        {
            if (storeLease is not null)
            {
                await storeLease.DisposeAsync().ConfigureAwait(false);
            }

            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _sync.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            Entry[] entries = _entries.Values.ToArray();
            foreach (Entry entry in entries)
            {
                entry.Retired = true;
                if (entry.ActiveLeases != 0)
                {
                    continue;
                }

                ClearOnce(entry);
                _entries.Remove(entry.Key);
                await DisposeEntryAsync(entry).ConfigureAwait(false);
            }
        }
        finally
        {
            _sync.Release();
        }
    }

    private Entry CreateEntry(
        string entryKey,
        string tileDirectory,
        string? trafficVersion,
        ITrafficSnapshotLease? trafficLease)
    {
        var reader = new GraphReader(new GraphReader.Config
        {
            TileDir = tileDirectory,
            MaxCacheSize = DefaultMaxCacheSizeBytes,
            UseLruMemCache = true,
            LruMemCacheHardControl = true,
            TrafficSnapshot = trafficLease,
        });
        return new Entry(entryKey, tileDirectory, trafficVersion, reader, trafficLease);
    }

    private static bool HasTiles(Entry entry)
    {
        lock (entry.Gate)
        {
            return entry.Reader.GetTileSet().Count != 0;
        }
    }

    private async ValueTask ReleaseAsync(Entry entry)
    {
        await _sync.WaitAsync().ConfigureAwait(false);
        try
        {
            if (entry.ActiveLeases > 0)
            {
                entry.ActiveLeases--;
            }

            if (entry.Retired && entry.ActiveLeases == 0)
            {
                ClearOnce(entry);
                _entries.Remove(entry.Key);
                await DisposeEntryAsync(entry).ConfigureAwait(false);
            }
        }
        finally
        {
            _sync.Release();
        }
    }

    private void ClearOnce(Entry entry)
    {
        if (entry.Cleared)
        {
            return;
        }

        lock (entry.Gate)
        {
            entry.Reader.Clear();
        }

        entry.Cleared = true;
        Interlocked.Increment(ref _cacheClearCount);
    }

    private static async ValueTask DisposeEntryAsync(Entry entry)
    {
        if (entry.TrafficLease is not null)
        {
            await entry.TrafficLease.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static string CreateEntryKey(string tileDirectory, string? version) =>
        tileDirectory + "|" + (version ?? "baseline");

    private static bool TryNormalizeTileDirectory(string? tileDirectory, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(tileDirectory))
        {
            return false;
        }

        normalized = Path.GetFullPath(tileDirectory.Trim());
        return Directory.Exists(normalized);
    }
}
