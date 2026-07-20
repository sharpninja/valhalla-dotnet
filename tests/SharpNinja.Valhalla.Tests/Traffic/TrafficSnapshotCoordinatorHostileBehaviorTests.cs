using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using SharpNinja.Valhalla;
using SharpNinja.Valhalla.Traffic;
using SharpNinja.Valhalla.Traffic.Routing;
using SharpNinja.Valhalla.Traffic.Tiles;

namespace SharpNinja.Valhalla.Tests.Traffic;

public sealed class TrafficSnapshotCoordinatorHostileBehaviorTests
{
    [Fact]
    [SuppressMessage(
        "xUnit",
        "xUnit1051",
        Justification = "A separately canceled waiter token is the behavior under test; the primary waiter remains bound to TestContext.")]
    public async Task RefreshAsync_SameRequestIsSingleFlightAndWaiterCancellationDoesNotCancelSharedFetch()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var factory = new BlockingFactory();
        var writer = new RecordingWriter();
        var coordinator = CreateCoordinator(factory, writer, now);
        var request = Request(TrafficFeedKind.Flow);

        Task<TrafficSnapshotRefreshResult> primary =
            coordinator.RefreshAsync(request, TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => factory.CallCount == 1);
        using var canceledWaiter = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        Task<TrafficSnapshotRefreshResult> canceled =
            coordinator.RefreshAsync(request, canceledWaiter.Token);
        canceledWaiter.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceled);
        Assert.Equal(1, factory.CallCount);
        Assert.False(factory.ObservedToken.CanBeCanceled);

        factory.Complete(EmptySnapshot(now));
        TrafficSnapshotRefreshResult completed = await primary;

        Assert.Empty(completed.Snapshot.Events);
        Assert.Equal(2, writer.CallCount);
        Assert.Equal(TrafficSnapshotPolicy.Enabled, writer.Calls[0].Options.Policy);
        Assert.Equal(TrafficSnapshotPolicy.ClosureOnly, writer.Calls[1].Options.Policy);
    }

    [Fact]
    public async Task RefreshAsync_DistinctFeedKindsAreNotAliasedIntoOneInflightRequest()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var factory = new PerRequestBlockingFactory();
        var writer = new RecordingWriter();
        var coordinator = CreateCoordinator(factory, writer, now);
        TrafficDataRequest flowRequest = Request(TrafficFeedKind.Flow);
        TrafficDataRequest closureRequest = Request(TrafficFeedKind.Closure);

        Task<TrafficSnapshotRefreshResult> flow =
            coordinator.RefreshAsync(flowRequest, TestContext.Current.CancellationToken);
        Task<TrafficSnapshotRefreshResult> closure =
            coordinator.RefreshAsync(closureRequest, TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => factory.CallCount == 2);

        Assert.Equal(1, factory.CallCountFor(TrafficFeedKind.Flow));
        Assert.Equal(1, factory.CallCountFor(TrafficFeedKind.Closure));
        factory.Complete(TrafficFeedKind.Flow, EmptySnapshot(now));
        factory.Complete(TrafficFeedKind.Closure, EmptySnapshot(now));
        await Task.WhenAll(flow, closure);

        Assert.Equal(4, writer.CallCount);
    }

    [Fact]
    public async Task RefreshAsync_LastKnownDataIsRetainedOnlyForUnavailableProviderFeedScope()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        NormalizedTrafficEvent tomTom = Event("same", "tomtom", NormalizedTrafficEventKind.Flow, now, 60);
        NormalizedTrafficEvent here = Event("incident", "here", NormalizedTrafficEventKind.Incident, now, 45);
        var factory = new SequenceFactory(
            Snapshot(
                now,
                [tomTom, here],
                [Edge(tomTom, 1), Edge(here, 2)],
                [
                    Status("tomtom", TrafficFeedKind.Flow, TrafficSourceKind.Proxy),
                    Status("here", TrafficFeedKind.Incident, TrafficSourceKind.Proxy),
                ]),
            Snapshot(
                now.AddSeconds(1),
                [],
                [],
                [
                    Status("tomtom", TrafficFeedKind.Flow, TrafficSourceKind.Unavailable),
                    Status("here", TrafficFeedKind.Incident, TrafficSourceKind.Proxy),
                ]));
        var coordinator = CreateCoordinator(factory, new RecordingWriter(), now);
        TrafficDataRequest request = new();

        _ = await coordinator.RefreshAsync(request, TestContext.Current.CancellationToken);
        TrafficSnapshotRefreshResult second =
            await coordinator.RefreshAsync(request, TestContext.Current.CancellationToken);

        NormalizedTrafficEvent retained = Assert.Single(second.Snapshot.Events);
        Assert.Equal("tomtom", retained.ProviderId);
        Assert.Equal("same", retained.Id);
        ValhallaTrafficEdgeUpdate edge = Assert.Single(second.Snapshot.ValhallaEdgeUpdates);
        Assert.Equal("tomtom", edge.ProviderId);
        Assert.Single(second.Snapshot.RouteModifierSources);
        Assert.Single(second.Snapshot.RouteModifierImpacts);
    }

    [Fact]
    public async Task RefreshAsync_AuthoritativeSuccessfulEmptyFeedDoesNotResurrectLastKnownData()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        NormalizedTrafficEvent trafficEvent =
            Event("flow", "tomtom", NormalizedTrafficEventKind.Flow, now, 60);
        var factory = new SequenceFactory(
            Snapshot(
                now,
                [trafficEvent],
                [Edge(trafficEvent, 1)],
                [Status("tomtom", TrafficFeedKind.Flow, TrafficSourceKind.Proxy)]),
            Snapshot(
                now.AddSeconds(1),
                [],
                [],
                [Status("tomtom", TrafficFeedKind.Flow, TrafficSourceKind.Proxy)]));
        var coordinator = CreateCoordinator(factory, new RecordingWriter(), now);
        TrafficDataRequest request = Request(TrafficFeedKind.Flow);

        _ = await coordinator.RefreshAsync(request, TestContext.Current.CancellationToken);
        TrafficSnapshotRefreshResult second =
            await coordinator.RefreshAsync(request, TestContext.Current.CancellationToken);

        Assert.Empty(second.Snapshot.Events);
        Assert.Empty(second.Snapshot.ValhallaEdgeUpdates);
        Assert.Empty(second.Snapshot.RouteModifierSources);
        Assert.Empty(second.Snapshot.RouteModifierImpacts);
    }

    [Fact]
    public async Task RefreshAsync_ProviderAndEventIdCollisionsRetainOnlyFailedScopeEdges()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        NormalizedTrafficEvent providerA =
            Event("collision", "provider-a", NormalizedTrafficEventKind.Flow, now, 60);
        NormalizedTrafficEvent providerBOld =
            Event("collision", "provider-b", NormalizedTrafficEventKind.Flow, now, 70);
        NormalizedTrafficEvent providerBCurrent =
            Event("collision", "provider-b", NormalizedTrafficEventKind.Flow, now.AddSeconds(1), 80);
        var factory = new SequenceFactory(
            Snapshot(
                now,
                [providerA, providerBOld],
                [Edge(providerA, 1), Edge(providerBOld, 2)],
                [
                    Status("provider-a", TrafficFeedKind.Flow, TrafficSourceKind.Proxy),
                    Status("provider-b", TrafficFeedKind.Flow, TrafficSourceKind.Proxy),
                ]),
            Snapshot(
                now.AddSeconds(1),
                [providerBCurrent],
                [Edge(providerBCurrent, 3)],
                [
                    Status("provider-a", TrafficFeedKind.Flow, TrafficSourceKind.Unavailable),
                    Status("provider-b", TrafficFeedKind.Flow, TrafficSourceKind.Proxy),
                ]));
        var coordinator = CreateCoordinator(factory, new RecordingWriter(), now);
        TrafficDataRequest request = Request(TrafficFeedKind.Flow);

        _ = await coordinator.RefreshAsync(request, TestContext.Current.CancellationToken);
        TrafficSnapshotRefreshResult second =
            await coordinator.RefreshAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(2, second.Snapshot.Events.Count);
        Assert.Contains(second.Snapshot.Events, item => item.ProviderId == "provider-a" && item.Id == "collision");
        Assert.Contains(second.Snapshot.Events, item => item.ProviderId == "provider-b" && item.Id == "collision");
        Assert.Equal(2, second.Snapshot.ValhallaEdgeUpdates.Count);
        Assert.Contains(
            second.Snapshot.ValhallaEdgeUpdates,
            edge => edge.ProviderId == "provider-a" && edge.DirectedEdgeIndex == 1);
        Assert.Contains(
            second.Snapshot.ValhallaEdgeUpdates,
            edge => edge.ProviderId == "provider-b" && edge.DirectedEdgeIndex == 3);
        Assert.DoesNotContain(
            second.Snapshot.ValhallaEdgeUpdates,
            edge => edge.ProviderId == "provider-b" && edge.DirectedEdgeIndex == 2);
        Assert.Equal(2, second.Snapshot.RouteModifierImpacts.Count);
        Assert.Equal(2, second.Snapshot.RouteModifierSources.Count);
        TrafficRouteModifierSource sourceA = Assert.Single(
            second.Snapshot.RouteModifierSources,
            source => source.ProviderIds.SequenceEqual(["provider-a"]));
        TrafficRouteModifierSource sourceB = Assert.Single(
            second.Snapshot.RouteModifierSources,
            source => source.ProviderIds.SequenceEqual(["provider-b"]));
        Assert.Equal(1u, Assert.Single(sourceA.AffectedEdges).DirectedEdgeIndex);
        Assert.Equal(3u, Assert.Single(sourceB.AffectedEdges).DirectedEdgeIndex);
    }

    [Fact]
    public async Task RefreshAsync_RecomputesCoherentEdgesImpactsAndSourcesFromEffectiveEvents()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        NormalizedTrafficEvent closure =
            Event("closure", "here", NormalizedTrafficEventKind.Closure, now, null);
        ValhallaTrafficEdgeUpdate edge = Edge(closure, 5, closed: true);
        var staleImpact = new RouteModifierImpact(
            "stale",
            RouteModifierImpactKind.Unknown,
            "must be discarded",
            false);
        var input = new NormalizedTrafficSnapshot(
            now,
            [closure],
            [staleImpact],
            [],
            [edge],
            null,
            [],
            [Status("here", TrafficFeedKind.Closure, TrafficSourceKind.Proxy)]);
        var coordinator = CreateCoordinator(new SequenceFactory(input), new RecordingWriter(), now);

        TrafficSnapshotRefreshResult result = await coordinator.RefreshAsync(
            Request(TrafficFeedKind.Closure),
            TestContext.Current.CancellationToken);

        RouteModifierImpact impact = Assert.Single(result.Snapshot.RouteModifierImpacts);
        TrafficRouteModifierSource source = Assert.Single(result.Snapshot.RouteModifierSources);
        Assert.Equal(RouteModifierImpactKind.RoadClosure, impact.Kind);
        Assert.True(impact.HardDeny);
        Assert.Equal(impact, source.Impact);
        Assert.Equal("here", Assert.Single(source.ProviderIds));
        Assert.Equal("closure", Assert.Single(source.SourceEventIds));
        Assert.Equal(edge, Assert.Single(source.AffectedEdges));
        Assert.Equal(edge, Assert.Single(result.Snapshot.ValhallaEdgeUpdates));
    }

    [Fact]
    public async Task RefreshAsync_SecondPublicationFailureThrowsAndDoesNotReturnPartialSuccess()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var writer = new RecordingWriter(failAtCall: 2);
        var coordinator = CreateCoordinator(
            new SequenceFactory(EmptySnapshot(now)),
            writer,
            now);

        TrafficSnapshotStoreException error =
            await Assert.ThrowsAsync<TrafficSnapshotStoreException>(() =>
                coordinator.RefreshAsync(
                    Request(TrafficFeedKind.Flow),
                    TestContext.Current.CancellationToken));

        Assert.Equal(TrafficSnapshotFailureCode.Incomplete, error.Code);
        Assert.Contains("ClosureOnly", error.Message, StringComparison.Ordinal);
        Assert.Equal(2, writer.CallCount);
        Assert.NotNull(writer.Calls[0].Result.Snapshot);
        Assert.Null(writer.Calls[1].Result.Snapshot);
    }

    private static TrafficSnapshotCoordinator CreateCoordinator(
        ITrafficDataFactory factory,
        IValhallaTrafficSnapshotPairWriter writer,
        DateTimeOffset now)
    {
        var enabled = new ValhallaTrafficWriteOptions("enabled");
        var closure = new ValhallaTrafficWriteOptions("closure-only");
        return new TrafficSnapshotCoordinator(
            factory,
            writer,
            new TrafficSnapshotCoordinatorOptions(
                enabled,
                closure,
                new FixedTimeProvider(now)));
    }

    private static TrafficDataRequest Request(params TrafficFeedKind[] kinds) =>
        new(kinds.ToHashSet());

    private static NormalizedTrafficSnapshot EmptySnapshot(DateTimeOffset now) =>
        Snapshot(now, [], [], []);

    private static NormalizedTrafficSnapshot Snapshot(
        DateTimeOffset created,
        IReadOnlyList<NormalizedTrafficEvent> events,
        IReadOnlyList<ValhallaTrafficEdgeUpdate> edges,
        IReadOnlyList<TrafficFeedSourceStatus> statuses) =>
        new(created, events, [], [], edges, null, [], statuses);

    private static NormalizedTrafficEvent Event(
        string id,
        string provider,
        NormalizedTrafficEventKind kind,
        DateTimeOffset now,
        double? speedKph) =>
        new(
            id,
            provider,
            kind,
            new TrafficGeometry(
                TrafficGeometryKind.LineString,
                [new GeoCoordinate(36.12, -86.67), new GeoCoordinate(36.13, -86.68)],
                TrafficGeometryDirection.AlongCoordinates),
            speedKph,
            100,
            speedKph is null ? null : 120,
            100,
            speedKph is null ? null : 20,
            kind == NormalizedTrafficEventKind.Closure,
            kind == NormalizedTrafficEventKind.Closure ? TrafficSeverity.Closed : TrafficSeverity.Heavy,
            0.9,
            id,
            now,
            now,
            now,
            now.AddMinutes(-1),
            now.AddMinutes(10),
            null,
            new Dictionary<string, string>());

    private static ValhallaTrafficEdgeUpdate Edge(
        NormalizedTrafficEvent trafficEvent,
        uint edgeIndex,
        bool closed = false) =>
        new(
            1,
            edgeIndex,
            TrafficDirection.Forward,
            trafficEvent.CurrentSpeedKph,
            trafficEvent.FreeFlowSpeedKph,
            trafficEvent.DelaySeconds,
            closed,
            trafficEvent.Kind == NormalizedTrafficEventKind.Incident,
            true,
            trafficEvent.Confidence,
            trafficEvent.Id,
            trafficEvent.ProviderId);

    private static TrafficFeedSourceStatus Status(
        string provider,
        TrafficFeedKind feedKind,
        TrafficSourceKind effectiveSource) =>
        new(
            provider,
            feedKind,
            TrafficSourceKind.Proxy,
            effectiveSource,
            effectiveSource == TrafficSourceKind.Unavailable ? 0 : 1,
            0,
            effectiveSource == TrafficSourceKind.Unavailable ? ["TrafficHttpFailure"] : []);

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        for (int attempt = 0; attempt < 100 && !predicate(); attempt++)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        Assert.True(predicate(), "Timed out waiting for controlled asynchronous operation.");
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class BlockingFactory : ITrafficDataFactory
    {
        private readonly TaskCompletionSource<NormalizedTrafficSnapshot> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _callCount;

        public int CallCount => Volatile.Read(ref _callCount);
        public CancellationToken ObservedToken { get; private set; }

        public Task<NormalizedTrafficSnapshot> CreateSnapshotAsync(
            TrafficDataRequest request,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _callCount);
            ObservedToken = cancellationToken;
            return _completion.Task;
        }

        public void Complete(NormalizedTrafficSnapshot snapshot) =>
            _completion.TrySetResult(snapshot);
    }

    private sealed class PerRequestBlockingFactory : ITrafficDataFactory
    {
        private readonly ConcurrentDictionary<TrafficFeedKind, TaskCompletionSource<NormalizedTrafficSnapshot>>
            _completions = new();
        private readonly ConcurrentDictionary<TrafficFeedKind, int> _calls = new();

        public int CallCount => _calls.Values.Sum();

        public int CallCountFor(TrafficFeedKind kind) => _calls.GetValueOrDefault(kind);

        public Task<NormalizedTrafficSnapshot> CreateSnapshotAsync(
            TrafficDataRequest request,
            CancellationToken cancellationToken = default)
        {
            TrafficFeedKind kind = Assert.Single(request.FeedKinds!);
            _calls.AddOrUpdate(kind, 1, static (_, count) => count + 1);
            return _completions.GetOrAdd(
                kind,
                static _ => new TaskCompletionSource<NormalizedTrafficSnapshot>(
                    TaskCreationOptions.RunContinuationsAsynchronously)).Task;
        }

        public void Complete(TrafficFeedKind kind, NormalizedTrafficSnapshot snapshot) =>
            _completions[kind].TrySetResult(snapshot);
    }

    private sealed class SequenceFactory(params NormalizedTrafficSnapshot[] snapshots)
        : ITrafficDataFactory
    {
        private readonly Queue<NormalizedTrafficSnapshot> _snapshots = new(snapshots);

        public Task<NormalizedTrafficSnapshot> CreateSnapshotAsync(
            TrafficDataRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_snapshots)
            {
                return Task.FromResult(_snapshots.Dequeue());
            }
        }
    }

    private sealed class RecordingWriter(int? failAtCall = null) : IValhallaTrafficSnapshotPairWriter
    {
        private readonly object _sync = new();
        private int _callCount;

        public int CallCount => Volatile.Read(ref _callCount);

        public List<WriterCall> Calls { get; } = [];

        public Task<ValhallaTrafficWriteResult> WriteAsync(
            IReadOnlyList<ValhallaTrafficEdgeUpdate> updates,
            ValhallaTrafficWriteOptions options,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int call = Interlocked.Increment(ref _callCount);
            bool fail = failAtCall == call;
            TrafficSnapshotReference? snapshot = fail
                ? null
                : new TrafficSnapshotReference(
                    new string('1', 64),
                    call.ToString("x64", CultureInfo.InvariantCulture),
                    Path.Combine(Path.GetTempPath(), "traffic-coordinator-hostile", call.ToString(CultureInfo.InvariantCulture)),
                    options.CreatedAtUtc ?? DateTimeOffset.UtcNow,
                    options.ExpiresAtUtc ?? DateTimeOffset.UtcNow.AddMinutes(1),
                    options.Policy);
            var result = new ValhallaTrafficWriteResult(!fail, fail ? 0 : updates.Count, [])
            {
                Snapshot = snapshot,
            };
            lock (_sync)
            {
                Calls.Add(new WriterCall(updates.ToArray(), options, result));
            }

            return Task.FromResult(result);
        }

        public async Task<ValhallaTrafficSnapshotPairWriteResult> WritePairAsync(
            IReadOnlyList<ValhallaTrafficEdgeUpdate> enabledUpdates,
            ValhallaTrafficWriteOptions enabledOptions,
            IReadOnlyList<ValhallaTrafficEdgeUpdate> closureOnlyUpdates,
            ValhallaTrafficWriteOptions closureOnlyOptions,
            CancellationToken cancellationToken)
        {
            ValhallaTrafficWriteResult enabled = await WriteAsync(
                enabledUpdates,
                enabledOptions,
                cancellationToken);
            ValhallaTrafficWriteResult closureOnly = await WriteAsync(
                closureOnlyUpdates,
                closureOnlyOptions,
                cancellationToken);
            return new ValhallaTrafficSnapshotPairWriteResult(enabled, closureOnly);
        }
    }

    private sealed record WriterCall(
        IReadOnlyList<ValhallaTrafficEdgeUpdate> Updates,
        ValhallaTrafficWriteOptions Options,
        ValhallaTrafficWriteResult Result);
}

