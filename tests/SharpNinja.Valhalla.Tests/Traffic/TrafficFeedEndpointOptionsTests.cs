using System.Net;
using System.Net.Http;

using SharpNinja.Valhalla.Traffic;

namespace SharpNinja.Valhalla.Tests.Traffic;

public class TrafficFeedEndpointOptionsTests
{
    [Fact]
    public async Task ExactFeedUrl_IsUsedWithoutReconstruction()
    {
        var handler = new RecordingHttpMessageHandler();
        using var httpClient = new HttpClient(handler);
        var endpoint = new TrafficFeedEndpoint(
            "proxy",
            TrafficFeedKind.Composite,
            new Uri("https://traffic.example.test/custom/v7/feed.json?tenant=nashville&format=raw", UriKind.Absolute),
            TrafficFeedCredentialMode.None);
        var client = CreateClient(httpClient, [endpoint]);

        TrafficFeedFetchResult result = await client.FetchAsync(new TrafficDataRequest(), TestContext.Current.CancellationToken);

        Assert.Empty(result.Diagnostics);
        Assert.Single(result.Payloads);
        HttpRequestMessage request = Assert.Single(handler.Requests);
        Assert.Equal(endpoint.Url.OriginalString, request.RequestUri!.OriginalString);
    }

    [Fact]
    public async Task SeparateFeedUrls_AreFetchedIndependently()
    {
        var handler = new RecordingHttpMessageHandler();
        using var httpClient = new HttpClient(handler);
        TrafficFeedEndpoint[] endpoints =
        [
            Endpoint(TrafficFeedKind.Flow, "https://flow.example.test/nashville"),
            Endpoint(TrafficFeedKind.Incident, "https://incident.example.test/nashville"),
            Endpoint(TrafficFeedKind.Closure, "https://closure.example.test/nashville"),
            Endpoint(TrafficFeedKind.Restriction, "https://restriction.example.test/nashville"),
            Endpoint(TrafficFeedKind.Composite, "https://composite.example.test/nashville"),
        ];
        var client = CreateClient(httpClient, endpoints);

        TrafficFeedFetchResult result = await client.FetchAsync(new TrafficDataRequest(), TestContext.Current.CancellationToken);

        Assert.Empty(result.Diagnostics);
        Assert.Equal(5, result.Payloads.Count);
        Assert.Equal(
            endpoints.Select(static endpoint => endpoint.Url),
            handler.Requests.Select(static request => request.RequestUri));
    }

