using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;

using Microsoft.Extensions.Logging;

using SharpNinja.Valhalla.Traffic;

namespace SharpNinja.Valhalla.Tests.Traffic;

public class TrafficProviderClientTests
{
    [Fact]
    public async Task Diagnostics_RedactExactFeedUrlSecrets()
    {
        const string ApiKey = "diagnostic-query-secret";
        var handler = new StatusHttpMessageHandler(HttpStatusCode.BadGateway);
        using var httpClient = new HttpClient(handler);
        var endpoint = new TrafficFeedEndpoint(
            "tomtom",
            TrafficFeedKind.Flow,
            new Uri(
                $"https://traffic.example.test/flow?region=nashville&key={ApiKey}",
                UriKind.Absolute),
            TrafficFeedCredentialMode.None);
        var client = new ConfiguredTrafficFeedClient("tomtom", httpClient, [endpoint]);

        TrafficFeedFetchResult result = await client.FetchAsync(new TrafficDataRequest(), TestContext.Current.CancellationToken);

        TrafficProviderDiagnostic diagnostic = Assert.Single(result.Diagnostics);
        Assert.Empty(result.Payloads);
        Assert.Equal("https://traffic.example.test/redacted-path", diagnostic.RedactedSourceUrl);
        string diagnosticText = diagnostic.ToString();
        Assert.DoesNotContain(ApiKey, diagnosticText, StringComparison.Ordinal);
        Assert.DoesNotContain("region=nashville", diagnosticText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HttpFailure_DoesNotLogApiKey()
    {
        const string ApiKey = "header-api-secret";
        const string ExceptionSecret = "transport-exception-secret";
        var handler = new ThrowingHttpMessageHandler(ExceptionSecret);
        using var httpClient = new HttpClient(handler);
        var logger = new RecordingLogger<ConfiguredTrafficFeedClient>();
        var endpoint = new TrafficFeedEndpoint(
            "here",
            TrafficFeedKind.Incident,
            new Uri("https://traffic.example.test/incidents?tenant=nashville", UriKind.Absolute),
            TrafficFeedCredentialMode.Header,
            ApiKeyHeaderName: "X-Api-Key");
        var client = new ConfiguredTrafficFeedClient(
            "here",
            httpClient,
            [endpoint],
            new ConstantCredentialProvider(ApiKey),
            logger);

        TrafficFeedFetchResult result = await client.FetchAsync(new TrafficDataRequest(), TestContext.Current.CancellationToken);

        Assert.Single(result.Diagnostics);
        string logText = string.Join(Environment.NewLine, logger.Messages);
        Assert.DoesNotContain(ApiKey, logText, StringComparison.Ordinal);
        Assert.DoesNotContain(ExceptionSecret, logText, StringComparison.Ordinal);
        Assert.DoesNotContain("tenant=nashville", logText, StringComparison.Ordinal);
        Assert.Contains("https://traffic.example.test/redacted-path", logText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Diagnostics_RedactAuthorizationBearerAndQuerySecrets()
    {
        const string BearerSecret = "bearer-token-secret";
        const string QuerySecret = "query-token-secret";
        var handler = new StatusHttpMessageHandler(HttpStatusCode.Forbidden);
        using var invoker = new HttpMessageInvoker(handler);
        var logger = new RecordingLogger<ConfiguredTrafficFeedClient>();
        var endpoint = new TrafficFeedEndpoint(
            "future-provider",
            TrafficFeedKind.Composite,
            new Uri($"https://traffic.example.test/composite?access_token={QuerySecret}", UriKind.Absolute),
            TrafficFeedCredentialMode.CustomRequestMutator,
            ConfigureRequestAsync: (request, _) =>
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", BearerSecret);
                return ValueTask.CompletedTask;
            });
        var client = new ConfiguredTrafficFeedClient(
            "future-provider",
            invoker,
            [endpoint],
            logger: logger);

        TrafficFeedFetchResult result = await client.FetchAsync(new TrafficDataRequest(), TestContext.Current.CancellationToken);

        string safeText = Assert.Single(result.Diagnostics) + Environment.NewLine
            + string.Join(Environment.NewLine, logger.Messages);
        Assert.DoesNotContain(BearerSecret, safeText, StringComparison.Ordinal);
        Assert.DoesNotContain(QuerySecret, safeText, StringComparison.Ordinal);
        Assert.DoesNotContain("Authorization", safeText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("https://traffic.example.test/redacted-path", safeText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CustomRequestMutatorFailure_RedactsExceptionAndDoesNotSend()
    {
        const string MutatorSecret = "mutator-exception-secret";
        const string QuerySecret = "mutator-query-secret";
        var handler = new StatusHttpMessageHandler(HttpStatusCode.OK);
        using var invoker = new HttpMessageInvoker(handler);
        var logger = new RecordingLogger<ConfiguredTrafficFeedClient>();
        var endpoint = new TrafficFeedEndpoint(
            "future-provider",
            TrafficFeedKind.Restriction,
            new Uri($"https://traffic.example.test/restrictions?token={QuerySecret}", UriKind.Absolute),
            TrafficFeedCredentialMode.CustomRequestMutator,
            ConfigureRequestAsync: (_, _) =>
                ValueTask.FromException(new InvalidOperationException(MutatorSecret)));
        var client = new ConfiguredTrafficFeedClient(
            "future-provider",
            invoker,
            [endpoint],
            logger: logger);

        TrafficFeedFetchResult result = await client.FetchAsync(new TrafficDataRequest(), TestContext.Current.CancellationToken);

        Assert.Equal(0, handler.CallCount);
        TrafficProviderDiagnostic diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal("TrafficRequestConfigurationFailed", diagnostic.Code);
        string safeText = diagnostic + Environment.NewLine + string.Join(Environment.NewLine, logger.Messages);
        Assert.DoesNotContain(MutatorSecret, safeText, StringComparison.Ordinal);
        Assert.DoesNotContain(QuerySecret, safeText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CustomRequestMutator_CannotDowngradeCredentialTransportToHttp()
    {
        var handler = new StatusHttpMessageHandler(HttpStatusCode.OK);
        using var invoker = new HttpMessageInvoker(handler);
        var endpoint = new TrafficFeedEndpoint(
            "future-provider",
            TrafficFeedKind.Flow,
            new Uri("https://traffic.example.test/flow", UriKind.Absolute),
            TrafficFeedCredentialMode.CustomRequestMutator,
            ConfigureRequestAsync: (request, _) =>
            {
                request.RequestUri =
                    new Uri("http://traffic.example.test/flow", UriKind.Absolute);
                request.Headers.Authorization =
                    new AuthenticationHeaderValue("Bearer", "must-not-be-sent");
                return ValueTask.CompletedTask;
            });
        var client =
            new ConfiguredTrafficFeedClient("future-provider", invoker, [endpoint]);

        TrafficFeedFetchResult result = await client.FetchAsync(
            new TrafficDataRequest(),
            TestContext.Current.CancellationToken);

        Assert.Empty(result.Payloads);
        Assert.Equal(0, handler.CallCount);
        Assert.Equal(
            "TrafficCredentialTransportInsecure",
            Assert.Single(result.Diagnostics).Code);
    }

    [Fact]
    public async Task DeclaredPayloadBeyondConfiguredLimit_IsRejectedBeforeBodyRead()
    {
        const int MaxResponseContentBytes = 4;
        var content = new ByteArrayContent([1]);
        content.Headers.ContentLength = MaxResponseContentBytes + 1;
        using var httpClient = new HttpClient(new ResponseHttpMessageHandler(
            () => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = content,
            }));
        var endpoint = new TrafficFeedEndpoint(
            "tomtom",
            TrafficFeedKind.Flow,
            new Uri(
                "https://traffic.example.test/path-credential-secret/flow",
                UriKind.Absolute),
            TrafficFeedCredentialMode.None);
        var client = new ConfiguredTrafficFeedClient(
            "tomtom",
            httpClient,
            [endpoint],
            maxResponseContentBytes: MaxResponseContentBytes);

        TrafficFeedFetchResult result = await client.FetchAsync(
            new TrafficDataRequest(),
            TestContext.Current.CancellationToken);

        Assert.Empty(result.Payloads);
        TrafficProviderDiagnostic diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal("TrafficPayloadTooLarge", diagnostic.Code);
        Assert.Equal(
            "https://traffic.example.test/redacted-path",
            diagnostic.RedactedSourceUrl);
        Assert.DoesNotContain(
            "path-credential-secret",
            diagnostic.ToString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task ChunkedPayloadBeyondConfiguredLimit_IsRejectedByStreamingCap()
    {
        const int MaxResponseContentBytes = 4;
        using var httpClient = new HttpClient(new ResponseHttpMessageHandler(
            () => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(
                    new NonSeekableReadStream([1, 2, 3, 4, 5])),
            }));
        var endpoint = new TrafficFeedEndpoint(
            "here",
            TrafficFeedKind.Incident,
            new Uri("https://traffic.example.test/incidents", UriKind.Absolute),
            TrafficFeedCredentialMode.None);
        var client = new ConfiguredTrafficFeedClient(
            "here",
            httpClient,
            [endpoint],
            maxResponseContentBytes: MaxResponseContentBytes);

        TrafficFeedFetchResult result = await client.FetchAsync(
            new TrafficDataRequest(),
            TestContext.Current.CancellationToken);

        Assert.Empty(result.Payloads);
        Assert.Equal(
            "TrafficPayloadTooLarge",
            Assert.Single(result.Diagnostics).Code);
    }

    [Fact]
    public async Task ChunkedPayloadRead_ObservesCallerCancellation()
    {
        var stream = new BlockingReadStream();
        using var httpClient = new HttpClient(new ResponseHttpMessageHandler(
            () => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(stream),
            }));
        var endpoint = new TrafficFeedEndpoint(
            "here",
            TrafficFeedKind.Flow,
            new Uri("https://traffic.example.test/flow", UriKind.Absolute),
            TrafficFeedCredentialMode.None);
        var client = new ConfiguredTrafficFeedClient(
            "here",
            httpClient,
            [endpoint],
            maxResponseContentBytes: 1024);
        using var cancellation =
            CancellationTokenSource.CreateLinkedTokenSource(
                TestContext.Current.CancellationToken);

        Task<TrafficFeedFetchResult> fetch =
            client.FetchAsync(new TrafficDataRequest(), cancellation.Token);
        await stream.ReadStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fetch);
    }

    private sealed class ConstantCredentialProvider(string apiKey) : ITrafficProviderCredentialProvider
    {
        public ValueTask<string?> GetApiKeyAsync(
            string providerId,
            TrafficFeedKind feedKind,
            Uri feedUrl,
            CancellationToken cancellationToken)
            => ValueTask.FromResult<string?>(apiKey);
    }

    private sealed class StatusHttpMessageHandler(HttpStatusCode statusCode) : HttpMessageHandler
    {
        public int CallCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(new HttpResponseMessage(statusCode));
        }
    }

    private sealed class ThrowingHttpMessageHandler(string exceptionSecret) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            string authorization = request.Headers.Authorization?.ToString() ?? string.Empty;
            return Task.FromException<HttpResponseMessage>(
                new HttpRequestException(
                    $"Transport failed for {request.RequestUri}; Authorization={authorization}; marker={exceptionSecret}"));
        }
    }

    private sealed class ResponseHttpMessageHandler(
        Func<HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(responseFactory());
        }
    }

    private sealed class NonSeekableReadStream(byte[] content) : Stream
    {
        private int _offset;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => _offset;
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int available = Math.Min(count, content.Length - _offset);
            content.AsSpan(_offset, available).CopyTo(buffer.AsSpan(offset, available));
            _offset += available;
            return available;
        }

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int available = Math.Min(buffer.Length, content.Length - _offset);
            content.AsMemory(_offset, available).CopyTo(buffer);
            _offset += available;
            return ValueTask.FromResult(available);
        }

        public override long Seek(long offset, SeekOrigin origin)
            => throw new NotSupportedException();

        public override void SetLength(long value)
            => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
            => throw new NotSupportedException();
    }

    private sealed class BlockingReadStream : Stream
    {
        public TaskCompletionSource ReadStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => 0;
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
            => throw new NotSupportedException();

        public override async Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken)
        {
            ReadStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            ReadStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }

        public override long Seek(long offset, SeekOrigin origin)
            => throw new NotSupportedException();

        public override void SetLength(long value)
            => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
            => throw new NotSupportedException();
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            string message = formatter(state, exception);
            if (exception is not null)
            {
                message += Environment.NewLine + exception;
            }

            Messages.Add(message);
        }
    }
}