public sealed class TrafficSnapshotStoreStagingHostileBehaviorTests
{
    [Fact]
    public async Task CleanupAsync_OtherStoreDoesNotDeleteLiveStagingAndFailedPublishReleasesReservation()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            "SharpNinja.Valhalla.Tests",
            "traffic-staging-hostile",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var owner = new TrafficSnapshotStore(root);
            var cleaner = new TrafficSnapshotStore(root);
            string staging = owner.CreateStagingDirectory();

            await cleaner.CleanupAsync(TestContext.Current.CancellationToken);
            Assert.True(Directory.Exists(staging));

            DateTimeOffset now = DateTimeOffset.UtcNow;
            var invalidManifest = new TrafficSnapshotManifest(
                new string('1', 64),
                string.Empty,
                TrafficSnapshotPolicy.Enabled,
                now,
                now,
                false,
                []);
            _ = await Assert.ThrowsAsync<TrafficSnapshotStoreException>(() =>
                owner.PublishAsync(staging, invalidManifest, TestContext.Current.CancellationToken));

            await cleaner.CleanupAsync(TestContext.Current.CancellationToken);
            Assert.False(Directory.Exists(staging));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}

public sealed class TrafficSnapshotPairReopenHostileBehaviorTests
{
    private const string GraphSha256 =
        "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";

