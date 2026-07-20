using System.Globalization;

namespace SharpNinja.Valhalla.Traffic.Routing;

/// <summary>
/// Composes stable, precomputed lane-topology friction with route-specific lane
/// changes. This deliberately scores concrete lane events instead of applying a
/// blanket freeway penalty.
/// </summary>
public static class LaneFrictionAnalyzer
{
	private const int CarLaneChangePenalty = 8;
	private const int TruckLaneChangePenalty = 14;
	private const double TruckSensitiveMultiplier = 1.75d;

	public static LaneFrictionProfile Analyze(LaneFrictionRequest request)
	{
		ArgumentNullException.ThrowIfNull(request);

		var contributions = new List<LaneFrictionContribution>();
		var guidance = new List<LaneGuidancePoint>();
		var routeLaneChanges = 0;

		foreach (RouteLaneSegment segment in request.RouteSegments)
		{
			int laneChanges = Math.Abs(segment.ExitLane - segment.EntryLane);
			if (laneChanges > 0)
			{
				int score = laneChanges * LaneChangePenalty(request.VehicleClass);
				routeLaneChanges += laneChanges;
				contributions.Add(new LaneFrictionContribution(
					LaneFrictionContributionKind.RouteLaneChange,
					score,
					segment.SegmentId,
					segment.EntryLane,
					string.Format(
						CultureInfo.InvariantCulture,
						"route-specific lane change from lane {0} to lane {1}",
						segment.EntryLane,
						segment.ExitLane))
				{
					OverlaySource = segment.OverlaySource,
				});

				guidance.Add(new LaneGuidancePoint(
					segment.SegmentId,
					segment.DistanceAlongRouteMeters,
					string.Format(
						CultureInfo.InvariantCulture,
						"Move from lane {0} to lane {1}.",
						segment.EntryLane,
						segment.ExitLane)));
			}

			foreach (CanonicalLaneFrictionPoint point in request.CanonicalPoints
				.Where(point => string.Equals(point.SegmentId, segment.SegmentId, StringComparison.Ordinal)
					&& LanePassesThroughPoint(segment, point)))
			{
				int score = AdjustScore(point.Severity, point.TruckSensitive, request.VehicleClass);
				contributions.Add(new LaneFrictionContribution(
					point.Kind,
					score,
					point.SegmentId,
					point.LaneNumber,
					point.Description)
				{
					OverlaySource = point.OverlaySource,
				});

				guidance.Add(new LaneGuidancePoint(
					point.SegmentId,
					segment.DistanceAlongRouteMeters + point.DistanceAlongSegmentMeters,
					point.Description));
			}

			foreach (RouteLaneFrictionModifier modifier in (request.RouteModifiers ?? Array.Empty<RouteLaneFrictionModifier>())
				.Where(modifier => string.Equals(modifier.SegmentId, segment.SegmentId, StringComparison.Ordinal)
					&& (modifier.RouteSegmentOccurrenceIndex is null ||
						modifier.RouteSegmentOccurrenceIndex == segment.OccurrenceIndex)
					&& LanePassesThroughModifier(segment, modifier)))
			{
				int score = AdjustScore(modifier.Severity, modifier.TruckSensitive, request.VehicleClass);
				contributions.Add(new LaneFrictionContribution(
					modifier.Kind,
					score,
					modifier.SegmentId,
					modifier.LaneNumber,
					modifier.Description)
				{
					OverlaySource = modifier.OverlaySource,
				});

				guidance.Add(new LaneGuidancePoint(
					modifier.SegmentId,
					segment.DistanceAlongRouteMeters + modifier.DistanceAlongSegmentMeters,
					modifier.Description));
			}
		}

		var orderedContributions = contributions
			.Where(static contribution => contribution.Score > 0)
			.OrderByDescending(static contribution => contribution.Score)
			.ThenBy(static contribution => contribution.SegmentId, StringComparer.Ordinal)
			.ThenBy(static contribution => contribution.LaneNumber)
			.ToArray();

		return new LaneFrictionProfile(
			Score: SaturatingScore(orderedContributions),
			CanonicalPointCount: orderedContributions.Count(static contribution => contribution.Kind != LaneFrictionContributionKind.RouteLaneChange),
			RouteLaneChangeCount: routeLaneChanges,
			AdjacentMergeCount: orderedContributions.Count(static contribution => contribution.Kind == LaneFrictionContributionKind.AdjacentMerge),
			Contributions: orderedContributions,
			Guidance: guidance
				.OrderBy(static point => point.DistanceAlongRouteMeters)
				.ThenBy(static point => point.SegmentId, StringComparer.Ordinal)
				.ToArray());
	}


