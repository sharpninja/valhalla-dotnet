using System.Net;
using System.Net.Http;

using SharpNinja.Valhalla.Traffic;

namespace SharpNinja.Valhalla.Tests.Traffic;

public class ConfiguredTrafficFeedClientTests
{
    [Fact]
    public async Task HostSuppliedHttpClient_PreservesDelegatingHandlerPipeline()
    {
        var terminal = new TerminalHandler();
        var delegating = new PipelineStampingHandler
        {
            InnerHandler = terminal,
        };
        using var httpClient = new HttpClient(delegating);
        var client = new ConfiguredTrafficFeedClient(
            "proxy",
            httpClient,
            [Endpoint("https://traffic.example.test/flow")]);

        TrafficFeedFetchResult result = await client.FetchAsync(new TrafficDataRequest(), TestContext.Current.CancellationToken);

        Assert.Empty(result.Diagnostics);
        Assert.Equal(1, delegating.CallCount);
        HttpRequestMessage request = Assert.Single(terminal.Requests);
        Assert.Equal(["preserved"], request.Headers.GetValues("X-Delegating-Pipeline"));
    }

    [Fact]
    public async Task HostSuppliedHttpMessageInvoker_PreservesDelegatingHandlerPipeline()
    {
        var terminal = new TerminalHandler();
        var delegating = new PipelineStampingHandler
        {
            InnerHandler = terminal,
        };
        using var invoker = new HttpMessageInvoker(delegating);
        var client = new ConfiguredTrafficFeedClient(
            "proxy",
            invoker,
            [Endpoint("https://traffic.example.test/incidents", TrafficFeedKind.Incident)]);

        TrafficFeedFetchResult result = await client.FetchAsync(new TrafficDataRequest(), TestContext.Current.CancellationToken);

        Assert.Empty(result.Diagnostics);
        Assert.Equal(1, delegating.CallCount);
        Assert.Equal(["preserved"], Assert.Single(terminal.Requests).Headers.GetValues("X-Delegating-Pipeline"));
    }

