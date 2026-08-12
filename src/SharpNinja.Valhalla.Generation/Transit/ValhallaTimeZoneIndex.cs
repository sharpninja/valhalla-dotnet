using SharpNinja.Valhalla.Baldr;

namespace SharpNinja.Valhalla.Generation.Transit;

/// <summary>Generation compatibility facade over the core Valhalla timezone catalog.</summary>
internal static class ValhallaTimeZoneIndex
{
    public const string UpstreamCommit = ValhallaTimeZoneCatalog.UpstreamCommit;

    public static bool TryGetIndex(string timeZoneId, out uint index) =>
        ValhallaTimeZoneCatalog.TryGetIndex(timeZoneId, out index);
}
