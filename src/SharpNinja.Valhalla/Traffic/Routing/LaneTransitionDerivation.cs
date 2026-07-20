using SharpNinja.Valhalla.Baldr;

namespace SharpNinja.Valhalla.Traffic.Routing;

public interface ILaneTransitionDeriver
{
    LaneTransitionDerivation Derive(
        LaneTopologySegment from,
        LaneTopologySegment to,
        LaneTransitionTopologyContext context);
}

public sealed class EvidenceBackedLaneTransitionDeriver : ILaneTransitionDeriver
{
    public LaneTransitionDerivation Derive(
        LaneTopologySegment from,
        LaneTopologySegment to,
        LaneTransitionTopologyContext context)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);
        ArgumentNullException.ThrowIfNull(context);

        LaneTopologyGraphEvidence? fromEvidence = from.GraphEvidence;
        LaneTopologyGraphEvidence? toEvidence = to.GraphEvidence;
        if (fromEvidence is null ||
            toEvidence is null ||
            fromEvidence.CanonicalEndNodeId != toEvidence.CanonicalStartNodeId)
        {
            return Unavailable(from, to, LaneTransitionEvidenceKind.MissingSharedCanonicalNode);
        }

        LaneTransitionDerivation? explicitResult = DeriveExplicitConnectivity(from, to);
        if (explicitResult is not null)
        {
            return explicitResult;
        }

        if (TryDeriveMerge(from, to, context, out LaneTransitionDerivation merge))
        {
            return merge;
        }

        LaneTurnIntent desiredIntent = GetDesiredIntent(fromEvidence.EndHeadingDegrees, toEvidence.StartHeadingDegrees);
        LaneTopologySegment[] competing = context.OutboundEdges
            .Where(candidate =>
                candidate.GraphEvidence is not null &&
                candidate.GraphEvidence.CanonicalStartNodeId == fromEvidence.CanonicalEndNodeId &&
                IntentMatches(
                    desiredIntent,
                    GetDesiredIntent(
                        fromEvidence.EndHeadingDegrees,
                        candidate.GraphEvidence.StartHeadingDegrees)))
            .ToArray();

        if (competing.Length > 1)
        {
            return Unavailable(from, to, LaneTransitionEvidenceKind.CompetingTopologyMatch);
        }

        LaneTransitionDerivation? turnLaneResult = DeriveTurnLaneMapping(
            from,
            to,
            desiredIntent,
            context.OutboundEdgesComplete && competing.Length == 1);
        if (turnLaneResult is not null)
        {
            return turnLaneResult;
        }

        bool sameWay = from.OsmWayId.HasValue &&
            from.OsmWayId == to.OsmWayId;
        bool stableKnownLaneCount = fromEvidence.LaneCountKnown &&
            toEvidence.LaneCountKnown &&
            from.LaneCount == to.LaneCount;
        bool alignedBearing = HeadingDifference(
            fromEvidence.EndHeadingDegrees,
            toEvidence.StartHeadingDegrees) <= 10d;
        LaneTopologySegment[] continuityCandidates = context.OutboundEdges
            .Where(candidate => IsContinuityCandidate(from, candidate))
            .ToArray();
        bool uniqueContinuity = continuityCandidates.Length == 1 &&
            string.Equals(
                continuityCandidates[0].SegmentId,
                to.SegmentId,
                StringComparison.Ordinal);
        if (context.OutboundEdgesComplete &&
            sameWay && stableKnownLaneCount && alignedBearing && uniqueContinuity)
        {
            return Create(
                from,
                to,
                IdentityOptions(from.LaneCount),
                LaneTransitionProvenance.SameWayTopology,
                LaneTransitionConfidence.High,
                LaneTopologyChangeKind.Continuation,
                [
                    Evidence(from, to, LaneTransitionEvidenceKind.SharedCanonicalNode),
                    Evidence(from, to, LaneTransitionEvidenceKind.SameOsmWay),
                    Evidence(from, to, LaneTransitionEvidenceKind.KnownStableLaneCount),
                    Evidence(from, to, LaneTransitionEvidenceKind.AlignedBearing),
                    Evidence(from, to, LaneTransitionEvidenceKind.UniqueContinuingTopology),
                ]);
        }

        LaneTransitionEvidenceKind unavailableReason =
            context.OutboundEdgesComplete && context.InboundEdgesComplete
                ? LaneTransitionEvidenceKind.InsufficientTopologyEvidence
                : LaneTransitionEvidenceKind.IncompleteTopologyContext;
        return Unavailable(from, to, unavailableReason);
    }

    private static LaneTransitionDerivation? DeriveExplicitConnectivity(
        LaneTopologySegment from,
        LaneTopologySegment to)
    {
        string sourceWayId = from.OsmWayId?.ToString(System.Globalization.CultureInfo.InvariantCulture)
            ?? string.Empty;
        LaneTransitionOption[] options = to.IncomingConnections
            .Where(connection =>
                string.Equals(connection.FromSegmentId, sourceWayId, StringComparison.Ordinal))
            .SelectMany(static connection => ExpandConnection(connection.FromLanes, connection.ToLanes))
            .Where(option =>
                option.FromLane <= from.LaneCount &&
                option.ToLane <= to.LaneCount)
            .Distinct()
            .OrderBy(static option => option.FromLane)
            .ThenBy(static option => option.ToLane)
            .ToArray();
        if (options.Length == 0)
        {
            return null;
        }

        return Create(
            from,
            to,
            options,
            LaneTransitionProvenance.ExplicitConnectivity,
            LaneTransitionConfidence.High,
            ClassifyChange(from, to),
            [Evidence(from, to, LaneTransitionEvidenceKind.ExplicitLaneConnectivity)]);
    }

    private static LaneTransitionDerivation? DeriveTurnLaneMapping(
        LaneTopologySegment from,
        LaneTopologySegment to,
        LaneTurnIntent desiredIntent,
        bool uniqueTopologyMatch)
    {
        if (!uniqueTopologyMatch ||
            desiredIntent == LaneTurnIntent.None ||
            from.LaneIntents.Count != from.LaneCount)
        {
            return null;
        }

        int[] matchingFromLanes = from.LaneIntents
            .Select((intent, index) => new { Intent = intent, Lane = index + 1 })
            .Where(item => IntentMatches(item.Intent, desiredIntent))
            .Select(static item => item.Lane)
            .ToArray();
        if (matchingFromLanes.Length == 0)
        {
            return null;
        }

        LaneTransitionOption[] options;
        if (matchingFromLanes.Length == to.LaneCount)
        {
            options = matchingFromLanes
                .Select((lane, index) => new LaneTransitionOption(lane, index + 1))
                .ToArray();
        }
        else if (to.LaneCount == 1)
        {
            options = matchingFromLanes
                .Select(static lane => new LaneTransitionOption(lane, 1))
                .ToArray();
        }
        else
        {
            return null;
        }

        LaneTopologyChangeKind change = to.GraphEvidence?.Use == Use.Ramp
            ? LaneTopologyChangeKind.RampExit
            : ClassifyChange(from, to);
        return Create(
            from,
            to,
            options,
            LaneTransitionProvenance.TurnLaneInferred,
            LaneTransitionConfidence.High,
            change,
            [
                Evidence(from, to, LaneTransitionEvidenceKind.SharedCanonicalNode),
                Evidence(from, to, LaneTransitionEvidenceKind.KnownLaneCount),
                new LaneTransitionEvidence(
                    LaneTransitionEvidenceKind.UniqueTurnIntentBranch,
                    from.CanonicalDirectedEdgeId,
                    to.CanonicalDirectedEdgeId,
                    Array.AsReadOnly(new[] { desiredIntent }),
                    Array.Empty<ulong>()),
            ]);
    }

    private static bool TryDeriveMerge(
        LaneTopologySegment from,
        LaneTopologySegment to,
        LaneTransitionTopologyContext context,
        out LaneTransitionDerivation result)
    {
        result = null!;
        if (!context.InboundEdgesComplete ||
            from.OsmWayId is null ||
            from.OsmWayId != to.OsmWayId ||
            from.GraphEvidence is null ||
            to.GraphEvidence is null ||
            !from.GraphEvidence.LaneCountKnown ||
            !to.GraphEvidence.LaneCountKnown ||
            HeadingDifference(
                from.GraphEvidence.EndHeadingDegrees,
                to.GraphEvidence.StartHeadingDegrees) > 10d ||
            to.LaneCount <= from.LaneCount)
        {
            return false;
        }

        LaneTopologySegment[] additionalSources = context.InboundEdges
            .Where(candidate =>
                !string.Equals(candidate.SegmentId, from.SegmentId, StringComparison.Ordinal) &&
                candidate.GraphEvidence is not null &&
                candidate.GraphEvidence.CanonicalEndNodeId == to.GraphEvidence.CanonicalStartNodeId)
            .ToArray();
        int addedLanes = to.LaneCount - from.LaneCount;
        if (additionalSources.Length != 1)
        {
            return false;
        }

        LaneTopologyGraphEvidence? additionalEvidence = additionalSources[0].GraphEvidence;
        if (additionalEvidence is null ||
            !additionalEvidence.LaneCountKnown ||
            additionalEvidence.Use is not (Use.Ramp or Use.TurnChannel) ||
            HeadingDifference(
                additionalEvidence.EndHeadingDegrees,
                to.GraphEvidence.StartHeadingDegrees) > 45d ||
            additionalSources[0].LaneCount != addedLanes)
        {
            return false;
        }

        double mergeSide = SignedHeadingDelta(
            to.GraphEvidence.StartHeadingDegrees,
            additionalEvidence.EndHeadingDegrees);
        if (Math.Abs(mergeSide) < 5d)
        {
            return false;
        }

        bool joinsFromRight = mergeSide < 0d;
        IReadOnlyList<LaneTransitionOption> mainlineOptions = Array.AsReadOnly(
            Enumerable.Range(1, from.LaneCount)
                .Select(lane => new LaneTransitionOption(
                    lane,
                    joinsFromRight ? lane : lane + addedLanes))
                .ToArray());
        LaneTransitionEvidenceKind mergeSideEvidence = joinsFromRight
            ? LaneTransitionEvidenceKind.MergeFromRight
            : LaneTransitionEvidenceKind.MergeFromLeft;
        result = Create(
            from,
            to,
            mainlineOptions,
            LaneTransitionProvenance.SameWayTopology,
            LaneTransitionConfidence.High,
            LaneTopologyChangeKind.Merge,
            [
                Evidence(from, to, LaneTransitionEvidenceKind.SharedCanonicalNode),
                Evidence(from, to, LaneTransitionEvidenceKind.SameOsmWay),
                Evidence(from, to, LaneTransitionEvidenceKind.KnownLaneCountDelta),
                Evidence(from, to, LaneTransitionEvidenceKind.AlignedBearing),
                Evidence(from, to, LaneTransitionEvidenceKind.RampOrMergeTopology),
                Evidence(from, to, mergeSideEvidence),
                new LaneTransitionEvidence(
                    LaneTransitionEvidenceKind.UniqueAdditionalInboundSource,
                    from.CanonicalDirectedEdgeId,
                    to.CanonicalDirectedEdgeId,
                    Array.Empty<LaneTurnIntent>(),
                    Array.AsReadOnly(
                        additionalSources
                            .Select(static segment => segment.CanonicalDirectedEdgeId ?? 0UL)
                            .Where(static id => id != 0UL)
                            .ToArray())),
            ]);
        return true;
    }

    private static LaneTransitionDerivation Create(
        LaneTopologySegment from,
        LaneTopologySegment to,
        IReadOnlyList<LaneTransitionOption> options,
        LaneTransitionProvenance provenance,
        LaneTransitionConfidence confidence,
        LaneTopologyChangeKind changeKind,
        IReadOnlyList<LaneTransitionEvidence> evidence)
        => new(
            from.SegmentId,
            to.SegmentId,
            options,
            provenance,
            confidence,
            changeKind,
            evidence);

    private static LaneTransitionDerivation Unavailable(
        LaneTopologySegment from,
        LaneTopologySegment to,
        LaneTransitionEvidenceKind kind)
        => Create(
            from,
            to,
            Array.Empty<LaneTransitionOption>(),
            LaneTransitionProvenance.Unavailable,
            LaneTransitionConfidence.Unavailable,
            LaneTopologyChangeKind.Ambiguous,
            [Evidence(from, to, kind)]);

    private static LaneTransitionEvidence Evidence(
        LaneTopologySegment from,
        LaneTopologySegment to,
        LaneTransitionEvidenceKind kind)
        => new(
            kind,
            from.CanonicalDirectedEdgeId,
            to.CanonicalDirectedEdgeId,
            Array.Empty<LaneTurnIntent>(),
            Array.Empty<ulong>());

    private static LaneTopologyChangeKind ClassifyChange(
        LaneTopologySegment from,
        LaneTopologySegment to)
    {
        if (to.GraphEvidence?.Use == Use.Ramp)
        {
            return LaneTopologyChangeKind.RampExit;
        }

        if (to.LaneCount < from.LaneCount)
        {
            return LaneTopologyChangeKind.LaneDrop;
        }

        if (to.LaneCount > from.LaneCount)
        {
            return LaneTopologyChangeKind.Merge;
        }

        return LaneTopologyChangeKind.Continuation;
    }

    private static IReadOnlyList<LaneTransitionOption> IdentityOptions(int laneCount)
        => Array.AsReadOnly(
            Enumerable.Range(1, laneCount)
                .Select(static lane => new LaneTransitionOption(lane, lane))
                .ToArray());

    private static bool IsContinuityCandidate(
        LaneTopologySegment from,
        LaneTopologySegment candidate)
    {
        if (from.GraphEvidence is null || candidate.GraphEvidence is null)
        {
            return false;
        }

        bool sameWay = from.OsmWayId.HasValue && from.OsmWayId == candidate.OsmWayId;
        bool sharedReference = from.GraphEvidence.References
            .Intersect(candidate.GraphEvidence.References, StringComparer.OrdinalIgnoreCase)
            .Any();
        bool sharedDestination = from.GraphEvidence.Destinations
            .Intersect(candidate.GraphEvidence.Destinations, StringComparer.OrdinalIgnoreCase)
            .Any();
        return (sameWay || sharedReference || sharedDestination) &&
            HeadingDifference(
                from.GraphEvidence.EndHeadingDegrees,
                candidate.GraphEvidence.StartHeadingDegrees) <= 10d;
    }

    private static double HeadingDifference(double first, double second)
    {
        double difference = Math.Abs(first - second) % 360d;
        return difference > 180d ? 360d - difference : difference;
    }

    private static double SignedHeadingDelta(double reference, double candidate)
        => ((candidate - reference + 540d) % 360d) - 180d;

    private static LaneTurnIntent GetDesiredIntent(double fromHeading, double toHeading)
    {
        double delta = ((toHeading - fromHeading + 540d) % 360d) - 180d;
        if (delta >= 45d)
        {
            return LaneTurnIntent.Right;
        }

        if (delta >= 11d)
        {
            return LaneTurnIntent.SlightRight;
        }

        if (delta <= -45d)
        {
            return LaneTurnIntent.Left;
        }

        if (delta <= -11d)
        {
            return LaneTurnIntent.SlightLeft;
        }

        return LaneTurnIntent.Through;
    }

    private static bool IntentMatches(LaneTurnIntent available, LaneTurnIntent desired)
        => desired switch
        {
            LaneTurnIntent.Right =>
                (available & (LaneTurnIntent.Right | LaneTurnIntent.SlightRight)) != 0,
            LaneTurnIntent.SlightRight =>
                (available & (LaneTurnIntent.Right | LaneTurnIntent.SlightRight)) != 0,
            LaneTurnIntent.Left =>
                (available & (LaneTurnIntent.Left | LaneTurnIntent.SlightLeft)) != 0,
            LaneTurnIntent.SlightLeft =>
                (available & (LaneTurnIntent.Left | LaneTurnIntent.SlightLeft)) != 0,
            LaneTurnIntent.Through => (available & LaneTurnIntent.Through) != 0,
            _ => false,
        };

    private static IEnumerable<LaneTransitionOption> ExpandConnection(
        IReadOnlyList<int> fromLanes,
        IReadOnlyList<int> toLanes)
    {
        int[] from = fromLanes.Where(static lane => lane > 0).ToArray();
        int[] to = toLanes.Where(static lane => lane > 0).ToArray();
        if (from.Length == 0 || to.Length == 0)
        {
            yield break;
        }

        if (from.Length == to.Length)
        {
            for (var index = 0; index < from.Length; index++)
            {
                yield return new LaneTransitionOption(from[index], to[index]);
            }

            yield break;
        }

        if (to.Length == 1)
        {
            foreach (int lane in from)
            {
                yield return new LaneTransitionOption(lane, to[0]);
            }

            yield break;
        }

        if (from.Length == 1)
        {
            foreach (int lane in to)
            {
                yield return new LaneTransitionOption(from[0], lane);
            }
        }
    }
}

