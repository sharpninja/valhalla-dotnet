using SharpNinja.Valhalla.Traffic;
using SharpNinja.Valhalla.Traffic.Routing;
using SharpNinja.Valhalla.Traffic.Tiles;

namespace SharpNinja.Valhalla.Tests.Traffic;

public sealed class DirectoryValhallaTrafficTileWriterTests
{
    [Fact]
    public Task WriteAsync_ProducesNativeCompatibleLayoutAndInvalidSentinels() =>
        new TrafficRuntimeHostileBehaviorTests()
            .WriteAsync_NativeBytes_EncodeHeaderInvalidSpeedClosureAndIncidentDelay();

    [Fact]
    public Task WriteAsync_WithMismatchedGraphMetadata_RejectsGeneration() =>
        TrafficRuntimeExactContractBehavior
            .WriteAsync_RejectsGraphTileAndEdgeIdentityMismatches();

    [Fact]
    public async Task WriteAsync_AppliesOnlyDirectionSafeClosures()
    {
        var behavior = new TrafficRuntimeHostileBehaviorTests();
        await behavior.WriteAsync_NativeBytes_EncodeHeaderInvalidSpeedClosureAndIncidentDelay();
        await behavior.WriteAsync_UnresolvedClosure_DoesNotWriteUnsafeDirection();
    }

    [Fact]
    public Task WriteAsync_GroupsAndQuantizesResolvedSpeedUpdates() =>
        TrafficRuntimeExactContractBehavior
            .WriteAsync_GroupsClampsQuantizesAndPreservesDeterministicPrecedence();

    [Fact]
    public async Task WriteAsync_IncidentDelayWithoutSpeed_EncodesEquivalentSpeed()
    {
        await new TrafficRuntimeHostileBehaviorTests()
            .WriteAsync_NativeBytes_EncodeHeaderInvalidSpeedClosureAndIncidentDelay();
        await new EmbeddedValhallaRoutingTrafficProvenanceBehaviorTests()
            .MapCandidate_PinnedLiveTrafficGeneration_PreservesVersionAndDoesNotDoubleCountEta();
    }
}

public sealed class TrafficSnapshotStoreTests
{
    [Fact]
    public Task PublishAsync_PromotesOnlyCompleteContentAddressedGeneration() =>
        TrafficRuntimeExactContractBehavior
            .PublishAsync_PartialAndCancelledWritesNeverBecomeCurrent();

    [Fact]
    public Task CleanupAsync_RetainsThreeAndPinsActiveLeases() =>
        TrafficRuntimeExactContractBehavior
            .CleanupAsync_RetainsThreePinsLeasesAndRemovesAbandonedStaging();
}

public sealed class GraphReaderTrafficTests
{
    [Fact]
    public async Task Create_WithTrafficGeneration_UsesLiveSpeedAndClosure()
    {
        await new TrafficRuntimeHostileBehaviorTests()
            .GraphReader_GzipGraphTile_AttachesPinnedTrafficAndHonorsClosure();
        await new EmbeddedValhallaRoutingTrafficProvenanceBehaviorTests()
            .CalculateRouteAsync_LiveSnapshotFlowsThroughEngineAndProvenance();
    }
}

public sealed class EmbeddedValhallaGraphReaderFactoryTests
{
    [Fact]
    public async Task AcquireAsync_PinsOneTrafficGenerationForRouteLifetime()
    {
        await new EmbeddedValhallaRoutingTrafficProvenanceBehaviorTests()
            .MapCandidate_PinnedLiveTrafficGeneration_PreservesVersionAndDoesNotDoubleCountEta();
        await new TrafficRuntimeHostileBehaviorTests()
            .ReaderFactory_VersionChange_RetiresOldReaderOnlyAfterItsLeaseEnds();
    }

    [Fact]
    public async Task AcquireAsync_ClearsCacheOnlyWhenTrafficVersionChanges()
    {
        var behavior = new TrafficRuntimeHostileBehaviorTests();
        await behavior.ReaderFactory_SameVersionConcurrentLeases_ShareReaderAndDisposeExactlyOnce();
        await behavior.ReaderFactory_VersionChange_RetiresOldReaderOnlyAfterItsLeaseEnds();
    }