    [Fact]
    public async Task ProxyInjectedCredentialMode_DoesNotAppendOrSendApiKey()
    {
        var handler = new RecordingHttpMessageHandler();
        using var httpClient = new HttpClient(handler);
        var endpoint = Endpoint(
            TrafficFeedKind.Flow,
            "https://traffic-proxy.example.test/flow?region=nashville");
        var client = CreateClient(httpClient, [endpoint], new RecordingCredentialProvider("must-not-be-used"));

        await client.FetchAsync(new TrafficDataRequest(), TestContext.Current.CancellationToken);

        HttpRequestMessage request = Assert.Single(handler.Requests);
        Assert.Equal(endpoint.Url, request.RequestUri);
        Assert.DoesNotContain(
            request.Headers,
            static header => header.Key.Contains("key", StringComparison.OrdinalIgnoreCase)
                || header.Key.Contains("auth", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ProxyInjectedCredentialMode_DoesNotCallCredentialProvider()
    {
        var credentialProvider = new RecordingCredentialProvider("must-not-be-used");
        var handler = new RecordingHttpMessageHandler();
        using var httpClient = new HttpClient(handler);
        var client = CreateClient(
            httpClient,
            [Endpoint(TrafficFeedKind.Flow, "https://traffic-proxy.example.test/flow")],
            credentialProvider);

        await client.FetchAsync(new TrafficDataRequest(), TestContext.Current.CancellationToken);

        Assert.Equal(0, credentialProvider.CallCount);
    }

    [Fact]
    public async Task QueryApiKeyMode_AppendsConfiguredParameter()
    {
        var credentialProvider = new RecordingCredentialProvider("query secret + value");
        var handler = new RecordingHttpMessageHandler();
        using var httpClient = new HttpClient(handler);
        var endpoint = new TrafficFeedEndpoint(
            "tomtom",
            TrafficFeedKind.Flow,
            new Uri("https://traffic.example.test/flow?region=nashville", UriKind.Absolute),
            TrafficFeedCredentialMode.QueryParameter,
            ApiKeyParameterName: "subscription-key");
        var client = CreateClient(httpClient, [endpoint], credentialProvider);

        await client.FetchAsync(new TrafficDataRequest(), TestContext.Current.CancellationToken);

        Uri requestUri = Assert.Single(handler.Requests).RequestUri!;
        Assert.Equal("?region=nashville&subscription-key=query%20secret%20%2B%20value", requestUri.Query);
        Assert.Equal(1, credentialProvider.CallCount);
    }

    [Fact]
    public async Task HeaderApiKeyMode_SendsConfiguredHeader()
    {
        var credentialProvider = new RecordingCredentialProvider("header-secret");
        var handler = new RecordingHttpMessageHandler();
        using var httpClient = new HttpClient(handler);
        var endpoint = new TrafficFeedEndpoint(
            "here",
            TrafficFeedKind.Incident,
            new Uri("https://traffic.example.test/incidents", UriKind.Absolute),
            TrafficFeedCredentialMode.Header,
            ApiKeyHeaderName: "X-Central-Traffic-Key");
        var client = CreateClient(httpClient, [endpoint], credentialProvider);

        await client.FetchAsync(new TrafficDataRequest(), TestContext.Current.CancellationToken);

        HttpRequestMessage request = Assert.Single(handler.Requests);
        Assert.Equal(["header-secret"], request.Headers.GetValues("X-Central-Traffic-Key"));
        Assert.DoesNotContain("header-secret", request.RequestUri!.OriginalString, StringComparison.Ordinal);
        Assert.Equal(1, credentialProvider.CallCount);
    }

    [Fact]
    public async Task CustomRequestMutator_CanModifyRequest()
    {
        var handler = new RecordingHttpMessageHandler();
        using var httpClient = new HttpClient(handler);
        var endpoint = new TrafficFeedEndpoint(
            "future-provider",
            TrafficFeedKind.Composite,
            new Uri("https://traffic.example.test/future", UriKind.Absolute),
            TrafficFeedCredentialMode.CustomRequestMutator,
            ConfigureRequestAsync: (request, _) =>
            {
                request.Headers.Add("X-Proxy-Tenant", "nashville");
                return ValueTask.CompletedTask;
            });
        var client = CreateClient(httpClient, [endpoint]);

        await client.FetchAsync(new TrafficDataRequest(), TestContext.Current.CancellationToken);

        HttpRequestMessage request = Assert.Single(handler.Requests);
        Assert.Equal(["nashville"], request.Headers.GetValues("X-Proxy-Tenant"));
    }

    [Theory]
    [InlineData(TrafficFeedCredentialMode.QueryParameter)]
    [InlineData(TrafficFeedCredentialMode.Header)]
    [InlineData(TrafficFeedCredentialMode.CustomRequestMutator)]
    public void SecretBearingCredentialModes_RejectPlaintextHttp(
        TrafficFeedCredentialMode credentialMode)
    {
        Assert.Throws<ArgumentException>(() => new TrafficFeedEndpoint(
            "secured-provider",
            TrafficFeedKind.Flow,
            new Uri("http://traffic.example.test/secured", UriKind.Absolute),
            credentialMode,
            ApiKeyParameterName:
                credentialMode == TrafficFeedCredentialMode.QueryParameter
                    ? "key"
                    : null,
            ApiKeyHeaderName:
                credentialMode == TrafficFeedCredentialMode.Header
                    ? "X-Api-Key"
                    : null,
            ConfigureRequestAsync:
                credentialMode == TrafficFeedCredentialMode.CustomRequestMutator
                    ? static (_, _) => ValueTask.CompletedTask
                    : null));
    }

    [Fact]
    public void CredentialModeNone_AllowsPlaintextHttpForHostCredentialProxy()
    {
        var endpoint = new TrafficFeedEndpoint(
            "local-proxy",
            TrafficFeedKind.Composite,
            new Uri("http://127.0.0.1:7147/vendor/tomtom/traffic", UriKind.Absolute),
            TrafficFeedCredentialMode.None);

        Assert.Equal(Uri.UriSchemeHttp, endpoint.Url.Scheme);
        Assert.Equal(TrafficFeedCredentialMode.None, endpoint.CredentialMode);
    }

    [Fact]
    public void CredentialModeNone_RejectsCredentialFieldsAndCustomMutator()
    {
        Uri url = new("https://traffic-proxy.example.test/flow", UriKind.Absolute);

        Assert.Throws<ArgumentException>(() => new TrafficFeedEndpoint(
            "proxy",
            TrafficFeedKind.Flow,
            url,
            TrafficFeedCredentialMode.None,
            ApiKeyParameterName: "key"));

        Assert.Throws<ArgumentException>(() => new TrafficFeedEndpoint(
            "proxy",
            TrafficFeedKind.Flow,
            url,
            TrafficFeedCredentialMode.None,
            ApiKeyHeaderName: "Authorization"));

        Assert.Throws<ArgumentException>(() => new TrafficFeedEndpoint(
            "proxy",
            TrafficFeedKind.Flow,
            url,
            TrafficFeedCredentialMode.None,
            ConfigureRequestAsync: static (_, _) => ValueTask.CompletedTask));
    }

    [Theory]
    [InlineData(TrafficFeedCredentialMode.QueryParameter)]
    [InlineData(TrafficFeedCredentialMode.Header)]
    public async Task CredentialModeWithoutCredential_FailsClosedWithoutSending(
        TrafficFeedCredentialMode credentialMode)
    {
        var handler = new RecordingHttpMessageHandler();
        using var httpClient = new HttpClient(handler);
        var endpoint = new TrafficFeedEndpoint(
            "secured-provider",
            TrafficFeedKind.Flow,
            new Uri("https://traffic.example.test/secured", UriKind.Absolute),
            credentialMode,
            ApiKeyParameterName: credentialMode == TrafficFeedCredentialMode.QueryParameter ? "key" : null,
            ApiKeyHeaderName: credentialMode == TrafficFeedCredentialMode.Header ? "X-Api-Key" : null);
        var client = CreateClient(httpClient, [endpoint]);

        TrafficFeedFetchResult result = await client.FetchAsync(new TrafficDataRequest(), TestContext.Current.CancellationToken);

        Assert.Empty(result.Payloads);
        Assert.Empty(handler.Requests);
        Assert.Equal("TrafficCredentialUnavailable", Assert.Single(result.Diagnostics).Code);
    }

    private static ConfiguredTrafficFeedClient CreateClient(
        HttpClient httpClient,
        IReadOnlyList<TrafficFeedEndpoint> endpoints,
        ITrafficProviderCredentialProvider? credentialProvider = null)
    {
        string providerId = Assert.Single(
            endpoints.Select(static endpoint => endpoint.ProviderId).Distinct(StringComparer.OrdinalIgnoreCase));
        return new ConfiguredTrafficFeedClient(providerId, httpClient, endpoints, credentialProvider);
    }

    private static TrafficFeedEndpoint Endpoint(TrafficFeedKind kind, string url)
        => new("test-provider", kind, new Uri(url, UriKind.Absolute), TrafficFeedCredentialMode.None);

    private sealed class RecordingCredentialProvider(string apiKey) : ITrafficProviderCredentialProvider
    {
        public int CallCount { get; private set; }

        public ValueTask<string?> GetApiKeyAsync(
            string providerId,
            TrafficFeedKind feedKind,
            Uri feedUrl,
            CancellationToken cancellationToken)
        {
            CallCount++;
            return ValueTask.FromResult<string?>(apiKey);
        }
    }

    private sealed class RecordingHttpMessageHandler : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent([1, 2, 3]),
            });
        }
    }
}