public sealed class LaneTransitionTopologyContext
{
    public LaneTransitionTopologyContext(
        IEnumerable<LaneTopologySegment> outboundEdges,
        IEnumerable<LaneTopologySegment> inboundEdges,
        bool outboundEdgesComplete = true,
        bool inboundEdgesComplete = true,
        LaneTransitionTopologyContextSource source = LaneTransitionTopologyContextSource.CallerProvided)
    {
        ArgumentNullException.ThrowIfNull(outboundEdges);
        ArgumentNullException.ThrowIfNull(inboundEdges);
        OutboundEdges = Array.AsReadOnly(outboundEdges.ToArray());
        InboundEdges = Array.AsReadOnly(inboundEdges.ToArray());
        OutboundEdgesComplete = outboundEdgesComplete;
        InboundEdgesComplete = inboundEdgesComplete;
        Source = source;
    }

    public IReadOnlyList<LaneTopologySegment> OutboundEdges { get; }

    public IReadOnlyList<LaneTopologySegment> InboundEdges { get; }

    public bool OutboundEdgesComplete { get; }

    public bool InboundEdgesComplete { get; }

    public LaneTransitionTopologyContextSource Source { get; }
}

public enum LaneTransitionTopologyContextSource
{
    CallerProvided = 0,
    GraphTile = 1,
    IncompleteGraphTile = 2,
    MissingGraphData = 3,
}

