namespace SharpNinja.Valhalla.Traffic.Tiles;

[Flags]
public enum ValhallaTrafficControlKind
{
    None = 0,
    TrafficSignal = 1,
    StopSign = 2,
    YieldSign = 4,
}

public sealed record TrafficControlGraphEdge(
    ulong DirectedEdgeId,
    ulong FromNodeId,
    ulong ToNodeId,
    bool TrafficSignal,
    bool StopSign,
    bool YieldSign);

public interface IValhallaTrafficControlGraphSource
{
    Task<IReadOnlyList<TrafficControlGraphEdge>> ReadAsync(
        ValhallaGraphTrafficContext context,
        CancellationToken cancellationToken);
}

public interface IValhallaTrafficControlIndex : IDisposable
{
    Task<ValhallaTrafficControlSnapshot> BuildAsync(
        ValhallaGraphTrafficContext context,
        CancellationToken cancellationToken);

    bool Invalidate(string graphSignature);

    void Clear();
}

public sealed class ValhallaTrafficControlIndex : IValhallaTrafficControlIndex
{
    public const int DefaultMaxCachedSignatures = 8;

    private readonly IValhallaTrafficControlGraphSource _graphSource;
    private readonly int _maxCachedSignatures;
    private readonly object _sync = new();
    private readonly Dictionary<string, CacheEntry> _cache = new(StringComparer.Ordinal);
    private readonly LinkedList<string> _leastRecentlyUsed = new();
    private bool _disposed;

    public ValhallaTrafficControlIndex(
        IValhallaTrafficControlGraphSource graphSource,
        int maxCachedSignatures = DefaultMaxCachedSignatures)
    {
        _graphSource = graphSource ?? throw new ArgumentNullException(nameof(graphSource));
        ArgumentOutOfRangeException.ThrowIfLessThan(maxCachedSignatures, 1);
        _maxCachedSignatures = maxCachedSignatures;
    }

    public async Task<ValhallaTrafficControlSnapshot> BuildAsync(
        ValhallaGraphTrafficContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        CacheEntry entry;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_cache.TryGetValue(context.GraphSignature, out CacheEntry? cached))
            {
                entry = cached;
                TouchUnsafe(entry);
            }
            else
            {
                var node = new LinkedListNode<string>(context.GraphSignature);
                entry = new CacheEntry(
                    new Lazy<Task<ValhallaTrafficControlSnapshot>>(
                        () => BuildCoreAsync(context, CancellationToken.None),
                        LazyThreadSafetyMode.ExecutionAndPublication),
                    node);
                _cache.Add(context.GraphSignature, entry);
                _leastRecentlyUsed.AddLast(node);
                TrimUnsafe();
            }
        }

        Task<ValhallaTrafficControlSnapshot> build = entry.Build.Value;
        try
        {
            return await build.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch when (build.IsFaulted || build.IsCanceled)
        {
            RemoveIfSame(context.GraphSignature, entry);
            throw;
        }
    }

    public bool Invalidate(string graphSignature)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(graphSignature);
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return RemoveUnsafe(graphSignature);
        }
    }

    public void Clear()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _cache.Clear();
            _leastRecentlyUsed.Clear();
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _cache.Clear();
            _leastRecentlyUsed.Clear();
        }
    }

    private async Task<ValhallaTrafficControlSnapshot> BuildCoreAsync(
        ValhallaGraphTrafficContext context,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<TrafficControlGraphEdge> edges = await _graphSource
            .ReadAsync(context, cancellationToken)
            .ConfigureAwait(false);
        var controls = new Dictionary<PhysicalControlKey, HashSet<ulong>>();
        foreach (TrafficControlGraphEdge edge in edges)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Add(edge, ValhallaTrafficControlKind.TrafficSignal, edge.TrafficSignal);
            Add(edge, ValhallaTrafficControlKind.StopSign, edge.StopSign);
            Add(edge, ValhallaTrafficControlKind.YieldSign, edge.YieldSign);
        }

        ValhallaTrafficControl[] result = controls
            .OrderBy(static pair => pair.Key.FromNodeId)
            .ThenBy(static pair => pair.Key.ToNodeId)
            .ThenBy(static pair => pair.Key.Kind)
            .Select(static pair => new ValhallaTrafficControl(
                pair.Key.Kind,
                pair.Key.FromNodeId,
                pair.Key.ToNodeId,
                pair.Value.Order().ToArray()))
            .ToArray();
        return new ValhallaTrafficControlSnapshot(context.GraphSignature, result);

        void Add(
            TrafficControlGraphEdge edge,
            ValhallaTrafficControlKind kind,
            bool present)
        {
            if (!present)
            {
                return;
            }

            var key = new PhysicalControlKey(edge.FromNodeId, edge.ToNodeId, kind);
            if (!controls.TryGetValue(key, out HashSet<ulong>? approaches))
            {
                approaches = [];
                controls.Add(key, approaches);
            }

            approaches.Add(edge.DirectedEdgeId);
        }
    }

    private void TouchUnsafe(CacheEntry entry)
    {
        _leastRecentlyUsed.Remove(entry.Node);
        _leastRecentlyUsed.AddLast(entry.Node);
    }

    private void TrimUnsafe()
    {
        while (_cache.Count > _maxCachedSignatures)
        {
            LinkedListNode<string>? oldest = _leastRecentlyUsed.First;
            if (oldest is null)
            {
                return;
            }

            RemoveUnsafe(oldest.Value);
        }
    }

    private bool RemoveUnsafe(string graphSignature)
    {
        if (!_cache.Remove(graphSignature, out CacheEntry? entry))
        {
            return false;
        }

        _leastRecentlyUsed.Remove(entry.Node);
        return true;
    }

    private void RemoveIfSame(string graphSignature, CacheEntry entry)
    {
        lock (_sync)
        {
            if (_cache.TryGetValue(graphSignature, out CacheEntry? cached)
                && ReferenceEquals(cached, entry))
            {
                RemoveUnsafe(graphSignature);
            }
        }
    }

    private sealed record CacheEntry(
        Lazy<Task<ValhallaTrafficControlSnapshot>> Build,
        LinkedListNode<string> Node);

    private readonly record struct PhysicalControlKey(
        ulong FromNodeId,
        ulong ToNodeId,
        ValhallaTrafficControlKind Kind);
}