    [Fact]
    public Task AcquireAsync_ConcurrentCancellationAndPublish_IsLeaseSafe() =>
        TrafficRuntimeExactContractBehavior
            .AcquireAsync_CancellationPublicationAndDisposalRemainLeaseSafe();
}

public sealed class BidirectionalAStarTrafficTests
{
    [Fact]
    public async Task Route_WithTrafficSnapshot_UsesInvariantTimeInfo()
    {
        new TrafficRuntimeHostileBehaviorTests()
            .InvariantTrafficTime_EquivalentInstantsProduceDeterministicUtcSecondsOfWeek();
        TrafficRuntimeExactContractBehavior
            .Route_WithTrafficSnapshot_PassesInvariantTimeIntoAStarCosting();
        await new EmbeddedValhallaRoutingTrafficProvenanceBehaviorTests()
            .CalculateRouteAsync_LiveSnapshotFlowsThroughEngineAndProvenance();
    }
}

public sealed class EmbeddedValhallaRoutingClientTrafficTests
{
    [Fact]
    public Task RouteAsync_LiveTrafficDuration_IsAppliedExactlyOnce() =>
        new EmbeddedValhallaRoutingTrafficProvenanceBehaviorTests()
            .CalculateRouteAsync_LiveSnapshotFlowsThroughEngineAndProvenance();

    [Fact]
    public Task RouteAsync_InvalidTrafficSnapshot_ReturnsTypedFailure() =>
        new EmbeddedValhallaRoutingTrafficProvenanceBehaviorTests()
            .CalculateRouteAsync_InvalidSnapshots_ReturnTypedFailureCodes();
}

public sealed class TrafficSnapshotCoordinatorTests
{
    [Fact]
    [Trait("Requirement", "FR-VALHALLA-012")]
    [Trait("AcceptanceCriterion", "AC-VALHALLA-012-16")]
    public async Task RefreshAsync_FetchesOnceAndPublishesEnabledAndClosureOnly()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        NormalizedTrafficEvent flow = Event(
            "flow-a",
            "tomtom",
            NormalizedTrafficEventKind.Flow,
            now,
            validUntilUtc: now.AddMinutes(10));
        NormalizedTrafficEvent closure = Event(
            "closure-a",
            "tomtom",
            NormalizedTrafficEventKind.Closure,
            now,
            validUntilUtc: now.AddMinutes(10),
            hardDeny: true);
        NormalizedTrafficSnapshot snapshot = Snapshot(
            now,
            [flow, closure],
            [Edge(flow, 1), Edge(closure, 2, closed: true)],
            [Status("tomtom", TrafficFeedKind.Composite, TrafficSourceKind.Proxy)]);
        var factory = new CountingFactory(snapshot);
        var writer = new PairBlockingWriter();
        TrafficSnapshotCoordinator coordinator = Coordinator(factory, writer, new MutableTimeProvider(now));

        Task<TrafficSnapshotRefreshResult> refresh = coordinator.RefreshAsync(
            Request(TrafficFeedKind.Composite),
            TestContext.Current.CancellationToken);

        await WaitUntilAsync(() => writer.CallCount == 2);
        Assert.False(
            refresh.IsCompleted,
            "The coordinator must not expose an enabled/closure pair until both publications succeed.");
        Assert.Equal(TrafficSnapshotPolicy.Enabled, writer.Calls[0].Options.Policy);
        Assert.Equal(2, writer.Calls[0].Updates.Count);
        Assert.Equal(TrafficSnapshotPolicy.ClosureOnly, writer.Calls[1].Options.Policy);
        Assert.Equal("closure-a", Assert.Single(writer.Calls[1].Updates).SourceEventId);

        writer.ReleaseClosurePublication();
        TrafficSnapshotRefreshResult result = await refresh;

