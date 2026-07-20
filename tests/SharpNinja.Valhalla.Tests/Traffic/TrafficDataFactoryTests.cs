using SharpNinja.Valhalla.Traffic;
using SharpNinja.Valhalla.Traffic.Providers;
using SharpNinja.Valhalla.Traffic.Routing;
using SharpNinja.Valhalla.Traffic.Tiles;

namespace SharpNinja.Valhalla.Tests.Traffic;

public sealed class TrafficDataFactoryTests
{
    private static readonly DateTimeOffset EvaluationTime =
        new(2026, 7, 18, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task CreateSnapshot_ReturnsCompleteNormalizedSnapshot()
    {
        NormalizedTrafficEvent trafficEvent = Event(
            "flow-1",
            "tomtom",
            NormalizedTrafficEventKind.Flow,
            delaySeconds: 45);
        var matcher = new StubEdgeMatcher(Edge(trafficEvent, closed: false));
        TrafficDataFactory factory = Factory(
            [Source("tomtom", TrafficSourceKind.DirectProvider, TrafficFeedKind.Flow)],
            [new StubAdapter("tomtom", trafficEvent)],
            edgeMatcher: matcher);

        NormalizedTrafficSnapshot snapshot = await factory.CreateSnapshotAsync(
            new TrafficDataRequest(),
            TestContext.Current.CancellationToken);

        Assert.Equal(EvaluationTime, snapshot.CreatedAtUtc);
        NormalizedTrafficEvent publishedEvent = Assert.Single(snapshot.Events);
        Assert.NotSame(trafficEvent, publishedEvent);
        Assert.Equal(trafficEvent.Id, publishedEvent.Id);
        Assert.Equal(trafficEvent.ProviderId, publishedEvent.ProviderId);
        Assert.Single(snapshot.RouteModifierImpacts);
        Assert.Single(snapshot.RouteModifierSources);
        Assert.Single(snapshot.ValhallaEdgeUpdates);
        Assert.Null(snapshot.ValhallaWriteResult);
        TrafficFeedSourceStatus status = Assert.Single(snapshot.SourceStatuses);
        Assert.Equal(TrafficSourceKind.DirectProvider, status.ConfiguredSource);
        Assert.Equal(TrafficSourceKind.DirectProvider, status.EffectiveSource);
        Assert.Equal(1, status.PayloadCount);
        Assert.Equal(1, status.EventCount);
        Assert.Empty(snapshot.Diagnostics);
    }

    [Fact]
    public async Task CreateSnapshot_ClosureBeatsFlowForSameMatchedEdge()
    {
        NormalizedTrafficEvent flow = Event(
            "flow",
            "high-priority",
            NormalizedTrafficEventKind.Flow,
            confidence: 1);
        NormalizedTrafficEvent closure = Event(
            "closure",
            "low-priority",
            NormalizedTrafficEventKind.Closure,
            roadClosure: true,
            confidence: 0.1);
        var matcher = new StubEdgeMatcher(
            Edge(flow, closed: false),
            Edge(closure, closed: true));
        TrafficDataFactory factory = Factory(
            [
                Source("high-priority", TrafficSourceKind.Proxy, TrafficFeedKind.Flow),
                Source("low-priority", TrafficSourceKind.DirectProvider, TrafficFeedKind.Closure),
            ],
            [
                new StubAdapter("high-priority", flow),
                new StubAdapter("low-priority", closure),
            ],
            providerPriority: ["high-priority", "low-priority"],
            edgeMatcher: matcher);

        NormalizedTrafficSnapshot snapshot = await factory.CreateSnapshotAsync(
            new TrafficDataRequest(),
            TestContext.Current.CancellationToken);

        Assert.Equal("closure", Assert.Single(snapshot.Events).Id);
        Assert.True(Assert.Single(snapshot.ValhallaEdgeUpdates).Closed);
        Assert.True(Assert.Single(snapshot.RouteModifierImpacts).HardDeny);
    }

    [Fact]
    public void CreateSnapshot_ConflictingEvents_UsesDeterministicPrecedence()
    {
        DateTimeOffset older = EvaluationTime.AddMinutes(-10);
        DateTimeOffset newer = EvaluationTime.AddMinutes(-1);
        var resolver = new TrafficConflictResolver(["preferred", "secondary"]);

        TrafficConflictResolutionResult confidenceResult = resolver.Resolve(
        [
            Candidate(Event("lower", "preferred", NormalizedTrafficEventKind.Flow, confidence: 0.7), 1),
            Candidate(Event("higher", "secondary", NormalizedTrafficEventKind.Flow, confidence: 0.9), 1),
        ]);
        Assert.Equal("higher", Assert.Single(confidenceResult.Entries).Event.Id);

        TrafficConflictResolutionResult freshnessResult = resolver.Resolve(
        [
            Candidate(Event(
                "older",
                "preferred",
                NormalizedTrafficEventKind.Flow,
                confidence: 0.9,
                observedAtUtc: older,
                updatedAtUtc: older), 2),
            Candidate(Event(
                "newer",
                "secondary",
                NormalizedTrafficEventKind.Flow,
                confidence: 0.9,
                observedAtUtc: newer,
                updatedAtUtc: newer), 2),
        ]);
        Assert.Equal("newer", Assert.Single(freshnessResult.Entries).Event.Id);

        TrafficConflictResolutionResult priorityResult = resolver.Resolve(
        [
            Candidate(Event(
                "secondary",
                "secondary",
                NormalizedTrafficEventKind.Flow,
                confidence: 0.9,
                observedAtUtc: newer,
                updatedAtUtc: newer), 3),
            Candidate(Event(
                "preferred",
                "preferred",
                NormalizedTrafficEventKind.Flow,
                confidence: 0.9,
                observedAtUtc: newer,
                updatedAtUtc: newer), 3),
        ]);
        Assert.Equal("preferred", Assert.Single(priorityResult.Entries).Event.Id);
        Assert.Equal(["preferred", "secondary"], resolver.ProviderPriority);
    }

    [Fact]
    public void Resolve_SameCanonicalEdgeWithAlternateStorageCoordinates_UsesSingleWinner()
    {
        NormalizedTrafficEvent preferred = Event(
            "preferred-canonical",
            "preferred",
            NormalizedTrafficEventKind.Flow,
            confidence: 0.9);
        NormalizedTrafficEvent secondary = Event(
            "secondary-canonical",
            "secondary",
            NormalizedTrafficEventKind.Flow,
            confidence: 0.8);
        var resolver = new TrafficConflictResolver(["preferred", "secondary"]);

        TrafficConflictResolutionResult result = resolver.Resolve(
        [
            new TrafficConflictCandidate(
                preferred,
                [Edge(preferred, closed: false, edgeIndex: 1, tileId: 42, graphDirectedEdgeId: 9_001)]),
            new TrafficConflictCandidate(
                secondary,
                [Edge(secondary, closed: false, edgeIndex: 87, tileId: 99, graphDirectedEdgeId: 9_001)]),
        ]);

        TrafficConflictResolutionEntry winner = Assert.Single(result.Entries);
        Assert.Equal("preferred-canonical", winner.Event.Id);
        ValhallaTrafficEdgeUpdate edge = Assert.Single(winner.EdgeUpdates);
        Assert.Equal(42UL, edge.TileId);
        Assert.Equal(1U, edge.DirectedEdgeIndex);
        Assert.Equal(9_001UL, edge.CanonicalDirectedEdgeId);
    }

    [Fact]
    public async Task CreateSnapshot_OverlappingConflictComponent_SelectsSingleWinnerWithoutDelayDuplication()
    {
        NormalizedTrafficEvent primary = Event(
            "primary",
            "preferred",
            NormalizedTrafficEventKind.Flow,
            delaySeconds: 100,
            confidence: 0.9);
        NormalizedTrafficEvent secondary = Event(
            "secondary",
            "secondary",
            NormalizedTrafficEventKind.Flow,
            delaySeconds: 100,
            confidence: 0.8);
        var matcher = new StubEdgeMatcher(
            Edge(primary, closed: false, edgeIndex: 1),
            Edge(primary, closed: false, edgeIndex: 2),
            Edge(secondary, closed: false, edgeIndex: 2),
            Edge(secondary, closed: false, edgeIndex: 3));
        TrafficDataFactory factory = Factory(
            [
                Source("preferred", TrafficSourceKind.DirectProvider, TrafficFeedKind.Flow),
                Source("secondary", TrafficSourceKind.DirectProvider, TrafficFeedKind.Flow),
            ],
            [
                new StubAdapter("preferred", primary),
                new StubAdapter("secondary", secondary),
            ],
            providerPriority: ["preferred", "secondary"],
            edgeMatcher: matcher);

        NormalizedTrafficSnapshot snapshot = await factory.CreateSnapshotAsync(
            new TrafficDataRequest(),
            TestContext.Current.CancellationToken);

        TrafficRouteModifierSource component = Assert.Single(snapshot.RouteModifierSources);
        Assert.Equal(["primary", "secondary"], component.SourceEventIds);
        Assert.Equal(["preferred", "secondary"], component.ProviderIds);
        Assert.Equal(100, component.DelaySeconds);
        Assert.Equal(100, snapshot.RouteModifierSources.Sum(source => source.DelaySeconds ?? 0));
        Assert.Equal(["primary", "secondary"], snapshot.Events.Select(item => item.Id));
        Assert.Equal(
            [1u, 2u, 3u],
            snapshot.ValhallaEdgeUpdates
                .Select(update => update.DirectedEdgeIndex)
                .Order());
    }

    [Fact]
    public async Task CreateSnapshot_CanonicalOverlapAcrossAlternateStorageCoordinates_AggregatesDelayOnce()
    {
        NormalizedTrafficEvent primary = Event(
            "primary-canonical",
            "preferred",
            NormalizedTrafficEventKind.Flow,
            delaySeconds: 100,
            confidence: 0.9);
        NormalizedTrafficEvent secondary = Event(
            "secondary-canonical",
            "secondary",
            NormalizedTrafficEventKind.Flow,
            delaySeconds: 100,
            confidence: 0.8);
        var matcher = new StubEdgeMatcher(
            Edge(primary, closed: false, edgeIndex: 1, tileId: 42, graphDirectedEdgeId: 9_001),
            Edge(primary, closed: false, edgeIndex: 2, tileId: 42, graphDirectedEdgeId: 9_002),
            Edge(secondary, closed: false, edgeIndex: 88, tileId: 99, graphDirectedEdgeId: 9_002),
            Edge(secondary, closed: false, edgeIndex: 89, tileId: 99, graphDirectedEdgeId: 9_003));
        TrafficDataFactory factory = Factory(
            [
                Source("preferred", TrafficSourceKind.DirectProvider, TrafficFeedKind.Flow),
                Source("secondary", TrafficSourceKind.DirectProvider, TrafficFeedKind.Flow),
            ],
            [
                new StubAdapter("preferred", primary),
                new StubAdapter("secondary", secondary),
            ],
            providerPriority: ["preferred", "secondary"],
            edgeMatcher: matcher);

        NormalizedTrafficSnapshot snapshot = await factory.CreateSnapshotAsync(
            new TrafficDataRequest(),
            TestContext.Current.CancellationToken);

        TrafficRouteModifierSource component = Assert.Single(snapshot.RouteModifierSources);
        Assert.Equal(["primary-canonical", "secondary-canonical"], component.SourceEventIds);
        Assert.Equal(100, component.DelaySeconds);
        Assert.Equal(
            [9_001UL, 9_002UL, 9_003UL],
            snapshot.ValhallaEdgeUpdates
                .Select(static update => update.CanonicalDirectedEdgeId)
                .Order());
        Assert.DoesNotContain(
            snapshot.ValhallaEdgeUpdates,
            static update => update.TileId == 99 && update.DirectedEdgeIndex == 88);
        Assert.Contains(
            snapshot.ValhallaEdgeUpdates,
            static update => update.TileId == 99 && update.DirectedEdgeIndex == 89);
    }

    [Fact]
    public async Task CreateSnapshot_WithFutureProviderAdapter_DoesNotRequireFactoryChanges()
    {
        NormalizedTrafficEvent future = Event(
            "future-flow",
            "future-provider",
            NormalizedTrafficEventKind.Flow);
        TrafficDataFactory factory = Factory(
            [Source("future-provider", TrafficSourceKind.DirectProvider, TrafficFeedKind.Flow)],
            [new StubAdapter("future-provider", future)]);

        NormalizedTrafficSnapshot snapshot = await factory.CreateSnapshotAsync(
            new TrafficDataRequest(),
            TestContext.Current.CancellationToken);

        Assert.Equal("future-provider", Assert.Single(snapshot.Events).ProviderId);
        TrafficFeedSourceStatus status = Assert.Single(snapshot.SourceStatuses);
        Assert.Equal("future-provider", status.ProviderId);
        Assert.Equal(TrafficSourceKind.DirectProvider, status.ConfiguredSource);
    }

    [Fact]
    public async Task CreateSnapshot_WithDefaultRegistry_NormalizesTomTomAndHereWithoutHostProviderConstruction()
    {
        RawTrafficFeedPayload tomTomPayload = TrafficNormalizationFixture.Load(
            "tomtom",
            TrafficFeedKind.Flow,
            "TomTom",
            "flow.json",
            EvaluationTime);
        RawTrafficFeedPayload herePayload = TrafficNormalizationFixture.Load(
            "here",
            TrafficFeedKind.Flow,
            "Here",
            "flow.json",
            EvaluationTime);
        TrafficDataFactory factory = new(
            [
                new TrafficDataSourceRegistration(
                    new StubFeedClient(
                        "tomtom",
                        new TrafficFeedFetchResult([tomTomPayload], [])),
                    TrafficSourceKind.DirectProvider,
                    [TrafficFeedKind.Flow]),
                new TrafficDataSourceRegistration(
                    new StubFeedClient(
                        "here",
                        new TrafficFeedFetchResult([herePayload], [])),
                    TrafficSourceKind.DirectProvider,
                    [TrafficFeedKind.Flow]),
            ],
            TrafficFeedAdapterRegistry.CreateDefault(),
            new TrafficConflictResolver([]),
            new TrafficDataFactoryOptions
            {
                TrafficPolicy = TrafficPolicy.Enabled,
                TimeProvider = new StaticTimeProvider(EvaluationTime),
            });

        NormalizedTrafficSnapshot snapshot = await factory.CreateSnapshotAsync(
            new TrafficDataRequest(),
            TestContext.Current.CancellationToken);

        Assert.Contains(snapshot.Events, static trafficEvent =>
            string.Equals(trafficEvent.ProviderId, "tomtom", StringComparison.Ordinal));
        Assert.Contains(snapshot.Events, static trafficEvent =>
            string.Equals(trafficEvent.ProviderId, "here", StringComparison.Ordinal));
        Assert.DoesNotContain(
            snapshot.Diagnostics,
            static diagnostic => diagnostic.Code == "TrafficAdapterNotRegistered");
    }

    [Fact]
    public async Task CreateSnapshot_WithMalformedPayload_ReturnsDiagnostic()
    {
        TrafficDataFactory factory = Factory(
            [Source("tomtom", TrafficSourceKind.DirectProvider, TrafficFeedKind.Flow)],
            [new ThrowingAdapter("tomtom", "api-key-secret")]);

        NormalizedTrafficSnapshot snapshot = await factory.CreateSnapshotAsync(
            new TrafficDataRequest(),
            TestContext.Current.CancellationToken);

        Assert.Empty(snapshot.Events);
        TrafficProviderDiagnostic diagnostic = Assert.Single(snapshot.Diagnostics);
        Assert.Equal("TrafficPayloadNormalizationFailed", diagnostic.Code);
        Assert.DoesNotContain("api-key-secret", diagnostic.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateSnapshot_SanitizesSecretBearingAdapterDiagnosticMessages()
    {
        const string secretMessage =
            "GET https://user-secret:password-secret@traffic.example.test/path-secret" +
            "?apiKey=query-secret; Authorization: Bearer bearer-secret; " +
            "X-Api-Key: header-secret; Cookie: session=cookie-secret; " +
            "Set-Cookie: session=response-cookie-secret; #fragment-secret";
        var diagnostic = new TrafficProviderDiagnostic(
            "BearerSecretABC123",
            "ApiKeySecretABC123",
            TrafficFeedKind.Flow,
            secretMessage,
            "https://user-secret:password-secret@traffic.example.test/path-secret" +
            "?apiKey=query-secret#fragment-secret");
        TrafficDataFactory factory = Factory(
            [
                Source(
                    "future-provider",
                    TrafficSourceKind.DirectProvider,
                    TrafficFeedKind.Flow),
            ],
            [new DiagnosticAdapter("future-provider", diagnostic)]);

        NormalizedTrafficSnapshot snapshot = await factory.CreateSnapshotAsync(
            new TrafficDataRequest(),
            TestContext.Current.CancellationToken);

        TrafficProviderDiagnostic published = Assert.Single(snapshot.Diagnostics);
        Assert.Equal("TrafficProviderDiagnostic", published.Code);
        Assert.Equal("future-provider", published.ProviderId);
        Assert.Equal(
            "Traffic provider reported diagnostic 'TrafficProviderDiagnostic'.",
            published.Message);
        Assert.Equal(
            "https://traffic.example.test/redacted-path",
            published.RedactedSourceUrl);
        string serialized = published.ToString();
        Assert.DoesNotContain("query-secret", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("bearer-secret", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("header-secret", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("cookie-secret", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("response-cookie-secret", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("user-secret", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("password-secret", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("path-secret", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("fragment-secret", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("BearerSecretABC123", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("ApiKeySecretABC123", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("apiKey", serialized, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CreateSnapshot_SanitizesUntrustedRawPayloadBeforeAdapter()
    {
        const string secret = "future-client-secret";
        var rawPayload = new RawTrafficFeedPayload(
            "future-provider",
            TrafficFeedKind.Flow,
            "application/json; secret=content-secret",
            new byte[] { 1 },
            EvaluationTime,
            new Uri(
                $"https://user-{secret}:password-{secret}@traffic.example.test/raw-{secret}" +
                $"?apiKey=query-{secret}#fragment-{secret}",
                UriKind.Absolute),
            new Dictionary<string, string>
            {
                ["speedUnit"] = "MPH",
                ["apiKey"] = $"metadata-{secret}",
                ["Authorization"] = $"Bearer bearer-{secret}",
                ["Cookie"] = $"session=cookie-{secret}",
                ["custom"] = $"opaque-{secret}",
            });
        var adapter = new CapturingAdapter("future-provider");
        TrafficDataFactory factory = Factory(
            [
                new TrafficDataSourceRegistration(
                    new StubFeedClient(
                        "future-provider",
                        new TrafficFeedFetchResult([rawPayload], [])),
                    TrafficSourceKind.DirectProvider,
                    [TrafficFeedKind.Flow]),
            ],
            [adapter]);

        NormalizedTrafficSnapshot snapshot = await factory.CreateSnapshotAsync(
            new TrafficDataRequest(),
            TestContext.Current.CancellationToken);

        Assert.Empty(snapshot.Events);
        RawTrafficFeedPayload observed =
            Assert.IsType<RawTrafficFeedPayload>(adapter.ObservedPayload);
        Assert.Equal("future-provider", observed.ProviderId);
        Assert.Equal("application/json", observed.ContentType);
        Assert.Equal(
            "https://traffic.example.test/redacted-path",
            observed.SourceUri!.OriginalString);
        Assert.Equal("mph", Assert.Single(observed.ProviderMetadata).Value);
        string published = $"{observed.SourceUri}|{string.Join("|", observed.ProviderMetadata)}";
        Assert.DoesNotContain(secret, published, StringComparison.Ordinal);
        Assert.DoesNotContain("apiKey", published, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Authorization", published, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Cookie", published, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CreateSnapshot_WithExpiredClosure_ExcludesEventAndReturnsDiagnostic()
    {
        NormalizedTrafficEvent expired = Event(
            "expired",
            "here",
            NormalizedTrafficEventKind.Closure,
            roadClosure: true,
            validUntilUtc: EvaluationTime.AddSeconds(-1));
        TrafficDataFactory factory = Factory(
            [Source("here", TrafficSourceKind.DirectProvider, TrafficFeedKind.Closure)],
            [new StubAdapter("here", expired)]);

        NormalizedTrafficSnapshot snapshot = await factory.CreateSnapshotAsync(
            new TrafficDataRequest(),
            TestContext.Current.CancellationToken);

        Assert.Empty(snapshot.Events);
        Assert.Contains(snapshot.Diagnostics, diagnostic => diagnostic.Code == "TrafficEventExpired");
    }

    [Fact]
    public async Task CreateSnapshot_PreservesObservedUpdatedAndFetchedProvenance()
    {
        DateTimeOffset observed = EvaluationTime.AddMinutes(-5);
        DateTimeOffset updated = EvaluationTime.AddMinutes(-2);
        DateTimeOffset fetched = EvaluationTime.AddMinutes(-1);
        NormalizedTrafficEvent sourceEvent = Event(
            "provenance",
            "tomtom",
            NormalizedTrafficEventKind.Incident,
            observedAtUtc: observed,
            updatedAtUtc: updated,
            fetchedAtUtc: fetched,
            sourceUri: new Uri(
                "https://user-secret:password-secret@traffic.example.test/path-secret" +
                "?apiKey=query-secret#fragment-secret",
                UriKind.Absolute),
            providerReferences: new Dictionary<string, string>
            {
                ["frc"] = "FRC2",
                ["custom"] = "metadata-secret",
                ["apiKey"] = "reference-secret",
            });
        TrafficDataFactory factory = Factory(
            [Source("tomtom", TrafficSourceKind.DirectProvider, TrafficFeedKind.Incident)],
            [new StubAdapter("tomtom", sourceEvent)]);

        NormalizedTrafficEvent result = Assert.Single(
            (await factory.CreateSnapshotAsync(
                new TrafficDataRequest(),
                TestContext.Current.CancellationToken)).Events);

        Assert.NotSame(sourceEvent, result);
        Assert.Equal(observed, result.ObservedAtUtc);
        Assert.Equal(updated, result.UpdatedAtUtc);
        Assert.Equal(fetched, result.FetchedAtUtc);
        Assert.Equal("traffic.example.test", result.SourceUri!.Host);
        Assert.Equal("/redacted-path", result.SourceUri.AbsolutePath);
        Assert.Empty(result.SourceUri.Query);
        Assert.Empty(result.SourceUri.UserInfo);
        string published = $"{result.SourceUri}|{string.Join("|", result.ProviderReferences)}";
        Assert.DoesNotContain("user-secret", published, StringComparison.Ordinal);
        Assert.DoesNotContain("password-secret", published, StringComparison.Ordinal);
        Assert.DoesNotContain("path-secret", published, StringComparison.Ordinal);
        Assert.DoesNotContain("query-secret", published, StringComparison.Ordinal);
        Assert.DoesNotContain("fragment-secret", published, StringComparison.Ordinal);
        Assert.DoesNotContain("metadata-secret", published, StringComparison.Ordinal);
        Assert.DoesNotContain("reference-secret", published, StringComparison.Ordinal);
        Assert.Equal("frc", Assert.Single(result.ProviderReferences).Key);
    }

    [Fact]
    public async Task CreateSnapshot_ReportsPerFeedSourceStatusWhenEmptyOrUnavailable()
    {
        var availableClient = new StubFeedClient(
            "tomtom",
            new TrafficFeedFetchResult(
                [Payload("tomtom", TrafficFeedKind.Flow, [])],
                []));
        var unavailableClient = new StubFeedClient(
            "proxy",
            new TrafficFeedFetchResult(
                [],
                [Diagnostic("TrafficHttpFailure", "proxy", TrafficFeedKind.Incident)]));
        TrafficDataFactory factory = Factory(
            [
                new TrafficDataSourceRegistration(
                    availableClient,
                    TrafficSourceKind.DirectProvider,
                    [TrafficFeedKind.Flow]),
                new TrafficDataSourceRegistration(
                    unavailableClient,
                    TrafficSourceKind.Proxy,
                    [TrafficFeedKind.Incident]),
            ],
            [
                new StubAdapter("tomtom"),
                new StubAdapter("proxy"),
            ]);

        NormalizedTrafficSnapshot snapshot = await factory.CreateSnapshotAsync(
            new TrafficDataRequest(),
            TestContext.Current.CancellationToken);

        TrafficFeedSourceStatus empty = Assert.Single(
            snapshot.SourceStatuses,
            status => status.ProviderId == "tomtom");
        Assert.Equal(TrafficSourceKind.DirectProvider, empty.EffectiveSource);
        Assert.Equal(1, empty.PayloadCount);
        Assert.Equal(0, empty.EventCount);

        TrafficFeedSourceStatus unavailable = Assert.Single(
            snapshot.SourceStatuses,
            status => status.ProviderId == "proxy");
        Assert.Equal(TrafficSourceKind.Proxy, unavailable.ConfiguredSource);
        Assert.Equal(TrafficSourceKind.Unavailable, unavailable.EffectiveSource);
        Assert.Equal(["TrafficHttpFailure"], unavailable.DiagnosticCodes);
    }

    [Fact]
    public async Task CreateSnapshot_WithCompositePayload_NormalizesAllSupportedKinds()
    {
        NormalizedTrafficEvent[] events =
        [
            Event("flow", "here", NormalizedTrafficEventKind.Flow),
            Event("incident", "here", NormalizedTrafficEventKind.Incident),
            Event("closure", "here", NormalizedTrafficEventKind.Closure, roadClosure: true),
            Event("restriction", "here", NormalizedTrafficEventKind.Restriction),
        ];
        TrafficDataFactory factory = Factory(
            [Source("here", TrafficSourceKind.DirectProvider, TrafficFeedKind.Composite)],
            [new StubAdapter("here", events)]);

        NormalizedTrafficSnapshot snapshot = await factory.CreateSnapshotAsync(
            new TrafficDataRequest(),
            TestContext.Current.CancellationToken);

        Assert.Equal(
            Enum.GetValues<NormalizedTrafficEventKind>().Order(),
            snapshot.Events.Select(item => item.Kind).Order());
    }

    [Fact]
    public async Task CreateSnapshot_DefensivelyCopiesPublishedCollections()
    {
        NormalizedTrafficEvent trafficEvent = Event(
            "flow",
            "fixture",
            NormalizedTrafficEventKind.Flow);
        var adapterEvents = new List<NormalizedTrafficEvent> { trafficEvent };
        var adapterDiagnostics = new List<TrafficProviderDiagnostic>();
        var adapter = new MutableAdapter("fixture", adapterEvents, adapterDiagnostics);
        TrafficDataFactory factory = Factory(
            [Source("fixture", TrafficSourceKind.Fixture, TrafficFeedKind.Flow)],
            [adapter]);

        NormalizedTrafficSnapshot snapshot = await factory.CreateSnapshotAsync(
            new TrafficDataRequest(),
            TestContext.Current.CancellationToken);
        adapterEvents.Clear();
        adapterDiagnostics.Add(Diagnostic("late", "fixture", TrafficFeedKind.Flow));

        Assert.Single(snapshot.Events);
        Assert.Empty(snapshot.Diagnostics);
        Assert.Throws<NotSupportedException>(
            () => Assert.IsAssignableFrom<IList<NormalizedTrafficEvent>>(snapshot.Events).Clear());
        Assert.Throws<NotSupportedException>(
            () => Assert.IsAssignableFrom<IList<TrafficProviderDiagnostic>>(snapshot.Diagnostics)
                .Add(Diagnostic("mutation", "fixture", TrafficFeedKind.Flow)));
        Assert.Throws<NotSupportedException>(
            () => Assert.IsAssignableFrom<IList<TrafficFeedSourceStatus>>(snapshot.SourceStatuses).Clear());
    }

    [Fact]
    public async Task TileOutputWithoutWriter_ReturnsDiagnosticAndEdgeUpdates()
    {
        NormalizedTrafficEvent flow = Event("flow", "tomtom", NormalizedTrafficEventKind.Flow);
        TrafficDataFactory factory = Factory(
            [Source("tomtom", TrafficSourceKind.DirectProvider, TrafficFeedKind.Flow)],
            [new StubAdapter("tomtom", flow)],
            edgeMatcher: new StubEdgeMatcher(Edge(flow, closed: false)),
            writeTrafficTiles: true);

        NormalizedTrafficSnapshot snapshot = await factory.CreateSnapshotAsync(
            new TrafficDataRequest(),
            TestContext.Current.CancellationToken);

        Assert.Single(snapshot.ValhallaEdgeUpdates);
        Assert.Null(snapshot.ValhallaWriteResult);
        Assert.Contains(
            snapshot.Diagnostics,
            diagnostic => diagnostic.Code == "ValhallaTileWriterNotConfigured");
    }

    [Fact]
    public async Task TileOutputWithWriter_WritesExactEdgeUpdates()
    {
        NormalizedTrafficEvent flow = Event("flow", "tomtom", NormalizedTrafficEventKind.Flow);
        ValhallaTrafficEdgeUpdate expected = Edge(flow, closed: false);
        var writer = new RecordingTileWriter();
        TrafficDataFactory factory = Factory(
            [Source("tomtom", TrafficSourceKind.DirectProvider, TrafficFeedKind.Flow)],
            [new StubAdapter("tomtom", flow)],
            edgeMatcher: new StubEdgeMatcher(expected),
            writeTrafficTiles: true,
            tileWriter: writer);

        NormalizedTrafficSnapshot snapshot = await factory.CreateSnapshotAsync(
            new TrafficDataRequest(),
            TestContext.Current.CancellationToken);

        Assert.Equal([expected], Assert.Single(writer.Calls));
        Assert.Equal(1, snapshot.ValhallaWriteResult!.UpdateCount);
        Assert.True(snapshot.ValhallaWriteResult.Succeeded);
    }

    [Fact]
    public async Task TileOutputWithDisabledPolicy_WritesOnlyDirectionSafeClosureConstraints()
    {
        NormalizedTrafficEvent flow = Event(
            "flow",
            "tomtom",
            NormalizedTrafficEventKind.Flow,
            delaySeconds: 90);
        NormalizedTrafficEvent closure = Event(
            "closure",
            "tomtom",
            NormalizedTrafficEventKind.Closure,
            delaySeconds: 30,
            roadClosure: true);
        var writer = new RecordingTileWriter();
        TrafficDataFactory factory = Factory(
            [
                Source("tomtom", TrafficSourceKind.DirectProvider, TrafficFeedKind.Composite),
            ],
            [new StubAdapter("tomtom", flow, closure)],
            edgeMatcher: new StubEdgeMatcher(
                Edge(flow, closed: false, edgeIndex: 1),
                Edge(closure, closed: true, edgeIndex: 2)),
            trafficPolicy: TrafficPolicy.Disabled,
            writeTrafficTiles: true,
            tileWriter: writer);

        NormalizedTrafficSnapshot snapshot = await factory.CreateSnapshotAsync(
            new TrafficDataRequest(),
            TestContext.Current.CancellationToken);

        ValhallaTrafficEdgeUpdate written = Assert.Single(Assert.Single(writer.Calls));
        Assert.True(written.Closed);
        Assert.True(written.DirectionResolved);
        Assert.Null(written.CurrentSpeedKph);
        Assert.Null(written.FreeFlowSpeedKph);
        Assert.Null(written.DelaySeconds);
        Assert.Equal(1_002UL, written.GraphDirectedEdgeId);
        Assert.Equal([written], snapshot.ValhallaEdgeUpdates);
        Assert.All(
            snapshot.RouteModifierSources,
            source => Assert.Null(source.DelaySeconds));
    }

    [Fact]
    public async Task TileOutputWithFrictionOnlyPolicy_DoesNotWriteDynamicTraffic()
    {
        NormalizedTrafficEvent flow = Event(
            "flow",
            "tomtom",
            NormalizedTrafficEventKind.Flow,
            delaySeconds: 90);
        var writer = new RecordingTileWriter();
        TrafficDataFactory factory = Factory(
            [Source("tomtom", TrafficSourceKind.DirectProvider, TrafficFeedKind.Flow)],
            [new StubAdapter("tomtom", flow)],
            edgeMatcher: new StubEdgeMatcher(Edge(flow, closed: false)),
            trafficPolicy: new TrafficPolicy(
                IncludeTrafficDelayInEta: false,
                IncludeTrafficDelayInFriction: true,
                KeepClosuresAsRouteConstraints: true),
            writeTrafficTiles: true,
            tileWriter: writer);

        NormalizedTrafficSnapshot snapshot = await factory.CreateSnapshotAsync(
            new TrafficDataRequest(),
            TestContext.Current.CancellationToken);

        Assert.Empty(Assert.Single(writer.Calls));
        Assert.Empty(snapshot.ValhallaEdgeUpdates);
        Assert.Equal(90, Assert.Single(snapshot.RouteModifierSources).DelaySeconds);
    }

    [Fact]
    public async Task TileOutputWithEnabledPolicy_WritesFullTrafficUpdates()
    {
        NormalizedTrafficEvent flow = Event(
            "flow",
            "tomtom",
            NormalizedTrafficEventKind.Flow,
            delaySeconds: 90);
        ValhallaTrafficEdgeUpdate expected = Edge(flow, closed: false);
        var writer = new RecordingTileWriter();
        TrafficDataFactory factory = Factory(
            [Source("tomtom", TrafficSourceKind.DirectProvider, TrafficFeedKind.Flow)],
            [new StubAdapter("tomtom", flow)],
            edgeMatcher: new StubEdgeMatcher(expected),
            trafficPolicy: TrafficPolicy.Enabled,
            writeTrafficTiles: true,
            tileWriter: writer);

        NormalizedTrafficSnapshot snapshot = await factory.CreateSnapshotAsync(
            new TrafficDataRequest(),
            TestContext.Current.CancellationToken);

        ValhallaTrafficEdgeUpdate written = Assert.Single(Assert.Single(writer.Calls));
        Assert.Equal(expected.CurrentSpeedKph, written.CurrentSpeedKph);
        Assert.Equal(expected.FreeFlowSpeedKph, written.FreeFlowSpeedKph);
        Assert.Equal(90, written.DelaySeconds);
        Assert.Equal([expected], snapshot.ValhallaEdgeUpdates);
    }

    [Fact]
    public async Task TileOutputWithUnsuccessfulSilentWriter_ReturnsDiagnostic()
    {
        NormalizedTrafficEvent flow = Event("flow", "tomtom", NormalizedTrafficEventKind.Flow);
        TrafficDataFactory factory = Factory(
            [Source("tomtom", TrafficSourceKind.DirectProvider, TrafficFeedKind.Flow)],
            [new StubAdapter("tomtom", flow)],
            edgeMatcher: new StubEdgeMatcher(Edge(flow, closed: false)),
            writeTrafficTiles: true,
            tileWriter: new SilentFailingTileWriter());

        NormalizedTrafficSnapshot snapshot = await factory.CreateSnapshotAsync(
            new TrafficDataRequest(),
            TestContext.Current.CancellationToken);

        Assert.False(snapshot.ValhallaWriteResult!.Succeeded);
        Assert.Contains(
            snapshot.Diagnostics,
            diagnostic => diagnostic.Code == "ValhallaTileWriteFailed");
    }

    [Fact]
    public async Task CreateSnapshotAsync_PropagatesCancellation()
    {
        var client = new BlockingFeedClient("tomtom");
        TrafficDataFactory factory = Factory(
            [
                new TrafficDataSourceRegistration(
                    client,
                    TrafficSourceKind.DirectProvider,
                    [TrafficFeedKind.Flow]),
            ],
            [new StubAdapter("tomtom")]);
        using var cancellation =
            CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

        Task<NormalizedTrafficSnapshot> operation = factory.CreateSnapshotAsync(
            new TrafficDataRequest(),
            cancellation.Token);
        await client.Started.Task.WaitAsync(
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
        Assert.True(client.ObservedCancellation);
    }

    private static TrafficDataFactory Factory(
        IReadOnlyList<TrafficDataSourceRegistration> sources,
        IReadOnlyList<ITrafficFeedAdapter> adapters,
        IReadOnlyList<string>? providerPriority = null,
        ITrafficEdgeMatcher? edgeMatcher = null,
        TrafficPolicy? trafficPolicy = null,
        bool writeTrafficTiles = false,
        IValhallaTrafficTileWriter? tileWriter = null)
        => new(
            sources,
            new TrafficFeedAdapterRegistry(adapters),
            new TrafficConflictResolver(providerPriority ?? []),
            new TrafficDataFactoryOptions
            {
                TrafficPolicy = trafficPolicy ?? TrafficPolicy.Enabled,
                TimeProvider = new StaticTimeProvider(EvaluationTime),
                EdgeMatcher = edgeMatcher,
                GraphContext = edgeMatcher is null
                    ? null
                    : new ValhallaGraphTrafficContext("test-graph"),
                WriteTrafficTiles = writeTrafficTiles,
                TileWriter = tileWriter,
                TileWriteOptions = new ValhallaTrafficWriteOptions("test-output"),
            });

    private static TrafficDataSourceRegistration Source(
        string providerId,
        TrafficSourceKind sourceKind,
        TrafficFeedKind feedKind)
        => new(
            new StubFeedClient(
                providerId,
                new TrafficFeedFetchResult([Payload(providerId, feedKind, [1])], [])),
            sourceKind,
            [feedKind]);

    private static RawTrafficFeedPayload Payload(
        string providerId,
        TrafficFeedKind feedKind,
        byte[] content)
        => new(
            providerId,
            feedKind,
            "application/json",
            content,
            EvaluationTime,
            new Uri($"https://traffic.example.test/{feedKind}", UriKind.Absolute),
            new Dictionary<string, string>());

    private static TrafficProviderDiagnostic Diagnostic(
        string code,
        string providerId,
        TrafficFeedKind feedKind)
        => new(
            code,
            providerId,
            feedKind,
            "safe diagnostic",
            $"https://traffic.example.test/{feedKind}");

    private static NormalizedTrafficEvent Event(
        string id,
        string providerId,
        NormalizedTrafficEventKind kind,
        int? delaySeconds = null,
        bool roadClosure = false,
        double confidence = 0.8,
        DateTimeOffset? observedAtUtc = null,
        DateTimeOffset? updatedAtUtc = null,
        DateTimeOffset? fetchedAtUtc = null,
        DateTimeOffset? validUntilUtc = null,
        Uri? sourceUri = null,
        IReadOnlyDictionary<string, string>? providerReferences = null)
        => new(
            id,
            providerId,
            kind,
            new TrafficGeometry(
                TrafficGeometryKind.LineString,
                [new GeoCoordinate(36.12, -86.67), new GeoCoordinate(36.13, -86.68)]),
            currentSpeedKph: roadClosure ? 0 : 40,
            freeFlowSpeedKph: 80,
            currentTravelTimeSeconds: delaySeconds is null ? 60 : 60 + delaySeconds,
            freeFlowTravelTimeSeconds: 60,
            delaySeconds,
            roadClosure,
            roadClosure ? TrafficSeverity.Closed : TrafficSeverity.Moderate,
            confidence,
            description: id,
            observedAtUtc ?? EvaluationTime.AddMinutes(-3),
            updatedAtUtc ?? EvaluationTime.AddMinutes(-2),
            fetchedAtUtc ?? EvaluationTime.AddMinutes(-1),
            validFromUtc: EvaluationTime.AddHours(-1),
            validUntilUtc: validUntilUtc ?? EvaluationTime.AddHours(1),
            sourceUri ?? new Uri(
                $"https://traffic.example.test/{providerId}/{id}",
                UriKind.Absolute),
            providerReferences ??
                new Dictionary<string, string> { ["source-id"] = id });

    private static TrafficConflictCandidate Candidate(
        NormalizedTrafficEvent trafficEvent,
        uint edgeIndex)
        => new(trafficEvent, [Edge(trafficEvent, closed: false, edgeIndex)]);

    private static ValhallaTrafficEdgeUpdate Edge(
        NormalizedTrafficEvent trafficEvent,
        bool closed,
        uint edgeIndex = 7,
        ulong tileId = 42,
        ulong? graphDirectedEdgeId = null)
        => new(
            TileId: tileId,
            DirectedEdgeIndex: edgeIndex,
            Direction: TrafficDirection.Forward,
            CurrentSpeedKph: trafficEvent.CurrentSpeedKph,
            FreeFlowSpeedKph: trafficEvent.FreeFlowSpeedKph,
            DelaySeconds: trafficEvent.DelaySeconds,
            Closed: closed,
            HasIncident: trafficEvent.Kind == NormalizedTrafficEventKind.Incident,
            DirectionResolved: true,
            Confidence: trafficEvent.Confidence,
            SourceEventId: trafficEvent.Id,
            ProviderId: trafficEvent.ProviderId,
            GraphDirectedEdgeId: graphDirectedEdgeId ?? 1_000UL + edgeIndex);

    private sealed class StaticTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private sealed class StubFeedClient(
        string providerId,
        TrafficFeedFetchResult result) : ITrafficFeedClient
    {
        public string ProviderId { get; } = providerId;

        public Task<TrafficFeedFetchResult> FetchAsync(
            TrafficDataRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(result);
        }
    }

    private sealed class BlockingFeedClient(string providerId) : ITrafficFeedClient
    {
        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool ObservedCancellation { get; private set; }

        public string ProviderId { get; } = providerId;

        public async Task<TrafficFeedFetchResult> FetchAsync(
            TrafficDataRequest request,
            CancellationToken cancellationToken = default)
        {
            Started.SetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                throw new InvalidOperationException("Unexpected continuation.");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                ObservedCancellation = true;
                throw;
            }
        }
    }

    private class StubAdapter(
        string providerId,
        params NormalizedTrafficEvent[] events) : ITrafficFeedAdapter
    {
        public string ProviderId { get; } = providerId;

        public virtual Task<TrafficFeedNormalizationResult> NormalizeAsync(
            RawTrafficFeedPayload payload,
            TrafficNormalizationContext context,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<TrafficFeedNormalizationResult>(new(events, []));
        }
    }

    private sealed class CapturingAdapter(string providerId) : ITrafficFeedAdapter
    {
        public string ProviderId { get; } = providerId;

        public RawTrafficFeedPayload? ObservedPayload { get; private set; }

        public Task<TrafficFeedNormalizationResult> NormalizeAsync(
            RawTrafficFeedPayload payload,
            TrafficNormalizationContext context,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ObservedPayload = payload;
            return Task.FromResult<TrafficFeedNormalizationResult>(new([], []));
        }
    }

    private sealed class MutableAdapter(
        string providerId,
        IReadOnlyList<NormalizedTrafficEvent> events,
        IReadOnlyList<TrafficProviderDiagnostic> diagnostics) : ITrafficFeedAdapter
    {
        public string ProviderId { get; } = providerId;

        public Task<TrafficFeedNormalizationResult> NormalizeAsync(
            RawTrafficFeedPayload payload,
            TrafficNormalizationContext context,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new TrafficFeedNormalizationResult(events, diagnostics));
        }
    }

    private sealed class DiagnosticAdapter(
        string providerId,
        params TrafficProviderDiagnostic[] diagnostics) : ITrafficFeedAdapter
    {
        public string ProviderId { get; } = providerId;

        public Task<TrafficFeedNormalizationResult> NormalizeAsync(
            RawTrafficFeedPayload payload,
            TrafficNormalizationContext context,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<TrafficFeedNormalizationResult>(new([], diagnostics));
        }
    }

    private sealed class ThrowingAdapter(
        string providerId,
        string secret) : ITrafficFeedAdapter
    {
        public string ProviderId { get; } = providerId;

        public Task<TrafficFeedNormalizationResult> NormalizeAsync(
            RawTrafficFeedPayload payload,
            TrafficNormalizationContext context,
            CancellationToken cancellationToken = default)
            => Task.FromException<TrafficFeedNormalizationResult>(
                new InvalidDataException($"Malformed provider payload contains {secret}."));
    }

    private sealed class StubEdgeMatcher(
        params ValhallaTrafficEdgeUpdate[] updates) : ITrafficEdgeMatcher
    {
        public Task<IReadOnlyList<ValhallaTrafficEdgeUpdate>> MatchAsync(
            NormalizedTrafficEvent trafficEvent,
            ValhallaGraphTrafficContext context,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<ValhallaTrafficEdgeUpdate> matches = updates
                .Where(update => update.SourceEventId == trafficEvent.Id)
                .ToArray();
            return Task.FromResult(matches);
        }
    }

    private sealed class RecordingTileWriter : IValhallaTrafficTileWriter
    {
        public List<IReadOnlyList<ValhallaTrafficEdgeUpdate>> Calls { get; } = [];

        public Task<ValhallaTrafficWriteResult> WriteAsync(
            IReadOnlyList<ValhallaTrafficEdgeUpdate> updates,
            ValhallaTrafficWriteOptions options,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<ValhallaTrafficEdgeUpdate> captured = Array.AsReadOnly(updates.ToArray());
            Calls.Add(captured);
            return Task.FromResult(new ValhallaTrafficWriteResult(true, captured.Count, []));
        }
    }

    private sealed class SilentFailingTileWriter : IValhallaTrafficTileWriter
    {
        public Task<ValhallaTrafficWriteResult> WriteAsync(
            IReadOnlyList<ValhallaTrafficEdgeUpdate> updates,
            ValhallaTrafficWriteOptions options,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new ValhallaTrafficWriteResult(false, 0, []));
        }
    }
}