public sealed class LaneTopologyGraphEvidence
{
    public LaneTopologyGraphEvidence(
        ulong canonicalStartNodeId,
        ulong canonicalEndNodeId,
        uint localEdgeIndex,
        double startHeadingDegrees,
        double endHeadingDegrees,
        Use use,
        bool laneCountKnown,
        IEnumerable<string> references,
        IEnumerable<string> destinations)
    {
        if (!double.IsFinite(startHeadingDegrees) ||
            startHeadingDegrees < 0d ||
            startHeadingDegrees >= 360d)
        {
            throw new ArgumentOutOfRangeException(nameof(startHeadingDegrees));
        }

        if (!double.IsFinite(endHeadingDegrees) ||
            endHeadingDegrees < 0d ||
            endHeadingDegrees >= 360d)
        {
            throw new ArgumentOutOfRangeException(nameof(endHeadingDegrees));
        }

        ArgumentNullException.ThrowIfNull(references);
        ArgumentNullException.ThrowIfNull(destinations);
        CanonicalStartNodeId = canonicalStartNodeId;
        CanonicalEndNodeId = canonicalEndNodeId;
        LocalEdgeIndex = localEdgeIndex;
        StartHeadingDegrees = startHeadingDegrees;
        EndHeadingDegrees = endHeadingDegrees;
        Use = use;
        LaneCountKnown = laneCountKnown;
        References = Array.AsReadOnly(
            references.Where(static value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.Ordinal)
                .ToArray());
        Destinations = Array.AsReadOnly(
            destinations.Where(static value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.Ordinal)
                .ToArray());
    }