    [Fact]
    public async Task FetchAsync_CancellationTokenCancelsRequest()
    {
        var handler = new CancellationAwareHandler();
        using var invoker = new HttpMessageInvoker(handler);
        var client = new ConfiguredTrafficFeedClient(
            "proxy",
            invoker,
            [Endpoint("https://traffic.example.test/flow")]);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

        Task<TrafficFeedFetchResult> fetch = client.FetchAsync(new TrafficDataRequest(), cancellation.Token);
        await handler.RequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fetch);
        Assert.True(handler.ObservedCancellation);
    }

    [Fact]
    public async Task FetchAsync_CancellationDuringCredentialProviderStopsBeforeSend()
    {
        var handler = new TerminalHandler();
        using var invoker = new HttpMessageInvoker(handler);
        var credentials = new CancellationAwareCredentialProvider();
        var endpoint = new TrafficFeedEndpoint(
            "secured",
            TrafficFeedKind.Flow,
            new Uri("https://traffic.example.test/flow", UriKind.Absolute),
            TrafficFeedCredentialMode.QueryParameter,
            ApiKeyParameterName: "key");
        var client = new ConfiguredTrafficFeedClient("secured", invoker, [endpoint], credentials);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

        Task<TrafficFeedFetchResult> fetch = client.FetchAsync(new TrafficDataRequest(), cancellation.Token);
        await credentials.RequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fetch);
        Assert.True(credentials.ObservedCancellation);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task FetchAsync_CancellationDuringCustomMutatorStopsBeforeSend()
    {
        var handler = new TerminalHandler();
        using var invoker = new HttpMessageInvoker(handler);
        var mutatorStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool observedCancellation = false;
        var endpoint = new TrafficFeedEndpoint(
            "custom",
            TrafficFeedKind.Flow,
            new Uri("https://traffic.example.test/flow", UriKind.Absolute),
            TrafficFeedCredentialMode.CustomRequestMutator,
            ConfigureRequestAsync: async (_, cancellationToken) =>
            {
                mutatorStarted.SetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    observedCancellation = true;
                    throw;
                }
            });
        var client = new ConfiguredTrafficFeedClient("custom", invoker, [endpoint]);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

        Task<TrafficFeedFetchResult> fetch = client.FetchAsync(new TrafficDataRequest(), cancellation.Token);
        await mutatorStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fetch);
        Assert.True(observedCancellation);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task FetchAsync_CancellationDuringContentReadPropagates()
    {
        var content = new CancellationAwareContent();
        var handler = new ContentHandler(content);
        using var invoker = new HttpMessageInvoker(handler);
        var client = new ConfiguredTrafficFeedClient(
            "proxy",
            invoker,
            [Endpoint("https://traffic.example.test/flow")]);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

        Task<TrafficFeedFetchResult> fetch = client.FetchAsync(new TrafficDataRequest(), cancellation.Token);
        await content.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fetch);
        Assert.True(content.ObservedCancellation);
    }

    [Fact]
    public void Endpoint_RejectsRelativeNonHttpUserInfoAndFragmentUrls()
    {
        Assert.Throws<ArgumentException>(() => new TrafficFeedEndpoint(
            "provider",
            TrafficFeedKind.Flow,
            new Uri("/relative", UriKind.Relative),
            TrafficFeedCredentialMode.None));

        Assert.Throws<ArgumentException>(() => new TrafficFeedEndpoint(
            "provider",
            TrafficFeedKind.Flow,
            new Uri("file:///tmp/traffic.json", UriKind.Absolute),
            TrafficFeedCredentialMode.None));

        Assert.Throws<ArgumentException>(() => new TrafficFeedEndpoint(
            "provider",
            TrafficFeedKind.Flow,
            new Uri("https://user:password@traffic.example.test/flow", UriKind.Absolute),
            TrafficFeedCredentialMode.None));

        Assert.Throws<ArgumentException>(() => new TrafficFeedEndpoint(
            "provider",
            TrafficFeedKind.Flow,
            new Uri("https://traffic.example.test/flow#secret", UriKind.Absolute),
            TrafficFeedCredentialMode.None));
    }

    [Fact]
    public void InvalidEndpointOrCredentialModeConfiguration_IsRejected()
    {
        Uri url = new("https://traffic.example.test/flow", UriKind.Absolute);

        var canonical = new TrafficFeedEndpoint(
            "  tomtom  ",
            TrafficFeedKind.Flow,
            url,
            TrafficFeedCredentialMode.None);
        Assert.Equal("tomtom", canonical.ProviderId);

        Assert.Throws<ArgumentOutOfRangeException>(() => new TrafficFeedEndpoint(
            "provider",
            (TrafficFeedKind)int.MaxValue,
            url,
            TrafficFeedCredentialMode.None));

        Assert.Throws<ArgumentException>(() => new TrafficFeedEndpoint(
            "provider",
            TrafficFeedKind.Flow,
            url,
            TrafficFeedCredentialMode.QueryParameter,
            ApiKeyParameterName: "bad&name"));

        Assert.Throws<ArgumentException>(() => new TrafficFeedEndpoint(
            "provider",
            TrafficFeedKind.Flow,
            url,
            TrafficFeedCredentialMode.Header,
            ApiKeyHeaderName: "bad header"));

        Assert.Throws<ArgumentException>(() => new TrafficFeedEndpoint(
            "provider",
            TrafficFeedKind.Flow,
            url,
            TrafficFeedCredentialMode.CustomRequestMutator));

        Assert.Throws<ArgumentOutOfRangeException>(() => new TrafficFeedEndpoint(
            "provider",
            TrafficFeedKind.Flow,
            url,
            (TrafficFeedCredentialMode)int.MaxValue));
    }

    [Fact]
    public void Constructor_RejectsProviderMismatchAndDuplicateFeedKeys()
    {
        var terminal = new TerminalHandler();
        using var invoker = new HttpMessageInvoker(terminal);

        Assert.Throws<ArgumentException>(() => new ConfiguredTrafficFeedClient(
            "different-provider",
            invoker,
            [Endpoint("https://traffic.example.test/flow")]));

        Assert.Throws<ArgumentException>(() => new ConfiguredTrafficFeedClient(
            "proxy",
            invoker,
            [
                Endpoint("https://traffic.example.test/flow-a"),
                Endpoint("https://traffic.example.test/flow-b"),
            ]));
    }

    [Fact]
    public async Task CustomRequestMutator_UsesFinalRedactedRequestUriAsProvenance()
    {
        var terminal = new TerminalHandler();
        using var invoker = new HttpMessageInvoker(terminal);
        var endpoint = new TrafficFeedEndpoint(
            "proxy",
            TrafficFeedKind.Flow,
            new Uri("https://configured.example.test/original", UriKind.Absolute),
            TrafficFeedCredentialMode.CustomRequestMutator,
            ConfigureRequestAsync: (request, _) =>
            {
                request.RequestUri = new Uri(
                    "https://mutated.example.test/nashville/flow?apiKey=credential-secret#fragment",
                    UriKind.Absolute);
                return ValueTask.CompletedTask;
            });
        var client = new ConfiguredTrafficFeedClient("proxy", invoker, [endpoint]);

        TrafficFeedFetchResult result = await client.FetchAsync(
            new TrafficDataRequest(),
            TestContext.Current.CancellationToken);

        RawTrafficFeedPayload payload = Assert.Single(result.Payloads);
        Assert.Empty(result.Diagnostics);
        Assert.Equal(
            new Uri("https://mutated.example.test/redacted-path", UriKind.Absolute),
            payload.SourceUri);
        Assert.DoesNotContain("nashville/flow", payload.SourceUri!.OriginalString, StringComparison.Ordinal);
        Assert.DoesNotContain("credential-secret", payload.SourceUri!.OriginalString, StringComparison.Ordinal);
        Assert.DoesNotContain("fragment", payload.SourceUri.OriginalString, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SuccessfulFetch_PersistsOnlyAllowlistedResponseProvenance()
    {
        using var invoker = new HttpMessageInvoker(new ProvenanceResponseHandler());
        var client = new ConfiguredTrafficFeedClient(
            "tomtom",
            invoker,
            [
                new TrafficFeedEndpoint(
                    "tomtom",
                    TrafficFeedKind.Flow,
                    new Uri("https://traffic.example.test/flow", UriKind.Absolute),
                    TrafficFeedCredentialMode.None),
            ]);

        TrafficFeedFetchResult result = await client.FetchAsync(
            new TrafficDataRequest(),
            TestContext.Current.CancellationToken);

        IReadOnlyDictionary<string, string> metadata =
            Assert.Single(result.Payloads).ProviderMetadata;
        Assert.Equal(4, metadata.Count);
        Assert.Equal("model-20260718-42", metadata["TrafficModelID"]);
        Assert.Equal("\"model-etag\"", metadata["ETag"]);
        Assert.Equal("Sat, 18 Jul 2026 11:58:00 GMT", metadata["Last-Modified"]);
        Assert.Equal("Sat, 18 Jul 2026 11:59:00 GMT", metadata["Date"]);

        string serialized = string.Join(
            "|",
            metadata.Select(pair => $"{pair.Key}={pair.Value}"));
        Assert.DoesNotContain("authorization-secret", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("api-key-secret", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("cookie-secret", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("unknown-secret", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("Authorization", metadata.Keys);
        Assert.DoesNotContain("X-Api-Key", metadata.Keys);
        Assert.DoesNotContain("Set-Cookie", metadata.Keys);
        Assert.DoesNotContain("X-Unknown", metadata.Keys);
    }

    [Fact]
    public async Task SuccessfulFetch_StoresOnlyRedactedSourceProvenance()
    {
        var terminal = new TerminalHandler();
        using var invoker = new HttpMessageInvoker(terminal);
        var endpoint = Endpoint(
            "https://traffic.example.test/incidents?region=nashville&apiKey=credential-secret",
            TrafficFeedKind.Incident);
        var client = new ConfiguredTrafficFeedClient("proxy", invoker, [endpoint]);

        TrafficFeedFetchResult result = await client.FetchAsync(new TrafficDataRequest(), TestContext.Current.CancellationToken);

        RawTrafficFeedPayload payload = Assert.Single(result.Payloads);
        Assert.Empty(result.Diagnostics);
        Assert.Equal("proxy", payload.ProviderId);
        Assert.Equal(TrafficFeedKind.Incident, payload.FeedKind);
        Assert.Equal(
            new Uri("https://traffic.example.test/redacted-path", UriKind.Absolute),
            payload.SourceUri);
        Assert.DoesNotContain("incidents", payload.SourceUri!.OriginalString, StringComparison.Ordinal);
        Assert.DoesNotContain("credential-secret", payload.SourceUri!.OriginalString, StringComparison.Ordinal);
        Assert.Equal([4, 5, 6], payload.Content.ToArray());
        Assert.Empty(payload.ProviderMetadata);
    }

    [Fact]
    public async Task InjectedTimeProvider_ControlsFetchedAtUtc()
    {
        var terminal = new TerminalHandler();
        using var invoker = new HttpMessageInvoker(terminal);
        DateTimeOffset expected = new(2026, 7, 18, 12, 34, 56, TimeSpan.Zero);
        var client = new ConfiguredTrafficFeedClient(
            "proxy",
            invoker,
            [Endpoint("https://traffic.example.test/flow")],
            timeProvider: new StaticTimeProvider(expected));

        TrafficFeedFetchResult result = await client.FetchAsync(new TrafficDataRequest(), TestContext.Current.CancellationToken);

        Assert.Equal(expected, Assert.Single(result.Payloads).FetchedAtUtc);
    }

    [Fact]
    public async Task HostSuppliedTransport_IsNotDisposed()
    {
        var handler = new DisposalTrackingHandler();
        using var invoker = new HttpMessageInvoker(handler);
        var client = new ConfiguredTrafficFeedClient(
            "proxy",
            invoker,
            [Endpoint("https://traffic.example.test/flow")]);

        await client.FetchAsync(new TrafficDataRequest(), TestContext.Current.CancellationToken);
        Assert.False(typeof(IDisposable).IsAssignableFrom(client.GetType()));

        using HttpResponseMessage response = await invoker.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, "https://traffic.example.test/after-client"),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(handler.DisposeCalled);
        Assert.Equal(2, handler.CallCount);
    }

    [Fact]
    public async Task SuccessfulFetch_PublishesReadOnlyDefensiveCollections()
    {
        var terminal = new TerminalHandler();
        using var invoker = new HttpMessageInvoker(terminal);
        var client = new ConfiguredTrafficFeedClient(
            "proxy",
            invoker,
            [Endpoint("https://traffic.example.test/flow")]);

        TrafficFeedFetchResult result = await client.FetchAsync(
            new TrafficDataRequest(),
            TestContext.Current.CancellationToken);
        RawTrafficFeedPayload payload = Assert.Single(result.Payloads);

        IList<RawTrafficFeedPayload> payloads =
            Assert.IsAssignableFrom<IList<RawTrafficFeedPayload>>(result.Payloads);
        IList<TrafficProviderDiagnostic> diagnostics =
            Assert.IsAssignableFrom<IList<TrafficProviderDiagnostic>>(result.Diagnostics);
        IDictionary<string, string> metadata =
            Assert.IsAssignableFrom<IDictionary<string, string>>(payload.ProviderMetadata);

        Assert.Throws<NotSupportedException>(() => payloads.Clear());
        Assert.Throws<NotSupportedException>(() => diagnostics.Add(new TrafficProviderDiagnostic(
            "test",
            "proxy",
            TrafficFeedKind.Flow,
            "test",
            "https://traffic.example.test/flow")));
        Assert.Throws<NotSupportedException>(() => metadata.Add("mutable", "no"));
    }

    [Fact]
    public async Task ConstructorAndFetch_DefensivelyCopyCallerOwnedEndpointsAndContent()
    {
        byte[] callerOwnedContent = [4, 5, 6];
        using var content = new ByteArrayContent(callerOwnedContent);
        using var invoker = new HttpMessageInvoker(new ContentHandler(content));
        var endpoints = new List<TrafficFeedEndpoint>
        {
            Endpoint("https://traffic.example.test/flow"),
        };
        var client = new ConfiguredTrafficFeedClient("proxy", invoker, endpoints);
        endpoints.Add(Endpoint("https://traffic.example.test/incidents", TrafficFeedKind.Incident));

        TrafficFeedFetchResult result = await client.FetchAsync(
            new TrafficDataRequest(),
            TestContext.Current.CancellationToken);
        callerOwnedContent[0] = 99;

        RawTrafficFeedPayload payload = Assert.Single(result.Payloads);
        Assert.Equal([4, 5, 6], payload.Content.ToArray());
    }

    private static TrafficFeedEndpoint Endpoint(
        string url,
        TrafficFeedKind kind = TrafficFeedKind.Flow)
        => new("proxy", kind, new Uri(url, UriKind.Absolute), TrafficFeedCredentialMode.None);

    private sealed class StaticTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private sealed class PipelineStampingHandler : DelegatingHandler
    {
        public int CallCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            request.Headers.Add("X-Delegating-Pipeline", "preserved");
            return base.SendAsync(request, cancellationToken);
        }
    }

    private sealed class ProvenanceResponseHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent([4, 5, 6]),
            };
            response.Headers.TryAddWithoutValidation("trafficmodelid", "model-20260718-42");
            response.Headers.TryAddWithoutValidation("Date", "Sat, 18 Jul 2026 11:59:00 GMT");
            response.Headers.TryAddWithoutValidation("ETag", "\"model-etag\"");
            response.Content.Headers.TryAddWithoutValidation(
                "Last-Modified",
                "Sat, 18 Jul 2026 11:58:00 GMT");
            response.Headers.TryAddWithoutValidation(
                "Authorization",
                "Bearer authorization-secret");
            response.Headers.TryAddWithoutValidation("X-Api-Key", "api-key-secret");
            response.Headers.TryAddWithoutValidation("Set-Cookie", "session=cookie-secret");
            response.Headers.TryAddWithoutValidation("X-Unknown", "unknown-secret");
            return Task.FromResult(response);
        }
    }

    private class TerminalHandler : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent([4, 5, 6]),
            });
        }
    }

    private sealed class DisposalTrackingHandler : HttpMessageHandler
    {
        public int CallCount { get; private set; }

        public bool DisposeCalled { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent([7]),
            });
        }

        protected override void Dispose(bool disposing)
        {
            DisposeCalled = true;
            base.Dispose(disposing);
        }
    }

    private sealed class CancellationAwareHandler : HttpMessageHandler
    {
        public TaskCompletionSource RequestStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool ObservedCancellation { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestStarted.SetResult();

            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                throw new InvalidOperationException("The cancellable handler unexpectedly resumed.");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                ObservedCancellation = true;
                throw;
            }
        }
    }

    private sealed class CancellationAwareCredentialProvider : ITrafficProviderCredentialProvider
    {
        public TaskCompletionSource RequestStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool ObservedCancellation { get; private set; }

        public async ValueTask<string?> GetApiKeyAsync(
            string providerId,
            TrafficFeedKind feedKind,
            Uri feedUrl,
            CancellationToken cancellationToken)
        {
            RequestStarted.SetResult();

            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return "unexpected";
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                ObservedCancellation = true;
                throw;
            }
        }
    }

    private sealed class ContentHandler(HttpContent content) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = content,
            });
    }

    private sealed class CancellationAwareContent : HttpContent
    {
        public TaskCompletionSource ReadStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool ObservedCancellation { get; private set; }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
            => SerializeToStreamAsync(stream, context, CancellationToken.None);

        protected override async Task SerializeToStreamAsync(
            Stream stream,
            TransportContext? context,
            CancellationToken cancellationToken)
        {
            ReadStarted.SetResult();

            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                ObservedCancellation = true;
                throw;
            }
        }

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }
}
