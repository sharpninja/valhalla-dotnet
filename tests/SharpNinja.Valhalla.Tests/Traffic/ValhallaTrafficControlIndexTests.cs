using SharpNinja.Valhalla.Traffic.Tiles;

namespace SharpNinja.Valhalla.Tests.Traffic;

public sealed class ValhallaTrafficControlIndexTests
{
    [Fact]
    public async Task BuildAsync_ExtractsTrafficSignalsStopSignsAndYieldSigns()
    {
        var source = new StubGraphSource(
        [
            new TrafficControlGraphEdge(
                DirectedEdgeId: 100,
                FromNodeId: 10,
                ToNodeId: 20,
                TrafficSignal: true,
                StopSign: true,
                YieldSign: true),
        ]);
        var index = new ValhallaTrafficControlIndex(source);

        ValhallaTrafficControlSnapshot snapshot = await index.BuildAsync(
            new ValhallaGraphTrafficContext("graph-a"),
            TestContext.Current.CancellationToken);

        Assert.Equal(3, snapshot.Controls.Count);
        ValhallaRouteTrafficControlCounts counts = snapshot.CountForRoute([100]);
        Assert.Equal(1, counts.TrafficSignalCount);
        Assert.Equal(1, counts.StopSignCount);
        Assert.Equal(1, counts.YieldSignCount);
    }

    [Fact]
    public async Task BuildAsync_CachesByTileSignature()
    {
        var source = new StubGraphSource([]);
        var index = new ValhallaTrafficControlIndex(source);

        ValhallaTrafficControlSnapshot first = await index.BuildAsync(
            new ValhallaGraphTrafficContext("graph-a"),
            TestContext.Current.CancellationToken);
        ValhallaTrafficControlSnapshot second = await index.BuildAsync(
            new ValhallaGraphTrafficContext("graph-a"),
            TestContext.Current.CancellationToken);

        Assert.Same(first, second);
        Assert.Equal(1, source.ReadCount);
    }

    [Fact]
    public async Task CountForRoute_UsesOrderedDirectedEdgeIdsWithoutGeometryProximity()
    {
        var source = new StubGraphSource(
        [
            new TrafficControlGraphEdge(100, 10, 20, false, true, false),
            new TrafficControlGraphEdge(101, 10, 20, false, true, false),
            new TrafficControlGraphEdge(102, 20, 10, false, true, false),
        ]);
        var index = new ValhallaTrafficControlIndex(source);
        ValhallaTrafficControlSnapshot snapshot = await index.BuildAsync(
            new ValhallaGraphTrafficContext("graph-a"),
            TestContext.Current.CancellationToken);

        ValhallaRouteTrafficControlCounts parallelCounts = snapshot.CountForRoute([100, 101]);
        ValhallaRouteTrafficControlCounts oppositeCounts = snapshot.CountForRoute([102]);
        ValhallaRouteTrafficControlCounts unrelatedCounts = snapshot.CountForRoute([999]);

        Assert.Equal(1, parallelCounts.StopSignCount);
        Assert.Equal(1, oppositeCounts.StopSignCount);
        Assert.Equal(0, unrelatedCounts.StopSignCount);
    }

    [Fact]
    public async Task BuildAsync_ChangedGraphSignatureRebuildsCache()
    {
        var source = new StubGraphSource([]);
        var index = new ValhallaTrafficControlIndex(source);

        _ = await index.BuildAsync(
            new ValhallaGraphTrafficContext("graph-a"),
            TestContext.Current.CancellationToken);
        _ = await index.BuildAsync(
            new ValhallaGraphTrafficContext("graph-b"),
            TestContext.Current.CancellationToken);

        Assert.Equal(2, source.ReadCount);
    }

    [Fact]
    public async Task GraphTileSource_ReadsControlFlagsFromRealValhallaTiles()
    {
        string tileDirectory = FindMonacoTileDirectory();
        var source = new GraphTileTrafficControlGraphSource();

        IReadOnlyList<TrafficControlGraphEdge> controls = await source.ReadAsync(
            new ValhallaGraphTrafficContext("monaco-fixture", tileDirectory),
            TestContext.Current.CancellationToken);

        Assert.NotEmpty(controls);
        Assert.All(
            controls,
            static control => Assert.True(
                control.TrafficSignal || control.StopSign || control.YieldSign));
        Assert.All(controls, static control => Assert.NotEqual(0UL, control.DirectedEdgeId));
    }

