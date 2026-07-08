namespace SharpNinja.Valhalla;

/// <summary>
/// Canonical OSM routing failure codes surfaced by <see cref="IOsmRoutingClient"/> implementations
/// and consumed by the navigation strategies and CQRS handlers. Relocated (Phase 5) from the removed
/// legacy HTTP Valhalla client so the codes outlive that class. String values are unchanged.
/// </summary>
public static class OsmRoutingErrorCodes
{
    public const string NotConfigured = "not_configured";
    public const string Auth = "auth_error";
    public const string RateLimit = "rate_limit";
    public const string Transport = "transport";
    public const string Parse = "parse";
    public const string Http = "http_error";

    /// <summary>
    /// The configured OSM extract source is present but invalid (e.g. a non-HTTPS URL, or a URL that
    /// does not yield a usable extract filename). Distinct from <see cref="NotConfigured"/> (no URL at
    /// all) and <see cref="Transport"/> (a URL that was fetched but failed at the network/IO layer):
    /// the on-device extract source rejects such sources WITHOUT performing any network I/O.
    /// </summary>
    public const string InvalidSource = "invalid_source";
}
