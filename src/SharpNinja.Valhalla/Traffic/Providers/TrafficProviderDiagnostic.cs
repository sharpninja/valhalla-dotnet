namespace SharpNinja.Valhalla.Traffic;

/// <summary>
/// Credential-safe provider diagnostic. It deliberately carries no exception, request headers,
/// response body, or unredacted URI.
/// </summary>
public sealed record TrafficProviderDiagnostic(
    string Code,
    string ProviderId,
    TrafficFeedKind FeedKind,
    string Message,
    string RedactedSourceUrl,
    int? HttpStatusCode = null);

internal static class TrafficDiagnosticRedaction
{
    public static string RedactUrl(Uri url)
    {
        ArgumentNullException.ThrowIfNull(url);
        if (!url.IsAbsoluteUri)
        {
            return "[invalid-traffic-feed-url]";
        }

        var builder = new UriBuilder(url.Scheme, url.Host)
        {
            Port = url.IsDefaultPort ? -1 : url.Port,
            Path = "/redacted-path",
            Query = string.Empty,
            Fragment = string.Empty,
            UserName = string.Empty,
            Password = string.Empty,
        };

        return builder.Uri.GetComponents(
            UriComponents.SchemeAndServer | UriComponents.Path,
            UriFormat.UriEscaped);
    }
}