    public ulong CanonicalStartNodeId { get; }

    public ulong CanonicalEndNodeId { get; }

    public uint LocalEdgeIndex { get; }

    public double StartHeadingDegrees { get; }

    public double EndHeadingDegrees { get; }

    public Use Use { get; }

    public bool LaneCountKnown { get; }

    public IReadOnlyList<string> References { get; }

    public IReadOnlyList<string> Destinations { get; }
}

public sealed class LaneTransitionDerivation
{
    public LaneTransitionDerivation(
        string fromSegmentId,
        string toSegmentId,
        IEnumerable<LaneTransitionOption> options,
        LaneTransitionProvenance provenance,
        LaneTransitionConfidence confidence,
        LaneTopologyChangeKind changeKind,
        IEnumerable<LaneTransitionEvidence> evidence)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fromSegmentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(toSegmentId);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(evidence);
        FromSegmentId = fromSegmentId;
        ToSegmentId = toSegmentId;
        Options = Array.AsReadOnly(options.Distinct().ToArray());
        Provenance = provenance;
        Confidence = confidence;
        ChangeKind = changeKind;
        Evidence = Array.AsReadOnly(evidence.ToArray());
    }

    public string FromSegmentId { get; }

    public string ToSegmentId { get; }

    public IReadOnlyList<LaneTransitionOption> Options { get; }

    public LaneTransitionProvenance Provenance { get; }

    public LaneTransitionConfidence Confidence { get; }

    public LaneTopologyChangeKind ChangeKind { get; }

    public IReadOnlyList<LaneTransitionEvidence> Evidence { get; }

    /// <summary>
    /// Gets the validated canonical overlay descriptor when this derivation originated from
    /// an external canonical lane-topology dataset.
    /// </summary>
    public LaneTopologyOverlayDescriptor? OverlaySource { get; init; }

    /// <summary>Gets the source rationale supplied by the canonical overlay.</summary>
    public string? SourceRationale { get; init; }

    public bool CanDriveGuidance =>
        Options.Count > 0 &&
        Confidence is LaneTransitionConfidence.Medium or LaneTransitionConfidence.High;
}