        Assert.Equal(1, factory.CallCount);
        Assert.Equal(snapshot.CreatedAtUtc, result.Snapshot.CreatedAtUtc);
        Assert.Equal(snapshot.Events, result.Snapshot.Events);
        Assert.Equal(snapshot.ValhallaEdgeUpdates, result.Snapshot.ValhallaEdgeUpdates);
        Assert.Equal(snapshot.SourceStatuses, result.Snapshot.SourceStatuses);
        Assert.Equal(TrafficSnapshotPolicy.Enabled, result.EnabledSnapshot.Policy);
        Assert.Equal(TrafficSnapshotPolicy.ClosureOnly, result.ClosureOnlySnapshot.Policy);
        Assert.NotEqual(
            result.EnabledSnapshot.Version,
            result.ClosureOnlySnapshot.Version);
    }

    [Fact]
    [Trait("Requirement", "FR-VALHALLA-012")]
    [Trait("AcceptanceCriterion", "AC-VALHALLA-012-17")]
    public async Task RefreshAsync_TrafficDisabled_PublishesClosureOnlyConstraints()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        NormalizedTrafficEvent flow = Event(
            "flow",
            "here",
            NormalizedTrafficEventKind.Flow,
            now,
            validUntilUtc: now.AddMinutes(10));
        NormalizedTrafficEvent incident = Event(
            "incident",
            "here",
            NormalizedTrafficEventKind.Incident,
            now,
            validUntilUtc: now.AddMinutes(10));
        NormalizedTrafficEvent closure = Event(
            "closure",
            "here",
            NormalizedTrafficEventKind.Closure,
            now,
            validUntilUtc: now.AddMinutes(10),
            hardDeny: true);
        NormalizedTrafficEvent restriction = Event(
            "restriction",
            "here",
            NormalizedTrafficEventKind.Restriction,
            now,
            validUntilUtc: now.AddMinutes(10),
            hardDeny: true);
        var writer = new RecordingWriter();
        TrafficSnapshotCoordinator coordinator = Coordinator(
            new CountingFactory(Snapshot(
                now,
                [flow, incident, closure, restriction],
                [
                    Edge(flow, 1),
                    Edge(incident, 2),
                    Edge(closure, 3, closed: true),
                    Edge(restriction, 4, closed: true),
                ],
                [Status("here", TrafficFeedKind.Composite, TrafficSourceKind.Proxy)])),
            writer,
            new MutableTimeProvider(now));

        TrafficSnapshotRefreshResult result = await coordinator.RefreshAsync(
            Request(TrafficFeedKind.Composite),
            TestContext.Current.CancellationToken);

        Assert.Equal(2, writer.CallCount);
        Assert.Equal(
            ["closure", "restriction"],
            writer.Calls[1].Updates
                .Select(static update => update.SourceEventId)
                .OrderBy(static id => id, StringComparer.Ordinal)
                .ToArray());
        Assert.All(writer.Calls[1].Updates, static update =>
        {
            Assert.True(update.Closed);
            Assert.True(update.DirectionResolved);
        });
        Assert.Equal(TrafficSnapshotPolicy.ClosureOnly, writer.Calls[1].Options.Policy);
        Assert.Equal(TrafficSnapshotPolicy.ClosureOnly, result.ClosureOnlySnapshot.Policy);
        Assert.DoesNotContain(
            writer.Calls[1].Updates,
            static update => update.SourceEventId is "flow" or "incident");
        await new EmbeddedValhallaRoutingTrafficProvenanceBehaviorTests()
            .CalculateRouteAsync_LiveSnapshotFlowsThroughEngineAndProvenance();
    }

    [Fact]
    [Trait("Requirement", "FR-VALHALLA-012")]
    [Trait("AcceptanceCriterion", "AC-VALHALLA-012-18")]
    public async Task RefreshAsync_FeedFailure_RetainsOnlyUnexpiredEvents()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var clock = new MutableTimeProvider(now);
        NormalizedTrafficEvent defaultFlow = Event(
            "flow-default",
            "tomtom",
            NormalizedTrafficEventKind.Flow,
            now);
        NormalizedTrafficEvent explicitFlow = Event(
            "flow-explicit",
            "tomtom",
            NormalizedTrafficEventKind.Flow,
            now,
            validUntilUtc: now.AddMinutes(10));
        NormalizedTrafficEvent incident = Event(
            "incident",
            "tomtom",
            NormalizedTrafficEventKind.Incident,
            now);
        NormalizedTrafficEvent closure = Event(
            "closure",
            "tomtom",
            NormalizedTrafficEventKind.Closure,
            now,
            hardDeny: true);
        NormalizedTrafficEvent restriction = Event(
            "restriction",
            "tomtom",
            NormalizedTrafficEventKind.Restriction,
            now,
            hardDeny: true);
        NormalizedTrafficSnapshot first = Snapshot(
            now,
            [defaultFlow, explicitFlow, incident, closure, restriction],
            [
                Edge(defaultFlow, 1),
                Edge(explicitFlow, 2),
                Edge(incident, 3),
                Edge(closure, 4, closed: true),
                Edge(restriction, 5, closed: true),
            ],
            [Status("tomtom", TrafficFeedKind.Composite, TrafficSourceKind.Proxy)]);
        NormalizedTrafficSnapshot unavailable = Snapshot(
            now.AddMinutes(3),
            [],
            [],
            [Status("tomtom", TrafficFeedKind.Composite, TrafficSourceKind.Unavailable)]);
        var expiryFactory = new SequenceFactory(first, unavailable);
        var expiryWriter = new RecordingWriter();
        TrafficSnapshotCoordinator expiryCoordinator = Coordinator(
            expiryFactory,
            expiryWriter,
            clock);

        _ = await expiryCoordinator.RefreshAsync(
            Request(TrafficFeedKind.Composite),
            TestContext.Current.CancellationToken);
        clock.Advance(TimeSpan.FromMinutes(3));
        TrafficSnapshotRefreshResult retained = await expiryCoordinator.RefreshAsync(
            Request(TrafficFeedKind.Composite),
            TestContext.Current.CancellationToken);

        Assert.Equal(
            ["closure", "flow-explicit", "incident", "restriction"],
            retained.Snapshot.Events
                .Select(static trafficEvent => trafficEvent.Id)
                .OrderBy(static id => id, StringComparer.Ordinal)
                .ToArray());
        Assert.DoesNotContain(
            retained.Snapshot.Events,
            static trafficEvent => trafficEvent.Id == "flow-default");

        NormalizedTrafficEvent publishedA = Event(
            "flow-lkg",
            "tomtom",
            NormalizedTrafficEventKind.Flow,
            now,
            validUntilUtc: now.AddMinutes(10),
            currentSpeedKph: 80);
        NormalizedTrafficEvent unpublishedB = Event(
            "flow-lkg",
            "tomtom",
            NormalizedTrafficEventKind.Flow,
            now.AddSeconds(1),
            validUntilUtc: now.AddMinutes(10),
            currentSpeedKph: 20);
        var poisoningFactory = new SequenceFactory(
            Snapshot(
                now,
                [publishedA],
                [Edge(publishedA, 11)],
                [Status("tomtom", TrafficFeedKind.Flow, TrafficSourceKind.Proxy)]),
            Snapshot(
                now.AddSeconds(1),
                [unpublishedB],
                [Edge(unpublishedB, 11)],
                [Status("tomtom", TrafficFeedKind.Flow, TrafficSourceKind.Proxy)]),
            Snapshot(
                now.AddSeconds(2),
                [],
                [],
                [Status("tomtom", TrafficFeedKind.Flow, TrafficSourceKind.Unavailable)]));
        var poisoningWriter = new RecordingWriter(failingCalls: new HashSet<int> { 4 });
        TrafficSnapshotCoordinator poisoningCoordinator = Coordinator(
            poisoningFactory,
            poisoningWriter,
            new MutableTimeProvider(now));

        _ = await poisoningCoordinator.RefreshAsync(
            Request(TrafficFeedKind.Flow),
            TestContext.Current.CancellationToken);
        TrafficSnapshotStoreException publicationFailure =
            await Assert.ThrowsAsync<TrafficSnapshotStoreException>(() =>
                poisoningCoordinator.RefreshAsync(
                    Request(TrafficFeedKind.Flow),
                    TestContext.Current.CancellationToken));
        TrafficSnapshotRefreshResult afterFailure = await poisoningCoordinator.RefreshAsync(
            Request(TrafficFeedKind.Flow),
            TestContext.Current.CancellationToken);

        Assert.Equal(TrafficSnapshotFailureCode.Incomplete, publicationFailure.Code);
        NormalizedTrafficEvent retainedA = Assert.Single(afterFailure.Snapshot.Events);
        Assert.Equal("flow-lkg", retainedA.Id);
        Assert.Equal(80, retainedA.CurrentSpeedKph);
        Assert.DoesNotContain(
            afterFailure.Snapshot.Events,
            trafficEvent => trafficEvent.CurrentSpeedKph == unpublishedB.CurrentSpeedKph);

        NormalizedTrafficEvent initialFlow = Event(
            "same-edge-flow-initial",
            "tomtom",
            NormalizedTrafficEventKind.Flow,
            now,
            validUntilUtc: now.AddMinutes(10),
            currentSpeedKph: 70);
        NormalizedTrafficEvent currentFlow = Event(
            "same-edge-flow-current",
            "tomtom",
            NormalizedTrafficEventKind.Flow,
            now.AddSeconds(1),
            validUntilUtc: now.AddMinutes(10),
            currentSpeedKph: 65);
        NormalizedTrafficEvent retainedIncident = Event(
            "same-edge-incident",
            "here",
            NormalizedTrafficEventKind.Incident,
            now,
            validUntilUtc: now.AddMinutes(10),
            currentSpeedKph: null);
        var orthogonalFactory = new SequenceFactory(
            Snapshot(
                now,
                [initialFlow, retainedIncident],
                [Edge(initialFlow, 21), Edge(retainedIncident, 21)],
                [
                    Status("tomtom", TrafficFeedKind.Flow, TrafficSourceKind.Proxy),
                    Status("here", TrafficFeedKind.Incident, TrafficSourceKind.Proxy),
                ]),
            Snapshot(
                now.AddSeconds(1),
                [currentFlow],
                [Edge(currentFlow, 21)],
                [
                    Status("tomtom", TrafficFeedKind.Flow, TrafficSourceKind.Proxy),
                    Status("here", TrafficFeedKind.Incident, TrafficSourceKind.Unavailable),
                ]));
        var orthogonalWriter = new RecordingWriter();
        TrafficSnapshotCoordinator orthogonalCoordinator = Coordinator(
            orthogonalFactory,
            orthogonalWriter,
            new MutableTimeProvider(now));

        _ = await orthogonalCoordinator.RefreshAsync(
            Request(TrafficFeedKind.Flow, TrafficFeedKind.Incident),
            TestContext.Current.CancellationToken);
        TrafficSnapshotRefreshResult orthogonalResult = await orthogonalCoordinator.RefreshAsync(
            Request(TrafficFeedKind.Flow, TrafficFeedKind.Incident),
            TestContext.Current.CancellationToken);

        Assert.Equal(
            ["same-edge-flow-current", "same-edge-incident"],
            orthogonalResult.Snapshot.Events
                .Select(static trafficEvent => trafficEvent.Id)
                .OrderBy(static id => id, StringComparer.Ordinal)
                .ToArray());
        Assert.Equal(2, orthogonalResult.Snapshot.ValhallaEdgeUpdates.Count);
        Assert.Single(
            orthogonalResult.Snapshot.ValhallaEdgeUpdates,
            static edge => edge.CurrentSpeedKph == 65);
        Assert.Single(
            orthogonalResult.Snapshot.ValhallaEdgeUpdates,
            static edge => edge.HasIncident);
        Assert.Equal(
            [RouteModifierImpactKind.TrafficDelay, RouteModifierImpactKind.Incident],
            orthogonalResult.Snapshot.RouteModifierImpacts
                .Select(static impact => impact.Kind)
                .OrderBy(static kind => kind)
                .ToArray());
        Assert.All(
            orthogonalResult.Snapshot.RouteModifierSources,
            static source => Assert.NotEmpty(source.AffectedEdges));
        Assert.Equal(
            ["same-edge-flow-current", "same-edge-incident"],
            orthogonalResult.Snapshot.RouteModifierSources
                .SelectMany(static source => source.SourceEventIds)
                .OrderBy(static id => id, StringComparer.Ordinal)
                .ToArray());
    }

    [Fact]
    [Trait("Requirement", "FR-VALHALLA-012")]
    [Trait("AcceptanceCriterion", "AC-VALHALLA-012-19")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "xUnit",
        "xUnit1051",
        Justification = "The independently cancelled waiter token is the behavior under test.")]
    public async Task RefreshAsync_ConcurrentWaiters_AreSingleFlightAndCancellationSafe()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        NormalizedTrafficEvent closure = Event(
            "closure",
            "here",
            NormalizedTrafficEventKind.Closure,
            now,
            validUntilUtc: now.AddMinutes(10),
            hardDeny: true);
        NormalizedTrafficSnapshot snapshot = Snapshot(
            now,
            [closure],
            [Edge(closure, 1, closed: true)],
            [Status("here", TrafficFeedKind.Closure, TrafficSourceKind.Proxy)]);
        var factory = new BlockingFactory();
        var writer = new RecordingWriter();
        TrafficSnapshotCoordinator coordinator = Coordinator(
            factory,
            writer,
            new MutableTimeProvider(now));
        TrafficDataRequest request = Request(TrafficFeedKind.Closure);
        using var cancelled = new CancellationTokenSource();

        Task<TrafficSnapshotRefreshResult> survivingWaiter = coordinator.RefreshAsync(
            request,
            TestContext.Current.CancellationToken);
        Task<TrafficSnapshotRefreshResult> cancelledWaiter = coordinator.RefreshAsync(
            request,
            cancelled.Token);
        await WaitUntilAsync(() => factory.CallCount == 1);
        cancelled.Cancel();

        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await cancelledWaiter);
        Assert.False(survivingWaiter.IsCompleted);
        factory.Complete(snapshot);
        TrafficSnapshotRefreshResult result = await survivingWaiter;

        Assert.Equal(1, factory.CallCount);
        Assert.False(factory.ObservedToken.CanBeCanceled);
        Assert.Equal(2, writer.CallCount);
        Assert.Equal(TrafficSnapshotPolicy.Enabled, result.EnabledSnapshot.Policy);
        Assert.Equal(TrafficSnapshotPolicy.ClosureOnly, result.ClosureOnlySnapshot.Policy);

        var concretePairBehavior = new TrafficSnapshotPairReopenHostileBehaviorTests();
        await concretePairBehavior
            .WritePairAsync_CancellationDuringSecondMemberLeavesPreviousPairCurrentAcrossReopen();
        await concretePairBehavior
            .WritePairAsync_SecondMemberFailureLeavesPreviousPairCurrentAcrossReopen();
    }

    private static TrafficSnapshotCoordinator Coordinator(
        ITrafficDataFactory factory,
        IValhallaTrafficSnapshotPairWriter writer,
        TimeProvider timeProvider) =>
        new(
            factory,
            writer,
            new TrafficSnapshotCoordinatorOptions(
                new ValhallaTrafficWriteOptions("enabled"),
                new ValhallaTrafficWriteOptions("closure-only"),
                timeProvider));

    private static TrafficDataRequest Request(params TrafficFeedKind[] kinds) =>
        new(kinds.ToHashSet());

    private static NormalizedTrafficSnapshot Snapshot(
        DateTimeOffset createdAtUtc,
        IReadOnlyList<NormalizedTrafficEvent> events,
        IReadOnlyList<ValhallaTrafficEdgeUpdate> edges,
        IReadOnlyList<TrafficFeedSourceStatus> statuses) =>
        new(createdAtUtc, events, [], [], edges, null, [], statuses);

    private static NormalizedTrafficEvent Event(
        string id,
        string providerId,
        NormalizedTrafficEventKind kind,
        DateTimeOffset now,
        DateTimeOffset? validUntilUtc = null,
        bool hardDeny = false,
        double? currentSpeedKph = 50) =>
        new(
            id,
            providerId,
            kind,
            new TrafficGeometry(
                TrafficGeometryKind.LineString,
                [new GeoCoordinate(36.12, -86.67), new GeoCoordinate(36.13, -86.68)],
                TrafficGeometryDirection.AlongCoordinates),
            currentSpeedKph,
            100,
            currentSpeedKph is null ? null : 120,
            100,
            currentSpeedKph is null ? null : 20,
            hardDeny,
            hardDeny ? TrafficSeverity.Closed : TrafficSeverity.Heavy,
            0.9,
            id,
            now,
            now,
            now,
            now.AddMinutes(-1),
            validUntilUtc,
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
        string providerId,
        TrafficFeedKind feedKind,
        TrafficSourceKind effectiveSource) =>
        new(
            providerId,
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

    private sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan elapsed) => _utcNow += elapsed;
    }

    private sealed class CountingFactory(NormalizedTrafficSnapshot snapshot) : ITrafficDataFactory
    {
        private int _callCount;

        public int CallCount => Volatile.Read(ref _callCount);

        public Task<NormalizedTrafficSnapshot> CreateSnapshotAsync(
            TrafficDataRequest request,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _callCount);
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(snapshot);
        }
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

    private class RecordingWriter(IReadOnlySet<int>? failingCalls = null)
        : IValhallaTrafficSnapshotPairWriter
    {
        private readonly object _sync = new();
        private int _callCount;

        public int CallCount => Volatile.Read(ref _callCount);

        public List<WriterCall> Calls { get; } = [];

        public virtual Task<ValhallaTrafficWriteResult> WriteAsync(
            IReadOnlyList<ValhallaTrafficEdgeUpdate> updates,
            ValhallaTrafficWriteOptions options,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int call = Interlocked.Increment(ref _callCount);
            bool fail = failingCalls?.Contains(call) == true;
            ValhallaTrafficWriteResult result = Result(call, updates.Count, options, fail);
            lock (_sync)
            {
                Calls.Add(new WriterCall(updates.ToArray(), options, result));
            }

            return Task.FromResult(result);
        }

        public virtual async Task<ValhallaTrafficSnapshotPairWriteResult> WritePairAsync(
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

        protected static ValhallaTrafficWriteResult Result(
            int call,
            int updateCount,
            ValhallaTrafficWriteOptions options,
            bool fail)
        {
            TrafficSnapshotReference? snapshot = fail
                ? null
                : new TrafficSnapshotReference(
                    new string('1', 64),
                    call.ToString("x64", System.Globalization.CultureInfo.InvariantCulture),
                    Path.Combine(
                        Path.GetTempPath(),
                        "traffic-runtime-contract",
                        call.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                    options.CreatedAtUtc ?? DateTimeOffset.UtcNow,
                    options.ExpiresAtUtc ?? DateTimeOffset.UtcNow.AddMinutes(1),
                    options.Policy);
            return new ValhallaTrafficWriteResult(!fail, fail ? 0 : updateCount, [])
            {
                Snapshot = snapshot,
            };
        }
    }

    private sealed class PairBlockingWriter : RecordingWriter
    {
        private readonly TaskCompletionSource<bool> _releaseClosure =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async Task<ValhallaTrafficWriteResult> WriteAsync(
            IReadOnlyList<ValhallaTrafficEdgeUpdate> updates,
            ValhallaTrafficWriteOptions options,
            CancellationToken cancellationToken)
        {
            ValhallaTrafficWriteResult result = await base.WriteAsync(
                updates,
                options,
                cancellationToken);
            if (options.Policy == TrafficSnapshotPolicy.ClosureOnly)
            {
                await _releaseClosure.Task.WaitAsync(cancellationToken);
            }

            return result;
        }

        public void ReleaseClosurePublication() => _releaseClosure.TrySetResult(true);
    }

    private sealed record WriterCall(
        IReadOnlyList<ValhallaTrafficEdgeUpdate> Updates,
        ValhallaTrafficWriteOptions Options,
        ValhallaTrafficWriteResult Result);
}

public sealed class TrafficAwareRouteSetPlannerTests
{
    [Fact]
    public Task PlanAsync_PerformsTwoPassesWithStableIdentities() =>
        new TrafficAwareRouteSetPlannerBehaviorTests()
            .PlanAsync_PerformsTwoPassesWithStableIdentities();

    [Fact]
    public async Task PlanAsync_ExplainsExcludedBaselineCandidateAndActiveFailure()
    {
        var behavior = new TrafficAwareRouteSetPlannerBehaviorTests();
        await behavior.PlanAsync_ExplainsExcludedBaselineCandidateAndActiveFailure();
        await behavior
            .PlanAsync_ExplainsMateriallyDeprioritizedCandidateThatRemainsPresent();
    }

    [Fact]
    public Task PlanAsync_AppliesClosureAndDelaySwitchPolicy() =>
        new TrafficAwareRouteSetPlannerBehaviorTests()
            .PlanAsync_AppliesClosureAndDelaySwitchPolicy();
}
