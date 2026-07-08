namespace SharpNinja.Valhalla.Osm;

/// <summary>
/// OPT-IN, self-contained retrieval seam for the OSM <c>.osm.pbf</c> extract that the on-device
/// mjolnir tile builder consumes. The single concrete implementation
/// (<see cref="HttpOsmExtractSource"/>) reads its source URL + data directory from
/// <see cref="TruckMate.Core.Models.OsmProviderSettings"/>; when no source URL is configured it
/// performs NO network I/O and returns a not-configured result. Nothing phones home unless the user
/// explicitly sets a URL.
/// </summary>
/// <remarks>
/// The ported <c>PbfGraphParser</c> / <see cref="SharpNinja.Valhalla.Mjolnir.TileBuilder"/>
/// consume <c>.osm.pbf</c> only (protobuf), NOT the XML/JSON Overpass returns, so retrieval-for-tile-build
/// is a download of a configured regional <c>.osm.pbf</c> extract over HTTPS, never an Overpass query.
/// </remarks>
public interface IOsmExtractSource
{
	/// <summary>
	/// Ensures a local <c>.osm.pbf</c> extract is available, downloading it only when configured and
	/// not already cached on disk. On a cache hit (the target file already exists) the cached path is
	/// returned IMMEDIATELY with ZERO network I/O: nothing contacts the network unless a fresh download
	/// is actually needed. Never throws except <see cref="OperationCanceledException"/> on cancellation:
	/// every transport / I/O failure is mapped to an unsuccessful <see cref="OsmExtractResult"/>.
	/// </summary>
	Task<OsmExtractResult> EnsureExtractAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Result of <see cref="IOsmExtractSource.EnsureExtractAsync"/>.
/// </summary>
/// <param name="Success">True when a usable local <c>.osm.pbf</c> path is available.</param>
/// <param name="PbfPath">The absolute path to the local extract when <paramref name="Success"/> is true; otherwise null.</param>
/// <param name="Error">
/// A canonical <see cref="OsmRoutingErrorCodes"/> value when <paramref name="Success"/> is false;
/// otherwise null.
/// </param>
public sealed record OsmExtractResult(bool Success, string? PbfPath, string? Error)
{
	public static OsmExtractResult Configured(string pbfPath) => new(true, pbfPath, null);

	public static OsmExtractResult Failure(string error) => new(false, null, error);
}