public readonly record struct LaneTransitionOption(int FromLane, int ToLane);

public sealed record LaneTransitionEvidence(
    LaneTransitionEvidenceKind Kind,
    ulong? FromCanonicalDirectedEdgeId,
    ulong? ToCanonicalDirectedEdgeId,
    IReadOnlyList<LaneTurnIntent> TurnIntents,
    IReadOnlyList<ulong> RelatedCanonicalDirectedEdgeIds);

public enum LaneTransitionProvenance
{
    Unavailable = 0,
    ExplicitConnectivity = 1,
    TurnLaneInferred = 2,
    SameWayTopology = 3,
    CanonicalOverlay = 4,
}

public enum LaneTransitionConfidence
{
    Unavailable = 0,
    Low = 1,
    Medium = 2,
    High = 3,
}

public enum LaneTopologyChangeKind
{
    Continuation = 0,
    LaneDrop = 1,
    Merge = 2,
    Split = 3,
    RampExit = 4,
    Ambiguous = 5,
}

public enum LaneTransitionEvidenceKind
{
    ExplicitLaneConnectivity = 0,
    SharedCanonicalNode = 1,
    SameOsmWay = 2,
    KnownStableLaneCount = 3,
    KnownLaneCount = 4,
    KnownLaneCountDelta = 5,
    UniqueTurnIntentBranch = 6,
    UniqueAdditionalInboundSource = 7,
    CompetingTopologyMatch = 8,
    MissingSharedCanonicalNode = 9,
    InsufficientTopologyEvidence = 10,
    AlignedBearing = 11,
    UniqueContinuingTopology = 12,
    RampOrMergeTopology = 13,
    MergeFromRight = 14,
    MergeFromLeft = 15,
    IncompleteTopologyContext = 16,
    CanonicalOverlayDataset = 17,
}
