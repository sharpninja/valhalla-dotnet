using SharpNinja.Valhalla.Baldr;

namespace SharpNinja.Valhalla;

/// <summary>
/// Host-level provider that builds and caches a single long-lived <see cref="GraphReader"/> per
/// tile-directory for the in-process (embedded) Valhalla engine. Tile loading is expensive and the
/// <see cref="GraphReader"/> is the intended long-lived object, so the reader (and its tile cache)
/// are reused across requests. The cache is keyed by the tile-directory string so a settings change
/// transparently rebuilds the reader.
/// </summary>
/// <remarks>
/// The ported <see cref="GraphReader"/> tile caches are NOT thread-safe. Because
/// <c>EmbeddedValhallaRoutingClient.CalculateRouteAsync</c> runs the CPU-bound core on a worker
/// thread (<see cref="System.Threading.Tasks.Task.Run(System.Action)"/>), callers MUST serialize
/// reader use. <see cref="Lease"/> hands out the reader together with the gate object the client
/// locks on for the duration of a route computation.
/// </remarks>
public sealed class EmbeddedValhallaGraphReaderFactory
{
	// Per-tile-dir cached reader + the gate that serializes its (non-thread-safe) tile cache.
	private sealed class Entry
	{
		public Entry(GraphReader reader)
		{
			Reader = reader;
			Gate = new object();
		}

		public GraphReader Reader { get; }

		public object Gate { get; }
	}

	// 256 MiB on-device budget for the LRU tile cache (the engine default is 1 GiB which is too
	// large for a phone). Hard control evicts on every Put so the footprint stays bounded.
	private const long DefaultMaxCacheSizeBytes = 268_435_456L;

	private readonly object _sync = new();
	private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);

	/// <summary>
	/// A serialized lease over a cached <see cref="GraphReader"/>. The client locks on
	/// <see cref="Gate"/> while it correlates / routes / builds, because the ported tile caches are
	/// not thread-safe and the reader is shared across requests.
	/// </summary>
	public readonly struct Lease
	{
		public Lease(GraphReader reader, object gate)
		{
			Reader = reader;
			Gate = gate;
		}

		/// <summary>The cached graph reader.</summary>
		public GraphReader Reader { get; }

		/// <summary>The object to lock on for the duration of a single route computation.</summary>
		public object Gate { get; }
	}

	/// <summary>
	/// Resolves the cached reader for <paramref name="tileDirectory"/>, building it on first use.
	/// Returns false when the tile directory is unset, missing on disk, or contains no parsable
	/// tiles (the caller maps that to <c>not_configured</c>).
	/// </summary>
	/// <param name="tileDirectory">The local Valhalla tile directory.</param>
	/// <param name="lease">The serialized reader lease when available.</param>
	/// <returns>True when a usable reader (non-empty tile set) is available.</returns>
	public bool TryGetReader(string? tileDirectory, out Lease lease)
	{
		lease = default;

		if (string.IsNullOrWhiteSpace(tileDirectory))
		{
			return false;
		}

		var key = tileDirectory.Trim();
		if (!Directory.Exists(key))
		{
			return false;
		}

		Entry entry;
		lock (_sync)
		{
			if (!_entries.TryGetValue(key, out var cached))
			{
				var reader = new GraphReader(new GraphReader.Config
				{
					TileDir = key,
					MaxCacheSize = DefaultMaxCacheSizeBytes,
					UseLruMemCache = true,
					LruMemCacheHardControl = true,
				});

				cached = new Entry(reader);
				_entries[key] = cached;
			}

			entry = cached;
		}

		// Tile-set emptiness => not configured. Enumerate under the gate: GetTileSet only walks the
		// directory but keeping all reader access serialized avoids surprises with the shared cache.
		lock (entry.Gate)
		{
			if (entry.Reader.GetTileSet().Count == 0)
			{
				return false;
			}
		}

		lease = new Lease(entry.Reader, entry.Gate);
		return true;
	}
}