    [Fact]
    public async Task GetCurrentAsync_ClosureOnlyRemainsAvailableAfterEnabledMemberExpiresAcrossReopen()
    {
        string root = NewRoot("policy-expiry");
        DateTimeOffset now = new(2026, 7, 19, 12, 0, 0, TimeSpan.Zero);
        var clock = new MutableTimeProvider(now);
        try
        {
            var store = new TrafficSnapshotStore(root, timeProvider: clock);
            TrafficSnapshotReference enabled = await PublishEmptyAsync(
                store,
                TrafficSnapshotPolicy.Enabled,
                now.AddMinutes(-1),
                now.AddMinutes(2));
            TrafficSnapshotReference closureOnly = await PublishEmptyAsync(
                store,
                TrafficSnapshotPolicy.ClosureOnly,
                now.AddMinutes(-1),
                now.AddMinutes(15));
            await store.PromoteCurrentPairAsync(
                enabled,
                closureOnly,
                TestContext.Current.CancellationToken);

            clock.Advance(TimeSpan.FromMinutes(3));
            var reopened = new TrafficSnapshotStore(root, timeProvider: clock);

            TrafficSnapshotReference currentClosureOnly =
                Assert.IsType<TrafficSnapshotReference>(
                    await reopened.GetCurrentAsync(
                        GraphSha256,
                        TrafficSnapshotPolicy.ClosureOnly,
                        TestContext.Current.CancellationToken));
            Assert.Equal(closureOnly.Version, currentClosureOnly.Version);
            Assert.False(currentClosureOnly.IsExpired(clock));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task PromoteCurrentPairAsync_MixedRefreshCohortsAreRejected()
    {
        string root = NewRoot("mixed-cohort");
        DateTimeOffset now = new(2026, 7, 19, 12, 0, 0, TimeSpan.Zero);
        var clock = new MutableTimeProvider(now);
        try
        {
            var store = new TrafficSnapshotStore(root, timeProvider: clock);
            TrafficSnapshotReference enabled = await PublishEmptyAsync(
                store,
                TrafficSnapshotPolicy.Enabled,
                now.AddMinutes(-2),
                now.AddMinutes(10));
            TrafficSnapshotReference closureOnly = await PublishEmptyAsync(
                store,
                TrafficSnapshotPolicy.ClosureOnly,
                now.AddMinutes(-1),
                now.AddMinutes(15));
            Assert.NotEqual(enabled.CreatedAtUtc, closureOnly.CreatedAtUtc);

            TrafficSnapshotStoreException error =
                await Assert.ThrowsAsync<TrafficSnapshotStoreException>(() =>
                    store.PromoteCurrentPairAsync(
                        enabled,
                        closureOnly,
                        TestContext.Current.CancellationToken));

            Assert.Equal(TrafficSnapshotFailureCode.Incomplete, error.Code);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task GetCurrentAsync_ValidSameCohortPairSurvivesReopen()
    {
        string root = NewRoot("same-cohort");
        DateTimeOffset now = new(2026, 7, 19, 12, 0, 0, TimeSpan.Zero);
        var clock = new MutableTimeProvider(now);
        try
        {
            var store = new TrafficSnapshotStore(root, timeProvider: clock);
            DateTimeOffset created = now.AddMinutes(-1);
            TrafficSnapshotReference enabled = await PublishEmptyAsync(
                store,
                TrafficSnapshotPolicy.Enabled,
                created,
                now.AddMinutes(2));
            TrafficSnapshotReference closureOnly = await PublishEmptyAsync(
                store,
                TrafficSnapshotPolicy.ClosureOnly,
                created,
                now.AddMinutes(15));
            await store.PromoteCurrentPairAsync(
                enabled,
                closureOnly,
                TestContext.Current.CancellationToken);

            var reopened = new TrafficSnapshotStore(root, timeProvider: clock);
            TrafficSnapshotReference reopenedEnabled =
                Assert.IsType<TrafficSnapshotReference>(
                    await reopened.GetCurrentAsync(
                        GraphSha256,
                        TrafficSnapshotPolicy.Enabled,
                        TestContext.Current.CancellationToken));
            TrafficSnapshotReference reopenedClosureOnly =
                Assert.IsType<TrafficSnapshotReference>(
                    await reopened.GetCurrentAsync(
                        GraphSha256,
                        TrafficSnapshotPolicy.ClosureOnly,
                        TestContext.Current.CancellationToken));

            Assert.Equal(enabled.Version, reopenedEnabled.Version);
            Assert.Equal(closureOnly.Version, reopenedClosureOnly.Version);
            Assert.Equal(reopenedEnabled.CreatedAtUtc, reopenedClosureOnly.CreatedAtUtc);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    [SuppressMessage(
        "xUnit",
        "xUnit1051",
        Justification = "The linked token is deliberately cancelled during closure-member publication; the parent test token is preserved.")]
    public async Task WritePairAsync_CancellationDuringSecondMemberLeavesPreviousPairCurrentAcrossReopen()
    {
        string graphRoot = FindMonacoFixture();
        string root = NewRoot("cancel-second-member");
        DateTimeOffset now = DateTimeOffset.UtcNow;
        try
        {
            string graphSha = await GraphFingerprint.ComputeSha256Async(
                graphRoot,
                TestContext.Current.CancellationToken);
            var store = new TrafficSnapshotStore(root);
            var seedWriter = new DirectoryValhallaTrafficTileWriter(store);
            ValhallaTrafficWriteOptions seedEnabled = Options(
                root,
                graphRoot,
                graphSha,
                now.AddMinutes(-1),
                now.AddMinutes(10),
                TrafficSnapshotPolicy.Enabled);
            ValhallaTrafficWriteOptions seedClosure = seedEnabled with
            {
                Policy = TrafficSnapshotPolicy.ClosureOnly,
            };
            ValhallaTrafficSnapshotPairWriteResult seed = await seedWriter.WritePairAsync(
                [],
                seedEnabled,
                [],
                seedClosure,
                TestContext.Current.CancellationToken);
            Assert.True(seed.Succeeded);
            TrafficSnapshotReference seedEnabledReference =
                Assert.IsType<TrafficSnapshotReference>(seed.Enabled.Snapshot);
            TrafficSnapshotReference seedClosureReference =
                Assert.IsType<TrafficSnapshotReference>(seed.ClosureOnly.Snapshot);

            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
                TestContext.Current.CancellationToken);
            var cancellingClock = new CancelOnCallTimeProvider(
                now.AddMinutes(1),
                cancellation,
                cancelOnCall: 3);
            var cancellingWriter = new DirectoryValhallaTrafficTileWriter(store, cancellingClock);
            ValhallaTrafficWriteOptions attemptedEnabled = new(root)
            {
                GraphTileDirectory = graphRoot,
                GraphSha256 = graphSha,
                ExpiresAtUtc = now.AddMinutes(20),
                Policy = TrafficSnapshotPolicy.Enabled,
            };
            ValhallaTrafficWriteOptions attemptedClosure = attemptedEnabled with
            {
                Policy = TrafficSnapshotPolicy.ClosureOnly,
            };

            _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                cancellingWriter.WritePairAsync(
                    [],
                    attemptedEnabled,
                    [],
                    attemptedClosure,
                    cancellation.Token));
            Assert.Equal(3, cancellingClock.CallCount);

            await AssertCurrentPairAcrossReopenAsync(
                root,
                graphSha,
                seedEnabledReference,
                seedClosureReference);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task WritePairAsync_SecondMemberFailureLeavesPreviousPairCurrentAcrossReopen()
    {
        string graphRoot = FindMonacoFixture();
        string root = NewRoot("fail-second-member");
        DateTimeOffset now = DateTimeOffset.UtcNow;
        try
        {
            string graphSha = await GraphFingerprint.ComputeSha256Async(
                graphRoot,
                TestContext.Current.CancellationToken);
            var store = new TrafficSnapshotStore(root);
            var writer = new DirectoryValhallaTrafficTileWriter(store);
            ValhallaTrafficWriteOptions seedEnabled = Options(
                root,
                graphRoot,
                graphSha,
                now.AddMinutes(-1),
                now.AddMinutes(10),
                TrafficSnapshotPolicy.Enabled);
            ValhallaTrafficWriteOptions seedClosure = seedEnabled with
            {
                Policy = TrafficSnapshotPolicy.ClosureOnly,
            };
            ValhallaTrafficSnapshotPairWriteResult seed = await writer.WritePairAsync(
                [],
                seedEnabled,
                [],
                seedClosure,
                TestContext.Current.CancellationToken);
            Assert.True(seed.Succeeded);
            TrafficSnapshotReference seedEnabledReference =
                Assert.IsType<TrafficSnapshotReference>(seed.Enabled.Snapshot);
            TrafficSnapshotReference seedClosureReference =
                Assert.IsType<TrafficSnapshotReference>(seed.ClosureOnly.Snapshot);

            ValhallaTrafficWriteOptions attemptedEnabled = seedEnabled with
            {
                CreatedAtUtc = now.AddMinutes(1),
                ExpiresAtUtc = now.AddMinutes(20),
            };
            ValhallaTrafficWriteOptions invalidClosure = seedClosure with
            {
                GraphSha256 = new string('B', 64),
                CreatedAtUtc = now.AddMinutes(1),
                ExpiresAtUtc = now.AddMinutes(20),
            };
            ValhallaTrafficSnapshotPairWriteResult failed = await writer.WritePairAsync(
                [],
                attemptedEnabled,
                [],
                invalidClosure,
                TestContext.Current.CancellationToken);

            Assert.False(failed.Succeeded);
            Assert.True(failed.Enabled.Succeeded);
            Assert.NotNull(failed.Enabled.Snapshot);
            Assert.False(failed.ClosureOnly.Succeeded);
            Assert.Null(failed.ClosureOnly.Snapshot);

            await AssertCurrentPairAcrossReopenAsync(
                root,
                graphSha,
                seedEnabledReference,
                seedClosureReference);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    private static ValhallaTrafficWriteOptions Options(
        string root,
        string graphRoot,
        string graphSha,
        DateTimeOffset createdAtUtc,
        DateTimeOffset expiresAtUtc,
        TrafficSnapshotPolicy policy) =>
        new(root)
        {
            GraphTileDirectory = graphRoot,
            GraphSha256 = graphSha,
            CreatedAtUtc = createdAtUtc,
            ExpiresAtUtc = expiresAtUtc,
            Policy = policy,
        };

    private static async Task AssertCurrentPairAcrossReopenAsync(
        string root,
        string graphSha,
        TrafficSnapshotReference expectedEnabled,
        TrafficSnapshotReference expectedClosureOnly)
    {
        var reopened = new TrafficSnapshotStore(root);
        TrafficSnapshotReference currentEnabled =
            Assert.IsType<TrafficSnapshotReference>(
                await reopened.GetCurrentAsync(
                    graphSha,
                    TrafficSnapshotPolicy.Enabled,
                    TestContext.Current.CancellationToken));
        TrafficSnapshotReference currentClosureOnly =
            Assert.IsType<TrafficSnapshotReference>(
                await reopened.GetCurrentAsync(
                    graphSha,
                    TrafficSnapshotPolicy.ClosureOnly,
                    TestContext.Current.CancellationToken));

        Assert.Equal(expectedEnabled.Version, currentEnabled.Version);
        Assert.Equal(expectedClosureOnly.Version, currentClosureOnly.Version);
        Assert.Equal(expectedEnabled.CreatedAtUtc, currentEnabled.CreatedAtUtc);
        Assert.Equal(expectedClosureOnly.CreatedAtUtc, currentClosureOnly.CreatedAtUtc);
    }

    private static string FindMonacoFixture()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string candidate = Path.Combine(
                directory.FullName,
                "artifacts",
                "valhalla-monaco-tiles");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new Xunit.Sdk.XunitException("Tracked Monaco graph fixture was not found.");
    }

    private sealed class CancelOnCallTimeProvider(
        DateTimeOffset now,
        CancellationTokenSource cancellation,
        int cancelOnCall) : TimeProvider
    {
        private int _callCount;

        public int CallCount => Volatile.Read(ref _callCount);

        public override DateTimeOffset GetUtcNow()
        {
            int call = Interlocked.Increment(ref _callCount);
            if (call == cancelOnCall)
            {
                cancellation.Cancel();
            }

            return now;
        }
    }

    private static async Task<TrafficSnapshotReference> PublishEmptyAsync(
        TrafficSnapshotStore store,
        TrafficSnapshotPolicy policy,
        DateTimeOffset createdAtUtc,
        DateTimeOffset expiresAtUtc)
    {
        string staging = store.CreateStagingDirectory();
        var manifest = new TrafficSnapshotManifest(
            GraphSha256,
            string.Empty,
            policy,
            createdAtUtc,
            expiresAtUtc,
            false,
            []);
        return await store.PublishAsync(
            staging,
            manifest,
            TestContext.Current.CancellationToken);
    }

    private static string NewRoot(string scenario)
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            "SharpNinja.Valhalla.Tests",
            "traffic-pair-reopen-hostile",
            scenario + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void DeleteRoot(string root)
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan elapsed) => _now += elapsed;
    }
}
