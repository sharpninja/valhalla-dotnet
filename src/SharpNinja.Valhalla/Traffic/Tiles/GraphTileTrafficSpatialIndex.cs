using System.Collections.Concurrent;
using System.Globalization;
using SharpNinja.Valhalla.Baldr;
using SharpNinja.Valhalla.Midgard;

namespace SharpNinja.Valhalla.Traffic.Tiles;

/// <summary>
/// Reads Valhalla graph-tile directed-edge shapes and matches normalized traffic geometry without
/// introducing UI dependencies. Query-tile snapshots are shared by exact graph signature. Cancellation
/// is observed before and between synchronous tile reads and throughout index construction/matching.
/// </summary>
public sealed class GraphTileTrafficSpatialIndex :
    IValhallaTrafficSpatialIndex,
    IDisposable
{
    /// <summary>Default maximum separation between provider geometry and a graph edge.</summary>
    public const double DefaultMatchToleranceMeters = 8d;

    /// <summary>Default maximum heading delta used to resolve directed line geometry.</summary>
    public const double DefaultDirectionToleranceDegrees = 45d;

    /// <summary>Maximum number of query-tile snapshots retained by one index instance.</summary>
    public const int MaximumCachedSnapshots = 32;

    /// <summary>Maximum graph tiles retained by any single cached snapshot.</summary>
    public const int MaximumTilesPerSnapshot = 64;

    /// <summary>Maximum canonical graph tiles that one provider geometry may query.</summary>
    public const int MaximumTilesPerQuery =
        MaximumTilesPerSnapshot * MaximumCachedSnapshots;

    /// <summary>Maximum provider coordinates accepted by one spatial query.</summary>
    public const int MaximumGeometryPointsPerQuery = 16_384;

    private const double MinimumDirectionResolvedOverlapMeters = 20d;
    private const double MaximumSequentialCoverageOverlapMeters = 1d;

    private readonly IValhallaTrafficSpatialGraphSource _graphSource;
    private readonly double _matchToleranceMeters;
    private readonly double _directionToleranceDegrees;
    private readonly Action? _indexBuildStarted;
    private readonly Action? _cacheAdmissionAcquired;
    private readonly Action? _cacheEntryPublished;
    private readonly ConcurrentDictionary<TrafficSpatialCacheKey, TrafficSpatialBuildEntry> _cache =
        new();
    private readonly SemaphoreSlim _cacheAdmission =
        new(MaximumCachedSnapshots, MaximumCachedSnapshots);
    private readonly SemaphoreSlim _cacheStateChanged = new(0, int.MaxValue);
    private readonly SemaphoreSlim _buildAdmission =
        new(MaximumCachedSnapshots, MaximumCachedSnapshots);
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private long _accessSequence;
    private int _activeOperations;
    private int _pendingBuilds;
    private int _resourcesDisposed;
    private int _disposeSweepInProgress;
    private int _disposed;

    /// <summary>
    /// Creates a graph-tile spatial index with a narrow, configurable match tolerance.
    /// </summary>
    public GraphTileTrafficSpatialIndex(
        double matchToleranceMeters = DefaultMatchToleranceMeters,
        double directionToleranceDegrees = DefaultDirectionToleranceDegrees)
        : this(
            new GraphTileTrafficSpatialGraphSource(),
            matchToleranceMeters,
            directionToleranceDegrees)
    {
    }

    internal GraphTileTrafficSpatialIndex(
        IValhallaTrafficSpatialGraphSource graphSource,
        double matchToleranceMeters = DefaultMatchToleranceMeters,
        double directionToleranceDegrees = DefaultDirectionToleranceDegrees,
        Action? indexBuildStarted = null,
        Action? cacheAdmissionAcquired = null,
        Action? cacheEntryPublished = null)
    {
        _graphSource = graphSource ?? throw new ArgumentNullException(nameof(graphSource));
        if (!double.IsFinite(matchToleranceMeters) ||
            matchToleranceMeters <= 0 ||
            matchToleranceMeters > 1_000)
        {
            throw new ArgumentOutOfRangeException(
                nameof(matchToleranceMeters),
                "Match tolerance must be greater than zero and no more than 1,000 meters.");
        }

        if (!double.IsFinite(directionToleranceDegrees) ||
            directionToleranceDegrees <= 0 ||
            directionToleranceDegrees > 90)
        {
            throw new ArgumentOutOfRangeException(
                nameof(directionToleranceDegrees),
                "Direction tolerance must be greater than zero and no more than 90 degrees.");
        }

        _matchToleranceMeters = matchToleranceMeters;
        _directionToleranceDegrees = directionToleranceDegrees;
        _indexBuildStarted = indexBuildStarted;
        _cacheAdmissionAcquired = cacheAdmissionAcquired;
        _cacheEntryPublished = cacheEntryPublished;
    }

    internal int CachedSnapshotCount => _cache.Count;

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<TrafficEdgeMatchCandidate>> MatchAsync(
        TrafficGeometry geometry,
        ValhallaGraphTrafficContext context,
        CancellationToken cancellationToken)
    {
        EnterOperation();
        try
        {
            ArgumentNullException.ThrowIfNull(geometry);
            ArgumentNullException.ThrowIfNull(context);
            cancellationToken.ThrowIfCancellationRequested();
            if (geometry.Points.Count == 0)
            {
                return Array.Empty<TrafficEdgeMatchCandidate>();
            }

            TrafficSpatialQuery query =
                TrafficSpatialQuery.Create(
                    geometry.Points,
                    _matchToleranceMeters,
                    cancellationToken);
            TrafficSpatialSnapshot snapshot = await GetCombinedSnapshotAsync(
                    context,
                    query,
                    cancellationToken)
                .ConfigureAwait(false);
            return await Task.Run<IReadOnlyList<TrafficEdgeMatchCandidate>>(
                    () => MatchCore(snapshot, geometry, cancellationToken),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            ExitOperation();
        }
    }

    private void EnterOperation()
    {
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0,
            this);
        Interlocked.Increment(ref _activeOperations);
        if (Volatile.Read(ref _disposed) == 0)
        {
            return;
        }

        ExitOperation();
        throw new ObjectDisposedException(GetType().FullName);
    }

    private void ExitOperation()
    {
        if (Interlocked.Decrement(ref _activeOperations) == 0)
        {
            TryDisposeResources();
        }
    }

    private void TryDisposeResources()
    {
        if (Volatile.Read(ref _disposed) == 0 ||
            Volatile.Read(ref _disposeSweepInProgress) != 0 ||
            Volatile.Read(ref _activeOperations) != 0 ||
            Volatile.Read(ref _pendingBuilds) != 0 ||
            Interlocked.CompareExchange(ref _resourcesDisposed, 1, 0) != 0)
        {
            return;
        }

        _cacheAdmission.Dispose();
        _cacheStateChanged.Dispose();
        _buildAdmission.Dispose();
        _lifetimeCancellation.Dispose();
    }

    /// <summary>Removes cached query-tile snapshots for one exact graph signature.</summary>
    public void Invalidate(string graphSignature)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(graphSignature);
        foreach (KeyValuePair<TrafficSpatialCacheKey, TrafficSpatialBuildEntry> pair in _cache)
        {
            if (StringComparer.Ordinal.Equals(pair.Key.GraphSignature, graphSignature))
            {
                TryRemoveCacheEntry(pair.Key, pair.Value);
            }
        }
    }

    /// <summary>Removes every cached query-tile snapshot.</summary>
    public void Clear()
    {
        foreach (KeyValuePair<TrafficSpatialCacheKey, TrafficSpatialBuildEntry> pair in _cache)
        {
            TryRemoveCacheEntry(pair.Key, pair.Value);
        }

    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            Volatile.Write(ref _disposeSweepInProgress, 1);
            try
            {
                _lifetimeCancellation.Cancel();
                Clear();
            }
            finally
            {
                Volatile.Write(ref _disposeSweepInProgress, 0);
                TryDisposeResources();
            }
        }
    }

    private async Task<TrafficSpatialSnapshot> GetCombinedSnapshotAsync(
        ValhallaGraphTrafficContext context,
        TrafficSpatialQuery query,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<TrafficSpatialQuery> batches =
            query.Split(MaximumTilesPerSnapshot);
        if (batches.Count == 1)
        {
            return await GetSnapshotAsync(
                    context,
                    batches[0],
                    cancellationToken)
                .ConfigureAwait(false);
        }

        var edges = new Dictionary<ulong, TrafficSpatialGraphEdge>();
        foreach (TrafficSpatialQuery batch in batches)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TrafficSpatialSnapshot snapshot = await GetSnapshotAsync(
                    context,
                    batch,
                    cancellationToken)
                .ConfigureAwait(false);
            foreach (TrafficSpatialGraphEdge edge in snapshot.Edges)
            {
                edges.TryAdd(edge.CanonicalDirectedEdgeId, edge);
            }
        }

        TrafficSpatialGraphEdge[] combinedEdges = edges.Values
            .OrderBy(static edge => edge.CanonicalDirectedEdgeId)
            .ToArray();
        return await Task.Run(
                () => TrafficSpatialSnapshot.Create(
                    Array.AsReadOnly(combinedEdges),
                    _matchToleranceMeters,
                    cancellationToken),
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<TrafficSpatialSnapshot> GetSnapshotAsync(
        ValhallaGraphTrafficContext context,
        TrafficSpatialQuery query,
        CancellationToken cancellationToken)
    {
        var key = new TrafficSpatialCacheKey(context.GraphSignature, query.Key);
        TrafficSpatialBuildEntry entry;
        while (true)
        {
            ObjectDisposedException.ThrowIf(
                Volatile.Read(ref _disposed) != 0,
                this);
            cancellationToken.ThrowIfCancellationRequested();

            if (_cache.TryGetValue(key, out entry!))
            {
                if (entry.TryAcquireWaiter(NextAccessSequence()))
                {
                    break;
                }

                TryRemoveCacheEntry(key, entry);
                continue;
            }

            if (!_cacheAdmission.Wait(0))
            {
                if (TryEvictCompletedEntry())
                {
                    continue;
                }

                await WaitForCacheStateChangeAsync(cancellationToken)
                    .ConfigureAwait(false);
                continue;
            }

            _cacheAdmissionAcquired?.Invoke();
            if (Volatile.Read(ref _disposed) != 0)
            {
                _cacheAdmission.Release();
                SignalCacheStateChanged();
                throw new ObjectDisposedException(GetType().FullName);
            }

            var candidate = new TrafficSpatialBuildEntry(
                token => BuildWithAdmissionAsync(context, query, token));
            Interlocked.Increment(ref _pendingBuilds);
            if (!_cache.TryAdd(key, candidate))
            {
                Interlocked.Decrement(ref _pendingBuilds);
                _cacheAdmission.Release();
                SignalCacheStateChanged();
                TryDisposeResources();
                continue;
            }

            entry = candidate;
            _cacheEntryPublished?.Invoke();
            _ = entry.Build.ContinueWith(
                static (_, state) =>
                    ((GraphTileTrafficSpatialIndex)state!).OnBuildCompleted(),
                this,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            if (Volatile.Read(ref _disposed) != 0)
            {
                TryRemoveCacheEntry(key, entry);
                throw new ObjectDisposedException(GetType().FullName);
            }

            if (!entry.TryAcquireWaiter(NextAccessSequence()))
            {
                TryRemoveCacheEntry(key, entry);
                continue;
            }

            SignalCacheStateChanged();
            break;
        }

        Task<TrafficSpatialSnapshot> build = entry.Build;
        try
        {
            return await build.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch when (build.IsFaulted || build.IsCanceled)
        {
            TryRemoveCacheEntry(key, entry);
            throw;
        }
        finally
        {
            if (entry.ReleaseWaiterAndShouldCancel())
            {
                TryRemoveCacheEntry(key, entry);
            }
        }
    }

    private async Task WaitForCacheStateChangeAsync(
        CancellationToken cancellationToken)
    {
        using var linkedCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                _lifetimeCancellation.Token);
        try
        {
            await _cacheStateChanged
                .WaitAsync(linkedCancellation.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
            when (_lifetimeCancellation.IsCancellationRequested &&
                  !cancellationToken.IsCancellationRequested)
        {
            throw new ObjectDisposedException(GetType().FullName);
        }
    }

    private void OnBuildCompleted()
    {
        SignalCacheStateChanged();
        if (Interlocked.Decrement(ref _pendingBuilds) == 0)
        {
            TryDisposeResources();
        }
    }

    private async Task<TrafficSpatialSnapshot> BuildWithAdmissionAsync(
        ValhallaGraphTrafficContext context,
        TrafficSpatialQuery query,
        CancellationToken cancellationToken)
    {
        using var linkedCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                _lifetimeCancellation.Token);
        await _buildAdmission
            .WaitAsync(linkedCancellation.Token)
            .ConfigureAwait(false);
        try
        {
            return await BuildSnapshotAsync(
                    context,
                    query,
                    linkedCancellation.Token)
                .ConfigureAwait(false);
        }
        finally
        {
            _buildAdmission.Release();
        }
    }

    private long NextAccessSequence()
        => Interlocked.Increment(ref _accessSequence);

    private bool TryEvictCompletedEntry()
    {
        var candidates = _cache
            .Select(static pair => new
            {
                pair.Key,
                Entry = pair.Value,
                AccessSequence = pair.Value.LastAccessSequence,
            })
            .OrderBy(static candidate => candidate.AccessSequence)
            .ThenBy(static candidate => candidate.Key.GraphSignature, StringComparer.Ordinal)
            .ThenBy(static candidate => candidate.Key.QueryKey, StringComparer.Ordinal)
            .ToArray();
        foreach (var candidate in candidates)
        {
            if (candidate.Entry.TryPrepareForEviction(candidate.AccessSequence)
                && TryRemoveCacheEntry(candidate.Key, candidate.Entry))
            {
                return true;
            }
        }

        return false;
    }

    private bool TryRemoveCacheEntry(
        TrafficSpatialCacheKey key,
        TrafficSpatialBuildEntry entry)
    {
        if (!_cache.TryRemove(
                new KeyValuePair<TrafficSpatialCacheKey, TrafficSpatialBuildEntry>(
                    key,
                    entry)))
        {
            return false;
        }

        entry.Cancel();
        _cacheAdmission.Release();
        SignalCacheStateChanged();
        return true;
    }

    private void SignalCacheStateChanged()
        => _cacheStateChanged.Release();

    private async Task<TrafficSpatialSnapshot> BuildSnapshotAsync(
        ValhallaGraphTrafficContext context,
        TrafficSpatialQuery query,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<TrafficSpatialGraphEdge> edges = await _graphSource
            .ReadAsync(context, query, cancellationToken)
            .ConfigureAwait(false);
        return await Task.Run(
                () =>
                {
                    _indexBuildStarted?.Invoke();
                    return TrafficSpatialSnapshot.Create(
                        edges,
                        _matchToleranceMeters,
                        cancellationToken);
                },
                cancellationToken)
            .ConfigureAwait(false);
    }


    private IReadOnlyList<TrafficEdgeMatchCandidate> MatchCore(
        TrafficSpatialSnapshot snapshot,
        TrafficGeometry geometry,
        CancellationToken cancellationToken)
    {
        if (geometry.Points.Count == 0)
        {
            return Array.Empty<TrafficEdgeMatchCandidate>();
        }

        IReadOnlyList<int> candidateIndexes = snapshot.Query(
            geometry.Points,
            _matchToleranceMeters);
        if (candidateIndexes.Count == 0)
        {
            return Array.Empty<TrafficEdgeMatchCandidate>();
        }

        bool isLineGeometry =
            geometry.Kind == TrafficGeometryKind.LineString &&
            HasUsableSegment(geometry.Points);
        bool directionSemanticallyKnown =
            isLineGeometry &&
            geometry.Direction != TrafficGeometryDirection.Unknown;
        var matches = new List<TrafficEdgeMatchCandidate>();
        var lineMatches = new Dictionary<ulong, LineGeometryMatch>();
        var lineEdges = new Dictionary<ulong, TrafficSpatialGraphEdge>();
        foreach (int edgeIndex in candidateIndexes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TrafficSpatialGraphEdge edge = snapshot.Edges[edgeIndex];
            bool candidateDirectionResolved = false;
            double distance;
            if (isLineGeometry)
            {
                LineGeometryMatch? lineMatch = MatchLineGeometry(
                    geometry.Points,
                    edge.Shape,
                    geometry.Direction,
                    _matchToleranceMeters,
                    _directionToleranceDegrees,
                    cancellationToken);
                if (lineMatch is null)
                {
                    continue;
                }

                distance = lineMatch.Value.DistanceMeters;
                lineMatches.Add(edge.CanonicalDirectedEdgeId, lineMatch.Value);
                lineEdges.Add(edge.CanonicalDirectedEdgeId, edge);
                candidateDirectionResolved =
                    directionSemanticallyKnown &&
                    lineMatch.Value.MatchedLengthMeters + 0.01d >=
                    MinimumDirectionResolvedOverlapMeters;
            }
            else
            {
                distance = PointGeometryDistanceMeters(geometry.Points, edge.Shape);
            }

            if (!double.IsFinite(distance) || distance > _matchToleranceMeters)
            {
                continue;
            }

            matches.Add(new TrafficEdgeMatchCandidate(
                new ValhallaTrafficEdgeReference(
                    edge.TileId,
                    edge.DirectedEdgeIndex,
                    edge.CanonicalDirectedEdgeId),
                edge.Direction,
                distance,
                candidateDirectionResolved));
        }

        if (!isLineGeometry && matches.Count > 1)
        {
            double nearestDistance = matches.Min(static match => match.DistanceMeters);
            matches.RemoveAll(match => match.DistanceMeters > nearestDistance + 1d);
        }
        else if (isLineGeometry && matches.Count > 1)
        {
            IReadOnlyDictionary<int, double> nearestByProviderSegment = matches
                .GroupBy(match =>
                    lineMatches[match.Edge.CanonicalDirectedEdgeId].ProviderSegmentIndex)
                .ToDictionary(
                    static group => group.Key,
                    static group => group.Min(match => match.DistanceMeters));
            matches.RemoveAll(match =>
                match.DistanceMeters >
                nearestByProviderSegment[
                    lineMatches[match.Edge.CanonicalDirectedEdgeId].ProviderSegmentIndex] + 1d);

            var ambiguousEdgeIds = new HashSet<ulong>();
            for (int leftIndex = 0; leftIndex < matches.Count; leftIndex++)
            {
                LineGeometryMatch left =
                    lineMatches[matches[leftIndex].Edge.CanonicalDirectedEdgeId];
                for (int rightIndex = leftIndex + 1; rightIndex < matches.Count; rightIndex++)
                {
                    LineGeometryMatch right =
                        lineMatches[matches[rightIndex].Edge.CanonicalDirectedEdgeId];
                    if (matches[leftIndex].Direction != matches[rightIndex].Direction ||
                        Math.Abs(
                            matches[leftIndex].DistanceMeters -
                            matches[rightIndex].DistanceMeters) > 1d ||
                        AreSequentialEdges(
                            lineEdges[matches[leftIndex].Edge.CanonicalDirectedEdgeId],
                            lineEdges[matches[rightIndex].Edge.CanonicalDirectedEdgeId]))
                    {
                        continue;
                    }

                    double overlappingProviderCoverageMeters =
                        Math.Min(left.ProviderEndMeters, right.ProviderEndMeters) -
                        Math.Max(left.ProviderStartMeters, right.ProviderStartMeters);
                    if (overlappingProviderCoverageMeters >
                        MaximumSequentialCoverageOverlapMeters)
                    {
                        ambiguousEdgeIds.Add(
                            matches[leftIndex].Edge.CanonicalDirectedEdgeId);
                        ambiguousEdgeIds.Add(
                            matches[rightIndex].Edge.CanonicalDirectedEdgeId);
                    }
                }
            }

            for (int matchIndex = 0; matchIndex < matches.Count; matchIndex++)
            {
                TrafficEdgeMatchCandidate match = matches[matchIndex];
                if (ambiguousEdgeIds.Contains(match.Edge.CanonicalDirectedEdgeId))
                {
                    matches[matchIndex] = match with
                    {
                        DirectionResolved = false,
                    };
                }
            }
        }

        matches.Sort(static (left, right) =>
        {
            int distanceComparison = left.DistanceMeters.CompareTo(right.DistanceMeters);
            return distanceComparison != 0
                ? distanceComparison
                : left.Edge.CanonicalDirectedEdgeId.CompareTo(
                    right.Edge.CanonicalDirectedEdgeId);
        });
        return Array.AsReadOnly(matches.ToArray());
    }

    private static bool HasUsableSegment(IReadOnlyList<GeoCoordinate> points)
    {
        for (int index = 0; index + 1 < points.Count; index++)
        {
            if (HaversineMeters(points[index], points[index + 1]) > 0.05d)
            {
                return true;
            }
        }

        return false;
    }

    private static double PointGeometryDistanceMeters(
        IReadOnlyList<GeoCoordinate> providerPoints,
        IReadOnlyList<GeoCoordinate> edgeShape)
    {
        double minimum = double.PositiveInfinity;
        foreach (GeoCoordinate point in providerPoints)
        {
            for (int edgeIndex = 0; edgeIndex + 1 < edgeShape.Count; edgeIndex++)
            {
                minimum = Math.Min(
                    minimum,
                    PointToSegmentDistanceMeters(
                        point,
                        edgeShape[edgeIndex],
                        edgeShape[edgeIndex + 1]));
            }
        }

        return minimum;
    }

    private static bool AreSequentialEdges(
        TrafficSpatialGraphEdge left,
        TrafficSpatialGraphEdge right)
        => left.EndNodeId.HasValue
           && right.StartNodeId.HasValue
           && left.EndNodeId.Value == right.StartNodeId.Value
           || right.EndNodeId.HasValue
           && left.StartNodeId.HasValue
           && right.EndNodeId.Value == left.StartNodeId.Value;

    private static LineGeometryMatch? MatchLineGeometry(
        IReadOnlyList<GeoCoordinate> providerShape,
        IReadOnlyList<GeoCoordinate> edgeShape,
        TrafficGeometryDirection direction,
        double matchToleranceMeters,
        double directionToleranceDegrees,
        CancellationToken cancellationToken)
    {
        double providerLength = PolylineLengthMeters(providerShape);
        double edgeLength = PolylineLengthMeters(edgeShape);
        if (providerLength <= 0.05d || edgeLength <= 0.05d)
        {
            return null;
        }

        var providerSegmentStarts = new double[providerShape.Count - 1];
        var providerSegmentLengths = new double[providerShape.Count - 1];
        double providerPosition = 0d;
        for (int providerIndex = 0;
             providerIndex + 1 < providerShape.Count;
             providerIndex++)
        {
            providerSegmentStarts[providerIndex] = providerPosition;
            double segmentLength =
                HaversineMeters(providerShape[providerIndex], providerShape[providerIndex + 1]);
            providerSegmentLengths[providerIndex] = segmentLength;
            providerPosition += segmentLength;
        }

        double sampleLengthMeters = Math.Max(
            0.25d,
            Math.Min(
                Math.Max(2d, matchToleranceMeters / 2d),
                providerLength / 2d));
        double matchedLengthMeters = 0d;
        double minimumDistanceMeters = double.PositiveInfinity;
        double providerStartMeters = double.PositiveInfinity;
        double providerEndMeters = double.NegativeInfinity;
        int closestProviderSegment = -1;
        for (int edgeIndex = 0; edgeIndex + 1 < edgeShape.Count; edgeIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            GeoCoordinate edgeStart = edgeShape[edgeIndex];
            GeoCoordinate edgeEnd = edgeShape[edgeIndex + 1];
            double edgeSegmentLength = HaversineMeters(edgeStart, edgeEnd);
            if (edgeSegmentLength <= 0.05d)
            {
                continue;
            }

            int sampleCount = Math.Max(
                1,
                (int)Math.Ceiling(edgeSegmentLength / sampleLengthMeters));
            double sampleContribution = edgeSegmentLength / sampleCount;
            double edgeHeading = HeadingDegrees(edgeStart, edgeEnd);
            for (int sampleIndex = 0; sampleIndex < sampleCount; sampleIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                double fraction = (sampleIndex + 0.5d) / sampleCount;
                GeoCoordinate sample = Interpolate(edgeStart, edgeEnd, fraction);
                (double Distance, int ProviderSegment, double ProjectionFraction) nearest =
                    FindNearestProviderSegment(
                        sample,
                        edgeHeading,
                        providerShape,
                        direction,
                        directionToleranceDegrees);
                if (nearest.ProviderSegment < 0 ||
                    nearest.Distance > matchToleranceMeters)
                {
                    continue;
                }

                matchedLengthMeters += sampleContribution;
                double matchedProviderPosition =
                    providerSegmentStarts[nearest.ProviderSegment] +
                    (providerSegmentLengths[nearest.ProviderSegment] *
                     nearest.ProjectionFraction);
                double halfContribution = sampleContribution / 2d;
                providerStartMeters = Math.Min(
                    providerStartMeters,
                    Math.Max(0d, matchedProviderPosition - halfContribution));
                providerEndMeters = Math.Max(
                    providerEndMeters,
                    Math.Min(providerLength, matchedProviderPosition + halfContribution));
                if (nearest.Distance < minimumDistanceMeters)
                {
                    minimumDistanceMeters = nearest.Distance;
                    closestProviderSegment = nearest.ProviderSegment;
                }
            }
        }

        matchedLengthMeters = Math.Min(matchedLengthMeters, providerLength);
        double requiredOverlapMeters =
            Math.Min(providerLength, edgeLength) * 0.5d;
        if (closestProviderSegment < 0 ||
            matchedLengthMeters + 0.01d < requiredOverlapMeters)
        {
            return null;
        }

        return new LineGeometryMatch(
            minimumDistanceMeters,
            closestProviderSegment,
            matchedLengthMeters,
            providerStartMeters,
            providerEndMeters);
    }

    private static (
        double Distance,
        int ProviderSegment,
        double ProjectionFraction) FindNearestProviderSegment(
        GeoCoordinate sample,
        double edgeHeading,
        IReadOnlyList<GeoCoordinate> providerShape,
        TrafficGeometryDirection direction,
        double directionToleranceDegrees)
    {
        double minimum = double.PositiveInfinity;
        int nearestProviderSegment = -1;
        double nearestProjectionFraction = double.NaN;
        for (int providerIndex = 0;
             providerIndex + 1 < providerShape.Count;
             providerIndex++)
        {
            GeoCoordinate providerStart = providerShape[providerIndex];
            GeoCoordinate providerEnd = providerShape[providerIndex + 1];
            if (HaversineMeters(providerStart, providerEnd) <= 0.05d)
            {
                continue;
            }

            double providerHeading = HeadingDegrees(providerStart, providerEnd);
            double headingDelta = HeadingDeltaDegrees(providerHeading, edgeHeading);
            bool directionMatches = direction switch
            {
                TrafficGeometryDirection.AlongCoordinates =>
                    headingDelta <= directionToleranceDegrees,
                TrafficGeometryDirection.Unknown or TrafficGeometryDirection.BothDirections =>
                    Math.Min(headingDelta, Math.Abs(180d - headingDelta)) <=
                    directionToleranceDegrees,
                _ => false,
            };
            if (!directionMatches)
            {
                continue;
            }

            (double distance, double projectionFraction) =
                PointToSegmentProjectionMeters(
                    sample,
                    providerStart,
                    providerEnd);
            if (projectionFraction is < 0d or > 1d)
            {
                continue;
            }

            if (distance < minimum)
            {
                minimum = distance;
                nearestProviderSegment = providerIndex;
                nearestProjectionFraction = projectionFraction;
            }
        }

        return (minimum, nearestProviderSegment, nearestProjectionFraction);
    }

    private static double PolylineLengthMeters(IReadOnlyList<GeoCoordinate> shape)
    {
        double length = 0d;
        for (int index = 0; index + 1 < shape.Count; index++)
        {
            length += HaversineMeters(shape[index], shape[index + 1]);
        }

        return length;
    }

    private static GeoCoordinate Interpolate(
        GeoCoordinate start,
        GeoCoordinate end,
        double fraction)
        => new(
            start.Latitude + ((end.Latitude - start.Latitude) * fraction),
            start.Longitude + ((end.Longitude - start.Longitude) * fraction));

    private static (double Distance, double ProjectionFraction)
        PointToSegmentProjectionMeters(
            GeoCoordinate point,
            GeoCoordinate segmentStart,
            GeoCoordinate segmentEnd)
    {
        double referenceLatitude =
            (point.Latitude + segmentStart.Latitude + segmentEnd.Latitude) / 3d;
        LocalPoint localPoint = ToLocal(point, referenceLatitude);
        LocalPoint localStart = ToLocal(segmentStart, referenceLatitude);
        LocalPoint localEnd = ToLocal(segmentEnd, referenceLatitude);
        double dx = localEnd.X - localStart.X;
        double dy = localEnd.Y - localStart.Y;
        double lengthSquared = (dx * dx) + (dy * dy);
        if (lengthSquared <= double.Epsilon)
        {
            return (double.PositiveInfinity, double.NaN);
        }

        double projectionFraction =
            (((localPoint.X - localStart.X) * dx) +
             ((localPoint.Y - localStart.Y) * dy)) / lengthSquared;
        double projectedX = localStart.X + (projectionFraction * dx);
        double projectedY = localStart.Y + (projectionFraction * dy);
        double distance = Math.Sqrt(
            Math.Pow(localPoint.X - projectedX, 2) +
            Math.Pow(localPoint.Y - projectedY, 2));
        return (distance, projectionFraction);
    }

    private static double PointToSegmentDistanceMeters(
        GeoCoordinate point,
        GeoCoordinate segmentStart,
        GeoCoordinate segmentEnd)
    {
        double referenceLatitude =
            (point.Latitude + segmentStart.Latitude + segmentEnd.Latitude) / 3d;
        return PointToSegmentDistance(
            ToLocal(point, referenceLatitude),
            ToLocal(segmentStart, referenceLatitude),
            ToLocal(segmentEnd, referenceLatitude));
    }

    private static double PointToSegmentDistance(
        LocalPoint point,
        LocalPoint segmentStart,
        LocalPoint segmentEnd)
    {
        double dx = segmentEnd.X - segmentStart.X;
        double dy = segmentEnd.Y - segmentStart.Y;
        double lengthSquared = (dx * dx) + (dy * dy);
        if (lengthSquared <= double.Epsilon)
        {
            return Math.Sqrt(
                Math.Pow(point.X - segmentStart.X, 2) +
                Math.Pow(point.Y - segmentStart.Y, 2));
        }

        double projection =
            (((point.X - segmentStart.X) * dx) +
             ((point.Y - segmentStart.Y) * dy)) / lengthSquared;
        projection = Math.Clamp(projection, 0d, 1d);
        double closestX = segmentStart.X + (projection * dx);
        double closestY = segmentStart.Y + (projection * dy);
        return Math.Sqrt(
            Math.Pow(point.X - closestX, 2) +
            Math.Pow(point.Y - closestY, 2));
    }

    private static LocalPoint ToLocal(
        GeoCoordinate coordinate,
        double referenceLatitude)
    {
        const double earthRadiusMeters = 6_371_008.8d;
        double latitudeRadians = coordinate.Latitude * Math.PI / 180d;
        double longitudeRadians = coordinate.Longitude * Math.PI / 180d;
        double referenceRadians = referenceLatitude * Math.PI / 180d;
        return new LocalPoint(
            earthRadiusMeters * longitudeRadians * Math.Cos(referenceRadians),
            earthRadiusMeters * latitudeRadians);
    }

    private static double HeadingDegrees(GeoCoordinate start, GeoCoordinate end)
    {
        double startLatitude = start.Latitude * Math.PI / 180d;
        double endLatitude = end.Latitude * Math.PI / 180d;
        double longitudeDelta =
            (end.Longitude - start.Longitude) * Math.PI / 180d;
        double y = Math.Sin(longitudeDelta) * Math.Cos(endLatitude);
        double x =
            (Math.Cos(startLatitude) * Math.Sin(endLatitude)) -
            (Math.Sin(startLatitude) *
             Math.Cos(endLatitude) *
             Math.Cos(longitudeDelta));
        return (Math.Atan2(y, x) * 180d / Math.PI + 360d) % 360d;
    }

    private static double HeadingDeltaDegrees(double first, double second)
    {
        double delta = Math.Abs(first - second) % 360d;
        return delta > 180d ? 360d - delta : delta;
    }

    private static double HaversineMeters(GeoCoordinate first, GeoCoordinate second)
    {
        const double earthRadiusMeters = 6_371_008.8d;
        double firstLatitude = first.Latitude * Math.PI / 180d;
        double secondLatitude = second.Latitude * Math.PI / 180d;
        double latitudeDelta =
            (second.Latitude - first.Latitude) * Math.PI / 180d;
        double longitudeDelta =
            (second.Longitude - first.Longitude) * Math.PI / 180d;
        double a =
            Math.Pow(Math.Sin(latitudeDelta / 2d), 2) +
            (Math.Cos(firstLatitude) *
             Math.Cos(secondLatitude) *
             Math.Pow(Math.Sin(longitudeDelta / 2d), 2));
        return 2d * earthRadiusMeters * Math.Asin(Math.Min(1d, Math.Sqrt(a)));
    }

    private readonly record struct LineGeometryMatch(
        double DistanceMeters,
        int ProviderSegmentIndex,
        double MatchedLengthMeters,
        double ProviderStartMeters,
        double ProviderEndMeters);

    private readonly record struct LocalPoint(double X, double Y);
}

internal interface IValhallaTrafficSpatialGraphSource
{
    Task<IReadOnlyList<TrafficSpatialGraphEdge>> ReadAsync(
        ValhallaGraphTrafficContext context,
        TrafficSpatialQuery query,
        CancellationToken cancellationToken);
}

internal sealed class TrafficSpatialGraphEdge
{
    public TrafficSpatialGraphEdge(
        ulong tileId,
        uint directedEdgeIndex,
        ulong canonicalDirectedEdgeId,
        TrafficDirection direction,
        IReadOnlyList<GeoCoordinate> shape,
        ulong? startNodeId = null,
        ulong? endNodeId = null)
    {
        ArgumentNullException.ThrowIfNull(shape);
        if (shape.Count < 2)
        {
            throw new ArgumentException(
                "A graph edge shape requires at least two points.",
                nameof(shape));
        }

        TileId = tileId;
        DirectedEdgeIndex = directedEdgeIndex;
        CanonicalDirectedEdgeId = canonicalDirectedEdgeId;
        Direction = direction;
        Shape = Array.AsReadOnly(shape.ToArray());
        StartNodeId = startNodeId;
        EndNodeId = endNodeId;
    }

    public ulong TileId { get; }

    public uint DirectedEdgeIndex { get; }

    public ulong CanonicalDirectedEdgeId { get; }

    public TrafficDirection Direction { get; }

    public IReadOnlyList<GeoCoordinate> Shape { get; }

    public ulong? StartNodeId { get; }

    public ulong? EndNodeId { get; }
}

internal sealed record TrafficSpatialQuery(
    string Key,
    IReadOnlyList<GraphId> TileIds)
{
    public static TrafficSpatialQuery Create(
        IReadOnlyList<GeoCoordinate> geometry,
        double toleranceMeters,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        cancellationToken.ThrowIfCancellationRequested();
        if (geometry.Count == 0)
        {
            return new TrafficSpatialQuery(string.Empty, []);
        }

        if (geometry.Count >
            GraphTileTrafficSpatialIndex.MaximumGeometryPointsPerQuery)
        {
            throw new TrafficSpatialQueryLimitExceededException(
                GraphTileTrafficSpatialIndex.MaximumGeometryPointsPerQuery,
                "provider coordinates");
        }

        foreach (GeoCoordinate point in geometry)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!double.IsFinite(point.Latitude) ||
                !double.IsFinite(point.Longitude) ||
                point.Latitude is < -90 or > 90 ||
                point.Longitude is < -180 or > 180)
            {
                throw new ArgumentException(
                    "Traffic geometry coordinates must be finite WGS84 values.",
                    nameof(geometry));
            }
        }

        var tileIds = new Dictionary<ulong, GraphId>();
        foreach (var level in TileHierarchy.Levels())
        {
            cancellationToken.ThrowIfCancellationRequested();
            double tileSizeDegrees = level.Tiles.TileSize();
            if (geometry.Count == 1)
            {
                AddOwnerTileHalo(
                    geometry[0],
                    toleranceMeters,
                    tileSizeDegrees,
                    level.Level,
                    tileIds,
                    cancellationToken);
                continue;
            }

            for (int pointIndex = 0;
                 pointIndex < geometry.Count - 1;
                 pointIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                AddSegmentCorridor(
                    geometry[pointIndex],
                    geometry[pointIndex + 1],
                    toleranceMeters,
                    tileSizeDegrees,
                    level.Level,
                    tileIds,
                    cancellationToken);
            }
        }

        GraphId[] canonicalTileIds = tileIds
            .Values
            .OrderBy(static id => id.Value)
            .ToArray();
        return new TrafficSpatialQuery(
            CreateKey(canonicalTileIds),
            Array.AsReadOnly(canonicalTileIds));
    }

    private static void AddSegmentCorridor(
        GeoCoordinate start,
        GeoCoordinate end,
        double toleranceMeters,
        double tileSizeDegrees,
        byte level,
        IDictionary<ulong, GraphId> tileIds,
        CancellationToken cancellationToken)
    {
        double longitudeDelta = end.Longitude - start.Longitude;
        if (longitudeDelta > 180d)
        {
            longitudeDelta -= 360d;
        }
        else if (longitudeDelta < -180d)
        {
            longitudeDelta += 360d;
        }

        double latitudeDelta = end.Latitude - start.Latitude;
        int steps = Math.Max(
            1,
            checked((int)Math.Ceiling(
                Math.Max(
                    Math.Abs(latitudeDelta),
                    Math.Abs(longitudeDelta)) /
                tileSizeDegrees)));
        for (int step = 0; step <= steps; step++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            double fraction = (double)step / steps;
            AddOwnerTileHalo(
                new GeoCoordinate(
                    start.Latitude + (latitudeDelta * fraction),
                    NormalizeLongitude(
                        start.Longitude + (longitudeDelta * fraction))),
                toleranceMeters,
                tileSizeDegrees,
                level,
                tileIds,
                cancellationToken);
        }
    }

    private static void AddOwnerTileHalo(
        GeoCoordinate point,
        double toleranceMeters,
        double tileSizeDegrees,
        byte level,
        IDictionary<ulong, GraphId> tileIds,
        CancellationToken cancellationToken)
    {
        double latitudeTolerance = toleranceMeters / 111_320d;
        double longitudeScale = Math.Max(
            0.01d,
            Math.Cos(point.Latitude * Math.PI / 180d));
        double longitudeTolerance =
            toleranceMeters / (111_320d * longitudeScale);
        double latitudePadding = tileSizeDegrees + latitudeTolerance;
        double longitudePadding = tileSizeDegrees + longitudeTolerance;
        double minimumLatitude =
            Math.Max(-90d, point.Latitude - latitudePadding);
        double maximumLatitude =
            Math.Min(90d, point.Latitude + latitudePadding);
        double minimumLongitude = point.Longitude - longitudePadding;
        double maximumLongitude = point.Longitude + longitudePadding;

        if (minimumLongitude < -180d)
        {
            AddBounds(
                minimumLongitude + 360d,
                180d,
                minimumLatitude,
                maximumLatitude,
                level,
                tileIds,
                cancellationToken);
            AddBounds(
                -180d,
                maximumLongitude,
                minimumLatitude,
                maximumLatitude,
                level,
                tileIds,
                cancellationToken);
            return;
        }

        if (maximumLongitude > 180d)
        {
            AddBounds(
                minimumLongitude,
                180d,
                minimumLatitude,
                maximumLatitude,
                level,
                tileIds,
                cancellationToken);
            AddBounds(
                -180d,
                maximumLongitude - 360d,
                minimumLatitude,
                maximumLatitude,
                level,
                tileIds,
                cancellationToken);
            return;
        }

        AddBounds(
            minimumLongitude,
            maximumLongitude,
            minimumLatitude,
            maximumLatitude,
            level,
            tileIds,
            cancellationToken);
    }

    private static void AddBounds(
        double minimumLongitude,
        double maximumLongitude,
        double minimumLatitude,
        double maximumLatitude,
        byte level,
        IDictionary<ulong, GraphId> tileIds,
        CancellationToken cancellationToken)
    {
        var bounds = new Aabb2T<double>(
            minimumLongitude,
            minimumLatitude,
            maximumLongitude,
            maximumLatitude);
        foreach (GraphId graphId in TileHierarchy.GetGraphIds(bounds, level))
        {
            cancellationToken.ThrowIfCancellationRequested();
            GraphId tileBase = graphId.TileBase();
            if (tileIds.ContainsKey(tileBase.Value))
            {
                continue;
            }

            if (tileIds.Count >=
                GraphTileTrafficSpatialIndex.MaximumTilesPerQuery)
            {
                throw new TrafficSpatialQueryLimitExceededException(
                    GraphTileTrafficSpatialIndex.MaximumTilesPerQuery,
                    "canonical graph tiles");
            }

            tileIds.Add(tileBase.Value, tileBase);
        }
    }

    private static double NormalizeLongitude(double longitude)
    {
        double normalized = longitude % 360d;
        if (normalized > 180d)
        {
            normalized -= 360d;
        }
        else if (normalized < -180d)
        {
            normalized += 360d;
        }

        return normalized;
    }

    public IReadOnlyList<TrafficSpatialQuery> Split(int maximumTileCount)
    {
        if (maximumTileCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumTileCount));
        }

        if (TileIds.Count <= maximumTileCount)
        {
            return [this];
        }

        return TileIds
            .Chunk(maximumTileCount)
            .Select(static chunk =>
            {
                GraphId[] tiles = chunk.ToArray();
                return new TrafficSpatialQuery(
                    CreateKey(tiles),
                    Array.AsReadOnly(tiles));
            })
            .ToArray();
    }

    private static string CreateKey(IEnumerable<GraphId> tileIds)
        => string.Join(
            ',',
            tileIds.Select(static id =>
                id.Value.ToString("X16", CultureInfo.InvariantCulture)));
}