	internal static LaneFrictionProfile AnalyzeOverlayLowerBound(
		IReadOnlyList<CanonicalLaneFrictionPoint> canonicalPoints,
		IReadOnlyDictionary<string, int> segmentLaneCounts,
		LaneFrictionVehicleClass vehicleClass)
	{
		ArgumentNullException.ThrowIfNull(canonicalPoints);
		ArgumentNullException.ThrowIfNull(segmentLaneCounts);

		LaneFrictionContribution[] contributions = canonicalPoints
			.Where(static point => point.OverlaySource is not null)
			.GroupBy(point => new
			{
				point.SegmentId,
				DistanceMillimeters = checked((long)Math.Round(
					point.DistanceAlongSegmentMeters * 1_000d,
					MidpointRounding.AwayFromZero)),
				point.Kind,
				point.Description,
				point.TruckSensitive,
				point.OverlaySource,
			})
			.Where(group =>
				segmentLaneCounts.TryGetValue(group.Key.SegmentId, out int laneCount) &&
				laneCount > 0 &&
				group.Select(static point => point.LaneNumber)
					.Where(lane => lane >= 1 && lane <= laneCount)
					.Distinct()
					.Order()
					.SequenceEqual(Enumerable.Range(1, laneCount)))
			.Select(group => new LaneFrictionContribution(
				group.Key.Kind,
				group.Min(point => AdjustScore(
					point.Severity,
					point.TruckSensitive,
					vehicleClass)),
				group.Key.SegmentId,
				LaneNumber: 0,
				group.Key.Description)
			{
				OverlaySource = group.Key.OverlaySource,
			})
			.Where(static contribution => contribution.Score > 0)
			.OrderByDescending(static contribution => contribution.Score)
			.ThenBy(static contribution => contribution.SegmentId, StringComparer.Ordinal)
			.ThenBy(static contribution => contribution.Kind)
			.ToArray();

		return new LaneFrictionProfile(
			Score: SaturatingScore(contributions),
			CanonicalPointCount: contributions.Count(static contribution =>
				contribution.Kind != LaneFrictionContributionKind.RouteLaneChange),
			RouteLaneChangeCount: 0,
			AdjacentMergeCount: contributions.Count(static contribution =>
				contribution.Kind == LaneFrictionContributionKind.AdjacentMerge),
			Contributions: contributions,
			Guidance: Array.Empty<LaneGuidancePoint>());
	}

	private static bool LanePassesThroughPoint(RouteLaneSegment segment, CanonicalLaneFrictionPoint point)
		=> LanePassesThroughLane(segment, point.LaneNumber);

	private static bool LanePassesThroughModifier(RouteLaneSegment segment, RouteLaneFrictionModifier modifier)
		=> LanePassesThroughLane(segment, modifier.LaneNumber);

	private static bool LanePassesThroughLane(RouteLaneSegment segment, int laneNumber)
	{
		int minLane = Math.Min(segment.EntryLane, segment.ExitLane);
		int maxLane = Math.Max(segment.EntryLane, segment.ExitLane);
		return laneNumber >= minLane && laneNumber <= maxLane;
	}

