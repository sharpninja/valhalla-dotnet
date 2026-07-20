using System.Net.Http;

namespace SharpNinja.Valhalla.Traffic;

/// <summary>
/// The credential behavior applied to one exact traffic-feed endpoint.
/// </summary>
public enum TrafficFeedCredentialMode
{
    /// <summary>
    /// Send the exact configured URL without client-side credentials. This supports central
    /// proxies that inject credentials after the request leaves the process.
    /// </summary>
    None,

    /// <summary>Append the resolved credential as the configured query parameter.</summary>
    QueryParameter,

    /// <summary>Send the resolved credential in the configured request header.</summary>
    Header,

    /// <summary>Delegate request configuration to the host-supplied asynchronous mutator.</summary>
    CustomRequestMutator,
}

/// <summary>
/// Exact endpoint configuration for one provider feed.
/// </summary>
public sealed record TrafficFeedEndpoint
{
    /// <summary>Creates and validates an exact endpoint configuration.</summary>
    public TrafficFeedEndpoint(
        string ProviderId,
        TrafficFeedKind FeedKind,
        Uri Url,
        TrafficFeedCredentialMode CredentialMode,
        string? ApiKeyParameterName = null,
        string? ApiKeyHeaderName = null,
        Func<HttpRequestMessage, CancellationToken, ValueTask>? ConfigureRequestAsync = null)
    {
        if (string.IsNullOrWhiteSpace(ProviderId))
        {
            throw new ArgumentException("A non-empty provider id is required.", nameof(ProviderId));
        }

        if (!Enum.IsDefined(FeedKind))
        {
            throw new ArgumentOutOfRangeException(nameof(FeedKind));
        }

        ArgumentNullException.ThrowIfNull(Url);
        if (!Url.IsAbsoluteUri
            || (Url.Scheme != Uri.UriSchemeHttp && Url.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException(
                "Traffic feed URLs must be absolute HTTP or HTTPS URLs.",
                nameof(Url));
        }

        if (!string.IsNullOrEmpty(Url.UserInfo) || !string.IsNullOrEmpty(Url.Fragment))
        {
            throw new ArgumentException(
                "Traffic feed URLs must not contain user-info credentials or fragments.",
                nameof(Url));
        }

        if (!Enum.IsDefined(CredentialMode))
        {
            throw new ArgumentOutOfRangeException(nameof(CredentialMode));
        }

        if (CredentialMode != TrafficFeedCredentialMode.None &&
            !Url.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "Traffic endpoints that attach credentials must use HTTPS.",
                nameof(Url));
        }

        ValidateCredentialConfiguration(
            CredentialMode,
            ApiKeyParameterName,
            ApiKeyHeaderName,
            ConfigureRequestAsync);

        this.ProviderId = ProviderId.Trim();
        this.FeedKind = FeedKind;
        this.Url = Url;
        this.CredentialMode = CredentialMode;
        this.ApiKeyParameterName = ApiKeyParameterName;
        this.ApiKeyHeaderName = ApiKeyHeaderName;
        this.ConfigureRequestAsync = ConfigureRequestAsync;
    }

    /// <summary>Canonical provider registration id.</summary>
    public string ProviderId { get; }

    /// <summary>Kind of feed exposed at this exact endpoint.</summary>
    public TrafficFeedKind FeedKind { get; }

    /// <summary>The exact absolute HTTP(S) URL supplied by the host.</summary>
    public Uri Url { get; }

    /// <summary>Credential behavior for this endpoint.</summary>
    public TrafficFeedCredentialMode CredentialMode { get; }

    /// <summary>Query parameter name used only by <see cref="TrafficFeedCredentialMode.QueryParameter"/>.</summary>
    public string? ApiKeyParameterName { get; }

    /// <summary>Header name used only by <see cref="TrafficFeedCredentialMode.Header"/>.</summary>
    public string? ApiKeyHeaderName { get; }

    /// <summary>
    /// Host request mutation used only by
    /// <see cref="TrafficFeedCredentialMode.CustomRequestMutator"/>.
    /// </summary>
    public Func<HttpRequestMessage, CancellationToken, ValueTask>? ConfigureRequestAsync { get; }

    private static void ValidateCredentialConfiguration(
        TrafficFeedCredentialMode credentialMode,
        string? apiKeyParameterName,
        string? apiKeyHeaderName,
        Func<HttpRequestMessage, CancellationToken, ValueTask>? configureRequestAsync)
    {
        switch (credentialMode)
        {
            case TrafficFeedCredentialMode.None:
                if (apiKeyParameterName is not null
                    || apiKeyHeaderName is not null
                    || configureRequestAsync is not null)
                {
                    throw new ArgumentException(
                        "CredentialMode.None cannot include credential names or a request mutator.");
                }

                break;

            case TrafficFeedCredentialMode.QueryParameter:
                if (!IsValidQueryParameterName(apiKeyParameterName)
                    || apiKeyHeaderName is not null
                    || configureRequestAsync is not null)
                {
                    throw new ArgumentException(
                        "QueryParameter mode requires only a valid API-key query parameter name.");
                }

                break;

            case TrafficFeedCredentialMode.Header:
                if (!IsValidHeaderName(apiKeyHeaderName)
                    || apiKeyParameterName is not null
                    || configureRequestAsync is not null)
                {
                    throw new ArgumentException(
                        "Header mode requires only a valid API-key header name.");
                }

                break;

            case TrafficFeedCredentialMode.CustomRequestMutator:
                if (configureRequestAsync is null
                    || apiKeyParameterName is not null
                    || apiKeyHeaderName is not null)
                {
                    throw new ArgumentException(
                        "CustomRequestMutator mode requires only a request mutator.");
                }

                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(credentialMode));
        }
    }

    private static bool IsValidQueryParameterName(string? name)
        => !string.IsNullOrWhiteSpace(name)
           && name.All(static character =>
               char.IsLetterOrDigit(character)
               || character is '-' or '.' or '_' or '~');

    private static bool IsValidHeaderName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        const string tokenSymbols = "!#$%&'*+-.^_`|~";
        return name.All(character =>
            char.IsAsciiLetterOrDigit(character)
            || tokenSymbols.Contains(character, StringComparison.Ordinal));
    }
}

/// <summary>
/// Resolves provider credentials without making the configured client depend on a secret store.
/// </summary>
public interface ITrafficProviderCredentialProvider
{
    /// <summary>Returns the credential for one provider feed, or null when unavailable.</summary>
    ValueTask<string?> GetApiKeyAsync(
        string providerId,
        TrafficFeedKind feedKind,
        Uri feedUrl,
        CancellationToken cancellationToken);
}