public sealed record ValhallaTrafficControl
{
    public ValhallaTrafficControl(
        ValhallaTrafficControlKind kind,
        ulong fromNodeId,
        ulong toNodeId,
        IReadOnlyList<ulong> approachDirectedEdgeIds)
    {
        ArgumentNullException.ThrowIfNull(approachDirectedEdgeIds);
        Kind = kind;
        FromNodeId = fromNodeId;
        ToNodeId = toNodeId;
        ApproachDirectedEdgeIds = Array.AsReadOnly(approachDirectedEdgeIds.Distinct().ToArray());
    }

    public ValhallaTrafficControlKind Kind { get; }
    public ulong FromNodeId { get; }
    public ulong ToNodeId { get; }
    public IReadOnlyList<ulong> ApproachDirectedEdgeIds { get; }
}

public sealed record ValhallaTrafficControlSnapshot
{
    public ValhallaTrafficControlSnapshot(
        string graphSignature,
        IReadOnlyList<ValhallaTrafficControl> controls)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(graphSignature);
        ArgumentNullException.ThrowIfNull(controls);
        GraphSignature = graphSignature;
        Controls = Array.AsReadOnly(controls.ToArray());
    }

    public string GraphSignature { get; }
    public IReadOnlyList<ValhallaTrafficControl> Controls { get; }

    public ValhallaRouteTrafficControlCounts CountForRoute(
        IReadOnlyList<ulong> orderedDirectedEdgeIds)
    {
        ArgumentNullException.ThrowIfNull(orderedDirectedEdgeIds);
        var routeOrder = new Dictionary<ulong, int>();
        for (int index = 0; index < orderedDirectedEdgeIds.Count; index++)
        {
            routeOrder.TryAdd(orderedDirectedEdgeIds[index], index);
        }

        ValhallaTrafficControl[] matched = Controls
            .Select(control => new
            {
                Control = control,
                RouteIndex = control.ApproachDirectedEdgeIds
                    .Where(routeOrder.ContainsKey)
                    .Select(edgeId => routeOrder[edgeId])
                    .DefaultIfEmpty(int.MaxValue)
                    .Min(),
            })
            .Where(static item => item.RouteIndex != int.MaxValue)
            .OrderBy(static item => item.RouteIndex)
            .ThenBy(static item => item.Control.FromNodeId)
            .ThenBy(static item => item.Control.ToNodeId)
            .Select(static item => item.Control)
            .ToArray();

        return new ValhallaRouteTrafficControlCounts(
            matched.Count(static control => control.Kind == ValhallaTrafficControlKind.TrafficSignal),
            matched.Count(static control => control.Kind == ValhallaTrafficControlKind.StopSign),
            matched.Count(static control => control.Kind == ValhallaTrafficControlKind.YieldSign),
            matched);
    }
}

public sealed record ValhallaRouteTrafficControlCounts(
    int TrafficSignalCount,
    int StopSignCount,
    int YieldSignCount,
    IReadOnlyList<ValhallaTrafficControl> Controls);