	private static int SaturatingScore(
		IEnumerable<LaneFrictionContribution> contributions)
	{
		long total = 0;
		foreach (LaneFrictionContribution contribution in contributions)
		{
			total += contribution.Score;
			if (total >= int.MaxValue)
			{
				return int.MaxValue;
			}
		}

		return (int)total;
	}

	private static int AdjustScore(int severity, bool truckSensitive, LaneFrictionVehicleClass vehicleClass)
	{
		if (vehicleClass != LaneFrictionVehicleClass.Truck || !truckSensitive)
		{
			return severity;
		}

		return (int)Math.Ceiling(severity * TruckSensitiveMultiplier);
	}

	private static int LaneChangePenalty(LaneFrictionVehicleClass vehicleClass)
		=> vehicleClass == LaneFrictionVehicleClass.Truck ? TruckLaneChangePenalty : CarLaneChangePenalty;
}

public sealed record LaneFrictionRequest(
	IReadOnlyList<CanonicalLaneFrictionPoint> CanonicalPoints,
	IReadOnlyList<RouteLaneSegment> RouteSegments,
	LaneFrictionVehicleClass VehicleClass,
	IReadOnlyList<RouteLaneFrictionModifier>? RouteModifiers = null);

public sealed record CanonicalLaneFrictionPoint(
	string SegmentId,
	int LaneNumber,
	double DistanceAlongSegmentMeters,
	LaneFrictionContributionKind Kind,
	int Severity,
	string Description,
	bool TruckSensitive = false)
{
	/// <summary>Gets the validated canonical overlay descriptor for this point, if any.</summary>
	public LaneTopologyOverlayDescriptor? OverlaySource { get; init; }
}

public sealed record RouteLaneSegment(
	string SegmentId,
	int EntryLane,
	int ExitLane,
	double DistanceAlongRouteMeters)
{
	/// <summary>Gets the zero-based occurrence in the projected route when available.</summary>
	public int OccurrenceIndex { get; init; } = -1;

	/// <summary>Gets the validated canonical overlay descriptor that drove this lane path.</summary>
	public LaneTopologyOverlayDescriptor? OverlaySource { get; init; }
}

public sealed record RouteLaneFrictionModifier(
	string SegmentId,
	int LaneNumber,
	double DistanceAlongSegmentMeters,
	LaneFrictionContributionKind Kind,
	int Severity,
	string Description,
	bool TruckSensitive = false)
{
	/// <summary>
	/// Gets the route-segment occurrence this modifier applies to. A null value preserves
	/// compatibility for caller-supplied modifiers that intentionally apply to every occurrence.
	/// </summary>
	public int? RouteSegmentOccurrenceIndex { get; init; }

	/// <summary>Gets the validated canonical overlay descriptor for this modifier, if any.</summary>
	public LaneTopologyOverlayDescriptor? OverlaySource { get; init; }
}

public sealed record LaneFrictionProfile(
	int Score,
	int CanonicalPointCount,
	int RouteLaneChangeCount,
	int AdjacentMergeCount,
	IReadOnlyList<LaneFrictionContribution> Contributions,
	IReadOnlyList<LaneGuidancePoint> Guidance);

public sealed record LaneFrictionContribution(
	LaneFrictionContributionKind Kind,
	int Score,
	string SegmentId,
	int LaneNumber,
	string Description)
{
	/// <summary>Gets the validated canonical overlay descriptor for this contribution, if any.</summary>
	public LaneTopologyOverlayDescriptor? OverlaySource { get; init; }
}

public sealed record LaneGuidancePoint(
	string SegmentId,
	double DistanceAlongRouteMeters,
	string Instruction);

public enum LaneFrictionContributionKind
{
	RouteLaneChange = 0,
	ExitOnlyLane = 1,
	LaneDrop = 2,
	AdjacentMerge = 3,
	Weave = 4,
	RouteSplit = 5,
	TruckSensitiveConstraint = 6,
}

public enum LaneFrictionVehicleClass
{
	Car = 0,
	Truck = 1,
}
