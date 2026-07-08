namespace SharpNinja.Valhalla.Osm;

/// <summary>
/// Tiny seam over the ported mjolnir
/// <see cref="SharpNinja.Valhalla.Mjolnir.TileBuilder.BuildTileSet(System.Collections.Generic.IReadOnlyList{string}, string, SharpNinja.Valhalla.Mjolnir.TileBuilderConfig)"/>
/// so <see cref="OnDeviceTileProvisioner"/> is unit-testable without a real PBF. The production
/// implementation (<see cref="MjolnirTileSetBuilder"/>) calls straight through to the real builder.
/// </summary>
public interface ITileSetBuilder
{
	/// <summary>
	/// Builds a Valhalla tile set from the given <c>.osm.pbf</c> extract into <paramref name="tileDirectory"/>
	/// with hierarchy + shortcuts enabled.
	/// </summary>
	/// <returns>True when tiles were produced.</returns>
	bool BuildTiles(string pbfPath, string tileDirectory, CancellationToken cancellationToken = default);
}