internal sealed class TrafficSpatialQueryLimitExceededException
    : InvalidOperationException
{
    public TrafficSpatialQueryLimitExceededException(
        int maximumCount,
        string limitDescription)
        : base(
            $"Traffic geometry exceeds the bounded spatial-query limit of {maximumCount} {limitDescription}.")
    {
        MaximumCount = maximumCount;
        LimitDescription = limitDescription;
    }

    public int MaximumCount { get; }

    public string LimitDescription { get; }
}

internal readonly record struct TrafficSpatialCacheKey(
    string GraphSignature,
    string QueryKey);

internal sealed class TrafficSpatialBuildEntry
{
    private readonly object _gate = new();
    private readonly CancellationTokenSource _buildCancellation = new();
    private readonly Lazy<Task<TrafficSpatialSnapshot>> _build;
    private bool _acceptingWaiters = true;
    private int _waiters;
    private int _cancelRequested;
    private long _lastAccessSequence;

    public TrafficSpatialBuildEntry(
        Func<CancellationToken, Task<TrafficSpatialSnapshot>> build)
    {
        ArgumentNullException.ThrowIfNull(build);
        _build = new Lazy<Task<TrafficSpatialSnapshot>>(
            () => build(_buildCancellation.Token),
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public Task<TrafficSpatialSnapshot> Build => _build.Value;

    public long LastAccessSequence
    {
        get
        {
            lock (_gate)
            {
                return _lastAccessSequence;
            }
        }
    }

    public bool TryAcquireWaiter(long accessSequence)
    {
        lock (_gate)
        {
            if (!_acceptingWaiters)
            {
                return false;
            }

            _waiters++;
            _lastAccessSequence = accessSequence;
            return true;
        }
    }

    public bool ReleaseWaiterAndShouldCancel()
    {
        lock (_gate)
        {
            if (_waiters <= 0)
            {
                throw new InvalidOperationException("A spatial-index waiter was released twice.");
            }

            _waiters--;
            if (_waiters == 0 && !Build.IsCompleted)
            {
                _acceptingWaiters = false;
                return true;
            }

            return false;
        }
    }

    public bool TryPrepareForEviction(long expectedAccessSequence)
    {
        lock (_gate)
        {
            bool buildCompleted = _build.IsValueCreated && _build.Value.IsCompleted;
            if (_lastAccessSequence != expectedAccessSequence
                || _waiters != 0
                || !buildCompleted)
            {
                return false;
            }

            _acceptingWaiters = false;
            return true;
        }
    }

    public void Cancel()
    {
        lock (_gate)
        {
            _acceptingWaiters = false;
        }

        if (Interlocked.Exchange(ref _cancelRequested, 1) != 0)
        {
            return;
        }

        Task<TrafficSpatialSnapshot> build;
        try
        {
            build = Build;
        }
        catch
        {
            _buildCancellation.Dispose();
            return;
        }

        _buildCancellation.Cancel();
        _ = build.ContinueWith(
            static (_, state) => ((CancellationTokenSource)state!).Dispose(),
            _buildCancellation,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }
}

internal sealed class GraphTileTrafficSpatialGraphSource
    : IValhallaTrafficSpatialGraphSource
{
    public Task<IReadOnlyList<TrafficSpatialGraphEdge>> ReadAsync(
        ValhallaGraphTrafficContext context,
        TrafficSpatialQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(query);
        if (context.GraphTileDirectory is null)
        {
            throw new ArgumentException(
                "GraphTileDirectory is required to build the traffic spatial index.",
                nameof(context));
        }

        if (!Directory.Exists(context.GraphTileDirectory))
        {
            throw new DirectoryNotFoundException(
                $"Valhalla graph tile directory '{context.GraphTileDirectory}' does not exist.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        return Task.Run<IReadOnlyList<TrafficSpatialGraphEdge>>(
            () => ReadCore(
                context.GraphTileDirectory,
                query.TileIds,
                cancellationToken),
            cancellationToken);
    }

    private static IReadOnlyList<TrafficSpatialGraphEdge> ReadCore(
        string tileDirectory,
        IReadOnlyList<GraphId> tileIds,
        CancellationToken cancellationToken)
    {
        var edges = new List<TrafficSpatialGraphEdge>();
        foreach (GraphId tileId in tileIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            GraphTile? tile = GraphTile.Create(tileDirectory, tileId);
            if (tile is null)
            {
                continue;
            }

            ulong tileBaseId = tileId.TileBase().Value;
            uint directedEdgeCount = tile.Header().Directededgecount();
            ulong?[] startNodeIds = BuildStartNodeIds(tile, tileId, directedEdgeCount);
            for (uint directedEdgeIndex = 0;
                 directedEdgeIndex < directedEdgeCount;
                 directedEdgeIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                DirectedEdge edge = tile.DirectedEdge((int)directedEdgeIndex);
                IReadOnlyList<PointLL> graphShape = tile.EdgeInfo(edge).Shape();
                if (graphShape.Count < 2)
                {
                    continue;
                }

                var directedShape = graphShape
                    .Select(static point => new GeoCoordinate(point.Lat, point.Lng))
                    .ToList();
                if (!edge.Forward)
                {
                    directedShape.Reverse();
                }

                edges.Add(new TrafficSpatialGraphEdge(
                    tileBaseId,
                    directedEdgeIndex,
                    new GraphId(
                        tileId.Tileid(),
                        tileId.Level(),
                        directedEdgeIndex).Value,
                    edge.Forward ? TrafficDirection.Forward : TrafficDirection.Reverse,
                    directedShape,
                    startNodeIds[directedEdgeIndex],
                    edge.EndNode.Value));
            }
        }

        return Array.AsReadOnly(edges.ToArray());
    }

    private static ulong?[] BuildStartNodeIds(
        GraphTile tile,
        GraphId tileId,
        uint directedEdgeCount)
    {
        var startNodeIds = new ulong?[checked((int)directedEdgeCount)];
        uint nodeCount = tile.Header().Nodecount();
        for (uint nodeIndex = 0; nodeIndex < nodeCount; nodeIndex++)
        {
            NodeInfo node = tile.Node(checked((int)nodeIndex));
            uint firstEdgeIndex = node.EdgeIndex;
            uint exclusiveEnd = Math.Min(
                directedEdgeCount,
                checked(firstEdgeIndex + node.EdgeCount));
            ulong startNodeId = new GraphId(
                tileId.Tileid(),
                tileId.Level(),
                nodeIndex).Value;
            for (uint edgeIndex = firstEdgeIndex;
                 edgeIndex < exclusiveEnd;
                 edgeIndex++)
            {
                startNodeIds[checked((int)edgeIndex)] = startNodeId;
            }
        }

        return startNodeIds;
    }
}


internal sealed class TrafficSpatialSnapshot
{
    private const double MinimumGridCellDegrees = 0.00025d;
    private readonly IReadOnlyDictionary<(int Latitude, int Longitude), int[]> _grid;
    private readonly double _gridCellDegrees;

    private TrafficSpatialSnapshot(
        IReadOnlyList<TrafficSpatialGraphEdge> edges,
        IReadOnlyDictionary<(int Latitude, int Longitude), int[]> grid,
        double gridCellDegrees)
    {
        Edges = edges;
        _grid = grid;
        _gridCellDegrees = gridCellDegrees;
    }

    public IReadOnlyList<TrafficSpatialGraphEdge> Edges { get; }

    public static TrafficSpatialSnapshot Create(
        IReadOnlyList<TrafficSpatialGraphEdge> edges,
        double matchToleranceMeters,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(edges);
        cancellationToken.ThrowIfCancellationRequested();
        var immutableEdges = Array.AsReadOnly(edges.ToArray());
        double gridCellDegrees = Math.Max(
            MinimumGridCellDegrees,
            matchToleranceMeters * 4d / 111_320d);
        var mutableGrid = new Dictionary<(int Latitude, int Longitude), List<int>>();
        for (int edgeIndex = 0; edgeIndex < immutableEdges.Count; edgeIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<GeoCoordinate> shape = immutableEdges[edgeIndex].Shape;
            for (int shapeIndex = 0; shapeIndex + 1 < shape.Count; shapeIndex++)
            {
                VisitSegmentCells(
                    shape[shapeIndex],
                    shape[shapeIndex + 1],
                    gridCellDegrees,
                    (latitudeCell, longitudeCell) =>
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var key = (latitudeCell, longitudeCell);
                        if (!mutableGrid.TryGetValue(key, out List<int>? indexes))
                        {
                            indexes = [];
                            mutableGrid.Add(key, indexes);
                        }

                        indexes.Add(edgeIndex);
                    });
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        var grid = mutableGrid.ToDictionary(
            static pair => pair.Key,
            static pair => pair.Value.Distinct().ToArray());
        return new TrafficSpatialSnapshot(immutableEdges, grid, gridCellDegrees);
    }

    public IReadOnlyList<int> Query(
        IReadOnlyList<GeoCoordinate> geometry,
        double toleranceMeters)
    {
        if (geometry.Count == 0)
        {
            return Array.Empty<int>();
        }

        double middleLatitude =
            (geometry.Min(static point => point.Latitude) +
             geometry.Max(static point => point.Latitude)) / 2d;
        double latitudePadding = toleranceMeters / 111_320d;
        double longitudeScale = Math.Max(
            0.01d,
            Math.Cos(middleLatitude * Math.PI / 180d));
        double longitudePadding = toleranceMeters / (111_320d * longitudeScale);
        int latitudePaddingCells =
            (int)Math.Ceiling(latitudePadding / _gridCellDegrees);
        int longitudePaddingCells =
            (int)Math.Ceiling(longitudePadding / _gridCellDegrees);
        var indexes = new HashSet<int>();

        void AddNearbyCell(int latitudeCell, int longitudeCell)
        {
            for (int latitudeOffset = -latitudePaddingCells;
                 latitudeOffset <= latitudePaddingCells;
                 latitudeOffset++)
            {
                for (int longitudeOffset = -longitudePaddingCells;
                     longitudeOffset <= longitudePaddingCells;
                     longitudeOffset++)
                {
                    if (_grid.TryGetValue(
                            (
                                latitudeCell + latitudeOffset,
                                longitudeCell + longitudeOffset),
                            out int[]? cellIndexes))
                    {
                        indexes.UnionWith(cellIndexes);
                    }
                }
            }
        }

        if (geometry.Count == 1)
        {
            AddNearbyCell(
                Cell(geometry[0].Latitude, _gridCellDegrees),
                Cell(geometry[0].Longitude, _gridCellDegrees));
        }
        else
        {
            for (int geometryIndex = 0;
                 geometryIndex + 1 < geometry.Count;
                 geometryIndex++)
            {
                VisitSegmentCells(
                    geometry[geometryIndex],
                    geometry[geometryIndex + 1],
                    _gridCellDegrees,
                    AddNearbyCell);
            }
        }

        return indexes.Order().ToArray();
    }

    private static void VisitSegmentCells(
        GeoCoordinate start,
        GeoCoordinate end,
        double gridCellDegrees,
        Action<int, int> visitor)
    {
        int startLatitudeCell = Cell(start.Latitude, gridCellDegrees);
        int endLatitudeCell = Cell(end.Latitude, gridCellDegrees);
        int startLongitudeCell = Cell(start.Longitude, gridCellDegrees);
        int endLongitudeCell = Cell(end.Longitude, gridCellDegrees);
        int steps = Math.Max(
            Math.Abs(endLatitudeCell - startLatitudeCell),
            Math.Abs(endLongitudeCell - startLongitudeCell));
        if (steps == 0)
        {
            visitor(startLatitudeCell, startLongitudeCell);
            return;
        }

        for (int step = 0; step <= steps; step++)
        {
            double fraction = (double)step / steps;
            visitor(
                Cell(
                    start.Latitude +
                    ((end.Latitude - start.Latitude) * fraction),
                    gridCellDegrees),
                Cell(
                    start.Longitude +
                    ((end.Longitude - start.Longitude) * fraction),
                    gridCellDegrees));
        }
    }

    private static int Cell(double value, double gridCellDegrees)
        => checked((int)Math.Floor(value / gridCellDegrees));
}
