namespace SharpNinja.Valhalla;

/// <summary>
/// Host-supplied source of the local Valhalla tile directory the embedded engine routes from.
/// The package deliberately does not know how the host resolves or provisions tiles (settings
/// store, downloaded-on-demand from a tiles API, a bundled directory, etc.); it only needs the
/// path. Return null or empty to signal "not configured" (the routing client then reports
/// <see cref="OsmRoutingErrorCodes.NotConfigured"/>).
/// </summary>
public interface IOsmTileDirectoryProvider
{
    /// <summary>Resolves the local Valhalla tile directory, or null/empty when not configured.</summary>
    Task<string?> GetTileDirectoryAsync(CancellationToken cancellationToken = default);
}
