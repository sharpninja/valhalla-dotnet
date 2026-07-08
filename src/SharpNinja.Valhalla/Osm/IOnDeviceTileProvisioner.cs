namespace SharpNinja.Valhalla.Osm;

/// <summary>
/// Ensures the local Valhalla tile directory consumed by the embedded engine is populated, building
/// tiles on-device from a retrieved <c>.osm.pbf</c> extract when they are missing. Self-contained:
/// the only network step is the OPT-IN extract retrieval via <see cref="IOsmExtractSource"/>; tile
/// building and routing are pure in-process C#.
/// </summary>
public interface IOnDeviceTileProvisioner
{
	/// <summary>
	/// Ensures tiles exist in the configured tile directory. If they are already present, returns it
	/// without building. Otherwise retrieves the extract and builds tiles. The extract source's error
	/// is propagated verbatim when retrieval fails (no build attempted).
	/// </summary>
	Task<TileProvisionResult> EnsureTilesAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Result of <see cref="IOnDeviceTileProvisioner.EnsureTilesAsync"/>.
/// </summary>
/// <param name="Success">True when the configured tile directory contains usable tiles.</param>
/// <param name="TileDirectory">The populated tile directory when <paramref name="Success"/> is true; otherwise null.</param>
/// <param name="Error">A canonical <see cref="OsmRoutingErrorCodes"/> value when <paramref name="Success"/> is false; otherwise null.</param>
public sealed record TileProvisionResult(bool Success, string? TileDirectory, string? Error)
{
	public static TileProvisionResult Provisioned(string tileDirectory) => new(true, tileDirectory, null);

	public static TileProvisionResult Failure(string error) => new(false, null, error);
}