    [Fact]
    public async Task GraphTileSource_RequiresGraphTileDirectory()
    {
        var source = new GraphTileTrafficControlGraphSource();

        await Assert.ThrowsAsync<ArgumentException>(
            () => source.ReadAsync(
                new ValhallaGraphTrafficContext("graph-a"),
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task BuildAsync_FirstWaiterCancellationDoesNotPoisonSharedCache()
    {
        var source = new ControlledGraphSource();
        var index = new ValhallaTrafficControlIndex(source);
        var context = new ValhallaGraphTrafficContext("graph-shared");
        using var firstCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

        Task<ValhallaTrafficControlSnapshot> first =
            index.BuildAsync(context, firstCancellation.Token);
        await source.Started.Task.WaitAsync(TestContext.Current.CancellationToken);
        Task<ValhallaTrafficControlSnapshot> second =
            index.BuildAsync(context, TestContext.Current.CancellationToken);
        await firstCancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        source.Complete([]);
        ValhallaTrafficControlSnapshot completed = await second;
        ValhallaTrafficControlSnapshot cached = await index.BuildAsync(
            context,
            TestContext.Current.CancellationToken);

        Assert.Same(completed, cached);
        Assert.Equal(1, source.ReadCount);
    }

    [Fact]
    public async Task BuildAsync_LaterWaiterCanCancelWithoutCancelingSharedBuild()
    {
        var source = new ControlledGraphSource();
        var index = new ValhallaTrafficControlIndex(source);
        var context = new ValhallaGraphTrafficContext("graph-shared");
        Task<ValhallaTrafficControlSnapshot> owner =
            index.BuildAsync(context, TestContext.Current.CancellationToken);
        await source.Started.Task.WaitAsync(TestContext.Current.CancellationToken);
        using var waiterCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

        Task<ValhallaTrafficControlSnapshot> waiter =
            index.BuildAsync(context, waiterCancellation.Token);
        await waiterCancellation.CancelAsync();
        await Task.Delay(25, TestContext.Current.CancellationToken);

        Assert.True(waiter.IsCanceled);
        source.Complete([]);
        _ = await owner;
        Assert.Equal(1, source.ReadCount);
    }

    [Fact]
    public async Task BuildAsync_BoundedCacheEvictsLeastRecentlyUsedSignature()
    {
        var source = new StubGraphSource([]);
        using var index = new ValhallaTrafficControlIndex(source, maxCachedSignatures: 2);

        _ = await index.BuildAsync(
            new ValhallaGraphTrafficContext("graph-a"),
            TestContext.Current.CancellationToken);
        _ = await index.BuildAsync(
            new ValhallaGraphTrafficContext("graph-b"),
            TestContext.Current.CancellationToken);
        _ = await index.BuildAsync(
            new ValhallaGraphTrafficContext("graph-a"),
            TestContext.Current.CancellationToken);
        _ = await index.BuildAsync(
            new ValhallaGraphTrafficContext("graph-c"),
            TestContext.Current.CancellationToken);
        _ = await index.BuildAsync(
            new ValhallaGraphTrafficContext("graph-b"),
            TestContext.Current.CancellationToken);

        Assert.Equal(4, source.ReadCount);
    }

    [Fact]
    public async Task Invalidate_RemovesOnlyRequestedGraphSignature()
    {
        var source = new StubGraphSource([]);
        using var index = new ValhallaTrafficControlIndex(source, maxCachedSignatures: 2);
        var graphA = new ValhallaGraphTrafficContext("graph-a");
        var graphB = new ValhallaGraphTrafficContext("graph-b");

        _ = await index.BuildAsync(graphA, TestContext.Current.CancellationToken);
        ValhallaTrafficControlSnapshot graphBFirst = await index.BuildAsync(
            graphB,
            TestContext.Current.CancellationToken);

        Assert.True(index.Invalidate("graph-a"));

        _ = await index.BuildAsync(graphA, TestContext.Current.CancellationToken);
        ValhallaTrafficControlSnapshot graphBSecond = await index.BuildAsync(
            graphB,
            TestContext.Current.CancellationToken);

        Assert.Same(graphBFirst, graphBSecond);
        Assert.Equal(3, source.ReadCount);
    }

    [Fact]
    public async Task Clear_RemovesAllCachedGraphSignatures()
    {
        var source = new StubGraphSource([]);
        using var index = new ValhallaTrafficControlIndex(source, maxCachedSignatures: 2);
        var graphA = new ValhallaGraphTrafficContext("graph-a");
        var graphB = new ValhallaGraphTrafficContext("graph-b");

        _ = await index.BuildAsync(graphA, TestContext.Current.CancellationToken);
        _ = await index.BuildAsync(graphB, TestContext.Current.CancellationToken);

        index.Clear();

        _ = await index.BuildAsync(graphA, TestContext.Current.CancellationToken);
        _ = await index.BuildAsync(graphB, TestContext.Current.CancellationToken);

        Assert.Equal(4, source.ReadCount);
    }

    [Fact]
    public async Task Dispose_RejectsNewBuildButDoesNotCancelExistingWaiters()
    {
        var source = new ControlledGraphSource();
        var index = new ValhallaTrafficControlIndex(source, maxCachedSignatures: 1);
        var context = new ValhallaGraphTrafficContext("graph-shared");
        Task<ValhallaTrafficControlSnapshot> owner =
            index.BuildAsync(context, TestContext.Current.CancellationToken);
        await source.Started.Task.WaitAsync(TestContext.Current.CancellationToken);
        Task<ValhallaTrafficControlSnapshot> waiter =
            index.BuildAsync(context, TestContext.Current.CancellationToken);

        index.Dispose();
        source.Complete([]);

        ValhallaTrafficControlSnapshot ownerResult = await owner;
        ValhallaTrafficControlSnapshot waiterResult = await waiter;
        Assert.Same(ownerResult, waiterResult);
        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => index.BuildAsync(context, TestContext.Current.CancellationToken));
        Assert.Equal(1, source.ReadCount);
    }

    [Fact]
    public async Task Clear_DuringSharedBuild_DoesNotCancelExistingWaiters()
    {
        var source = new ControlledGraphSource();
        using var index = new ValhallaTrafficControlIndex(source, maxCachedSignatures: 1);
        var context = new ValhallaGraphTrafficContext("graph-shared");
        Task<ValhallaTrafficControlSnapshot> owner =
            index.BuildAsync(context, TestContext.Current.CancellationToken);
        await source.Started.Task.WaitAsync(TestContext.Current.CancellationToken);
        Task<ValhallaTrafficControlSnapshot> waiter =
            index.BuildAsync(context, TestContext.Current.CancellationToken);

        index.Clear();
        source.Complete([]);

        ValhallaTrafficControlSnapshot ownerResult = await owner;
        ValhallaTrafficControlSnapshot waiterResult = await waiter;
        Assert.Same(ownerResult, waiterResult);
        Assert.Equal(1, source.ReadCount);
    }

    private static string FindMonacoTileDirectory()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
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

        throw new DirectoryNotFoundException(
            "Tracked Monaco graph tile fixture was not found.");
    }

    private sealed class ControlledGraphSource : IValhallaTrafficControlGraphSource
    {
        private readonly TaskCompletionSource<IReadOnlyList<TrafficControlGraphEdge>> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int ReadCount { get; private set; }

        public Task<IReadOnlyList<TrafficControlGraphEdge>> ReadAsync(
            ValhallaGraphTrafficContext context,
            CancellationToken cancellationToken)
        {
            ReadCount++;
            Started.TrySetResult();
            return _completion.Task.WaitAsync(cancellationToken);
        }

        public void Complete(IReadOnlyList<TrafficControlGraphEdge> edges)
            => _completion.TrySetResult(edges);
    }

    private sealed class StubGraphSource(IReadOnlyList<TrafficControlGraphEdge> edges)
        : IValhallaTrafficControlGraphSource
    {
        public int ReadCount { get; private set; }

        public Task<IReadOnlyList<TrafficControlGraphEdge>> ReadAsync(
            ValhallaGraphTrafficContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReadCount++;
            return Task.FromResult(edges);
        }
    }
}
