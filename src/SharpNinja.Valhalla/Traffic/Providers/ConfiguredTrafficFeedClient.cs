using System.Net.Http;

using Microsoft.Extensions.Logging;

namespace SharpNinja.Valhalla.Traffic;

/// <summary>
/// Fetches exact host-configured endpoints for one provider. The injected transport remains owned
/// by the host, so existing delegating-handler, proxy, retry, and telemetry pipelines are preserved.
/// </summary>
public sealed class ConfiguredTrafficFeedClient : ITrafficFeedClient
{
    /// <summary>Default maximum response payload retained for one traffic feed.</summary>
    public const int DefaultMaxResponseContentBytes = 16 * 1024 * 1024;

    private readonly HttpMessageInvoker _transport;
    private readonly TrafficFeedEndpoint[] _endpoints;
    private readonly ITrafficProviderCredentialProvider? _credentialProvider;
    private readonly ILogger<ConfiguredTrafficFeedClient>? _logger;
    private readonly TimeProvider _timeProvider;
    private readonly int _maxResponseContentBytes;

    /// <summary>Creates a client over a host-owned <see cref="HttpClient"/> or <see cref="HttpMessageInvoker"/>.</summary>
    public ConfiguredTrafficFeedClient(
        string providerId,
        HttpMessageInvoker transport,
        IReadOnlyList<TrafficFeedEndpoint> endpoints,
        ITrafficProviderCredentialProvider? credentialProvider = null,
        ILogger<ConfiguredTrafficFeedClient>? logger = null,
        TimeProvider? timeProvider = null,
        int maxResponseContentBytes = DefaultMaxResponseContentBytes)
    {
        if (string.IsNullOrWhiteSpace(providerId))
        {
            throw new ArgumentException("A non-empty provider id is required.", nameof(providerId));
        }

        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(endpoints);
        if (endpoints.Count == 0)
        {
            throw new ArgumentException("At least one traffic feed endpoint is required.", nameof(endpoints));
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(maxResponseContentBytes, 1);

        string canonicalProviderId = providerId.Trim();
        foreach (TrafficFeedEndpoint endpoint in endpoints)
        {
            if (!string.Equals(canonicalProviderId, endpoint.ProviderId, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException(
                    "Every endpoint must have the same provider id as the configured client.",
                    nameof(endpoints));
            }
        }

        if (endpoints.GroupBy(static endpoint => endpoint.FeedKind).Any(static group => group.Count() > 1))
        {
            throw new ArgumentException(
                "A configured client may contain only one endpoint for each provider and feed kind.",
                nameof(endpoints));
        }

        ProviderId = canonicalProviderId;
        _transport = transport;
        _endpoints = endpoints.ToArray();
        _credentialProvider = credentialProvider;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _maxResponseContentBytes = maxResponseContentBytes;
    }

    /// <inheritdoc />
    public string ProviderId { get; }

    /// <inheritdoc />
    public async Task<TrafficFeedFetchResult> FetchAsync(
        TrafficDataRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var payloads = new List<RawTrafficFeedPayload>();
        var diagnostics = new List<TrafficProviderDiagnostic>();

        foreach (TrafficFeedEndpoint endpoint in _endpoints)
        {
            if (!request.Includes(endpoint.FeedKind))
            {
                continue;
            }

            cancellationToken.ThrowIfCancellationRequested();
            using var httpRequest = new HttpRequestMessage(HttpMethod.Get, endpoint.Url);
            if (!await ConfigureRequestAsync(
                    httpRequest,
                    endpoint,
                    diagnostics,
                    cancellationToken).ConfigureAwait(false))
            {
                continue;
            }

            if (endpoint.CredentialMode != TrafficFeedCredentialMode.None &&
                (httpRequest.RequestUri is null ||
                 !httpRequest.RequestUri.Scheme.Equals(
                     Uri.UriSchemeHttps,
                     StringComparison.OrdinalIgnoreCase)))
            {
                AddDiagnostic(
                    diagnostics,
                    endpoint,
                    code: "TrafficCredentialTransportInsecure",
                    message: "The traffic feed credential transport must remain HTTPS.");
                continue;
            }

            try
            {
                using HttpResponseMessage response = _transport is HttpClient httpClient
                    ? await httpClient.SendAsync(
                            httpRequest,
                            HttpCompletionOption.ResponseHeadersRead,
                            cancellationToken)
                        .ConfigureAwait(false)
                    : await _transport.SendAsync(httpRequest, cancellationToken)
                        .ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                {
                    AddDiagnostic(
                        diagnostics,
                        endpoint,
                        code: "TrafficHttpFailure",
                        message: "The traffic feed returned a non-success HTTP status.",
                        httpStatusCode: (int)response.StatusCode);
                    continue;
                }

                long? declaredLength = response.Content.Headers.ContentLength;
                if (declaredLength > _maxResponseContentBytes)
                {
                    AddDiagnostic(
                        diagnostics,
                        endpoint,
                        code: "TrafficPayloadTooLarge",
                        message: "The traffic feed response exceeded the configured payload limit.");
                    continue;
                }

                int initialCapacity = declaredLength.HasValue
                    ? (int)declaredLength.Value
                    : Math.Min(81_920, _maxResponseContentBytes);
                using var boundedContent = new BoundedTrafficContentStream(
                    initialCapacity,
                    _maxResponseContentBytes);
                try
                {
                    await response.Content
                        .CopyToAsync(boundedContent, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (Exception exception) when (IsPayloadTooLarge(exception))
                {
                    AddDiagnostic(
                        diagnostics,
                        endpoint,
                        code: "TrafficPayloadTooLarge",
                        message: "The traffic feed response exceeded the configured payload limit.");
                    continue;
                }

                byte[] content = boundedContent.ToArray();
                payloads.Add(new RawTrafficFeedPayload(
                    endpoint.ProviderId,
                    endpoint.FeedKind,
                    response.Content.Headers.ContentType?.ToString() ?? "application/octet-stream",
                    content,
                    _timeProvider.GetUtcNow(),
                    new Uri(
                        TrafficDiagnosticRedaction.RedactUrl(httpRequest.RequestUri ?? endpoint.Url),
                        UriKind.Absolute),
                    CreateProviderMetadata(response, httpRequest.RequestUri)));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                AddDiagnostic(
                    diagnostics,
                    endpoint,
                    code: "TrafficTransportFailure",
                    message: "The traffic feed request failed before a response was received.");
            }
        }

        return new TrafficFeedFetchResult(
            payloads.AsReadOnly(),
            diagnostics.AsReadOnly());
    }

    private async ValueTask<bool> ConfigureRequestAsync(
        HttpRequestMessage request,
        TrafficFeedEndpoint endpoint,
        List<TrafficProviderDiagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        switch (endpoint.CredentialMode)
        {
            case TrafficFeedCredentialMode.None:
                return true;

            case TrafficFeedCredentialMode.QueryParameter:
                if (string.IsNullOrWhiteSpace(endpoint.ApiKeyParameterName))
                {
                    AddDiagnostic(
                        diagnostics,
                        endpoint,
                        code: "TrafficCredentialConfigurationInvalid",
                        message: "The traffic feed credential configuration is invalid.");
                    return false;
                }

                string? queryCredential =
                    await ResolveCredentialAsync(endpoint, diagnostics, cancellationToken).ConfigureAwait(false);
                if (queryCredential is null)
                {
                    return false;
                }

                request.RequestUri = AppendQueryParameter(
                    endpoint.Url,
                    endpoint.ApiKeyParameterName,
                    queryCredential);
                return true;

            case TrafficFeedCredentialMode.Header:
                if (string.IsNullOrWhiteSpace(endpoint.ApiKeyHeaderName))
                {
                    AddDiagnostic(
                        diagnostics,
                        endpoint,
                        code: "TrafficCredentialConfigurationInvalid",
                        message: "The traffic feed credential configuration is invalid.");
                    return false;
                }

                string? headerCredential =
                    await ResolveCredentialAsync(endpoint, diagnostics, cancellationToken).ConfigureAwait(false);
                if (headerCredential is null)
                {
                    return false;
                }

                if (!request.Headers.TryAddWithoutValidation(endpoint.ApiKeyHeaderName, headerCredential))
                {
                    AddDiagnostic(
                        diagnostics,
                        endpoint,
                        code: "TrafficCredentialConfigurationInvalid",
                        message: "The traffic feed credential configuration is invalid.");
                    return false;
                }

                return true;

            case TrafficFeedCredentialMode.CustomRequestMutator:
                if (endpoint.ConfigureRequestAsync is null)
                {
                    AddDiagnostic(
                        diagnostics,
                        endpoint,
                        code: "TrafficCredentialConfigurationInvalid",
                        message: "The traffic feed credential configuration is invalid.");
                    return false;
                }

                try
                {
                    await endpoint.ConfigureRequestAsync(request, cancellationToken).ConfigureAwait(false);
                    return true;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch
                {
                    AddDiagnostic(
                        diagnostics,
                        endpoint,
                        code: "TrafficRequestConfigurationFailed",
                        message: "The traffic feed request could not be configured.");
                    return false;
                }

            default:
                throw new InvalidOperationException(
                    $"Unsupported traffic credential mode value {(int)endpoint.CredentialMode}.");
        }
    }

    private async ValueTask<string?> ResolveCredentialAsync(
        TrafficFeedEndpoint endpoint,
        List<TrafficProviderDiagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        if (_credentialProvider is null)
        {
            AddDiagnostic(
                diagnostics,
                endpoint,
                code: "TrafficCredentialUnavailable",
                message: "The traffic feed credential is unavailable.");
            return null;
        }

        try
        {
            string? credential = await _credentialProvider.GetApiKeyAsync(
                endpoint.ProviderId,
                endpoint.FeedKind,
                endpoint.Url,
                cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(credential))
            {
                AddDiagnostic(
                    diagnostics,
                    endpoint,
                    code: "TrafficCredentialUnavailable",
                    message: "The traffic feed credential is unavailable.");
                return null;
            }

            return credential;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            AddDiagnostic(
                diagnostics,
                endpoint,
                code: "TrafficCredentialProviderFailed",
                message: "The traffic feed credential could not be resolved.");
            return null;
        }
    }


    private static IReadOnlyDictionary<string, string> CreateProviderMetadata(
        HttpResponseMessage response,
        Uri? finalRequestUri)
    {
        var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        AddAllowedHeader(metadata, response.Headers, "TrafficModelID");
        AddAllowedHeader(metadata, response.Headers, "Date");
        AddAllowedHeader(metadata, response.Headers, "ETag");
        AddAllowedHeader(metadata, response.Content.Headers, "Last-Modified");
        AddSafeSpeedUnitMetadata(metadata, finalRequestUri);

        return new System.Collections.ObjectModel.ReadOnlyDictionary<string, string>(metadata);
    }

    private static void AddSafeSpeedUnitMetadata(
        IDictionary<string, string> metadata,
        Uri? finalRequestUri)
    {
        if (finalRequestUri is null ||
            !finalRequestUri.IsAbsoluteUri ||
            string.IsNullOrEmpty(finalRequestUri.Query))
        {
            return;
        }

        foreach (string item in finalRequestUri.Query
                     .TrimStart('?')
                     .Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] pair = item.Split('=', 2);
            if (pair.Length != 2 ||
                !Uri.UnescapeDataString(pair[0])
                    .Equals("unit", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string value = Uri.UnescapeDataString(pair[1]);
            if (value.Equals("mph", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("kmph", StringComparison.OrdinalIgnoreCase))
            {
                metadata["speedUnit"] = value.ToLowerInvariant();
            }

            return;
        }
    }

    private static void AddAllowedHeader(
        IDictionary<string, string> metadata,
        System.Net.Http.Headers.HttpHeaders headers,
        string canonicalName)
    {
        if (!headers.TryGetValues(canonicalName, out IEnumerable<string>? values))
        {
            return;
        }

        string value = string.Join(", ", values);
        if (!string.IsNullOrWhiteSpace(value))
        {
            metadata[canonicalName] = value;
        }
    }

    private void AddDiagnostic(
        List<TrafficProviderDiagnostic> diagnostics,
        TrafficFeedEndpoint endpoint,
        string code,
        string message,
        int? httpStatusCode = null)
    {
        string redactedUrl = TrafficDiagnosticRedaction.RedactUrl(endpoint.Url);
        diagnostics.Add(new TrafficProviderDiagnostic(
            code,
            endpoint.ProviderId,
            endpoint.FeedKind,
            message,
            redactedUrl,
            httpStatusCode));

        _logger?.LogWarning(
            "Traffic provider {ProviderId} feed {FeedKind} at {RedactedSourceUrl} failed with {DiagnosticCode} and HTTP status {HttpStatusCode}.",
            endpoint.ProviderId,
            endpoint.FeedKind,
            redactedUrl,
            code,
            httpStatusCode);
    }

    private static bool IsPayloadTooLarge(Exception exception)
        => exception is TrafficPayloadTooLargeException ||
           (exception.InnerException is not null &&
            IsPayloadTooLarge(exception.InnerException));

    private sealed class BoundedTrafficContentStream : MemoryStream
    {
        private readonly long _maxLength;

        public BoundedTrafficContentStream(int initialCapacity, int maxLength)
            : base(initialCapacity)
        {
            _maxLength = maxLength;
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            EnsureCanWrite(count);
            base.Write(buffer, offset, count);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            EnsureCanWrite(buffer.Length);
            base.Write(buffer);
        }

        public override Task WriteAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureCanWrite(count);
            return base.WriteAsync(buffer, offset, count, cancellationToken);
        }

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureCanWrite(buffer.Length);
            return base.WriteAsync(buffer, cancellationToken);
        }

        public override void WriteByte(byte value)
        {
            EnsureCanWrite(1);
            base.WriteByte(value);
        }

        private void EnsureCanWrite(int count)
        {
            if (count > _maxLength - Length)
            {
                throw new TrafficPayloadTooLargeException();
            }
        }
    }

    private sealed class TrafficPayloadTooLargeException : IOException
    {
    }

    private static Uri AppendQueryParameter(Uri source, string name, string value)
    {
        var builder = new UriBuilder(source);
        string existingQuery = builder.Query.TrimStart('?');
        string encodedPair =
            Uri.EscapeDataString(name) + "=" + Uri.EscapeDataString(value);
        builder.Query = string.IsNullOrEmpty(existingQuery)
            ? encodedPair
            : existingQuery + "&" + encodedPair;
        return builder.Uri;
    }
}
