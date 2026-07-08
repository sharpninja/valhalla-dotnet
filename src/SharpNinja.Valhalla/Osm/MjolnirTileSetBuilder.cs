using SharpNinja.Valhalla.Mjolnir;

namespace SharpNinja.Valhalla.Osm;

/// <summary>
/// Production <see cref="ITileSetBuilder"/>: calls the ported mjolnir
/// <see cref="TileBuilder.BuildTileSet(System.Collections.Generic.IReadOnlyList{string}, string, TileBuilderConfig)"/>
/// with hierarchy + shortcuts enabled (the same config the Monaco build+route E2E uses).
/// </summary>
public sealed class MjolnirTileSetBuilder : ITileSetBuilder
{
	public bool BuildTiles(string pbfPath, string tileDirectory, CancellationToken cancellationToken = default)
	{
		ArgumentException.ThrowIfNullOrEmpty(pbfPath);
		ArgumentException.ThrowIfNullOrEmpty(tileDirectory);

		cancellationToken.ThrowIfCancellationRequested();

		var config = new TileBuilderConfig
		{
			Hierarchy = true,
			Shortcuts = true,
		};

		var result = TileBuilder.BuildTileSet(new[] { pbfPath }, tileDirectory, config);
		return result.Success && result.TileCount > 0;
	}
}
