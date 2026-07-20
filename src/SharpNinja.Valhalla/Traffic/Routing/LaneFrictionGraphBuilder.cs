using System.Globalization;

namespace SharpNinja.Valhalla.Traffic.Routing;

/// <summary>
/// Builds reusable canonical lane-friction points from stable lane topology. Host
/// apps can populate the snapshots from Valhalla graph tiles, OSM way tags, or a
/// server-side cache without making the scoring model host-specific.
/// </summary>
public static class LaneFrictionGraphBuilder
{
	public static IReadOnlyList<CanonicalLaneFrictionPoint> BuildCanonicalPoints(IReadOnlyList<LaneTopologySegment> segments)
	{
		ArgumentNullException.ThrowIfNull(segments);

		var points = new List<CanonicalLaneFrictionPoint>();
		foreach (LaneTopologySegment segment in segments)
		{
			AddTurnIntentPoints(segment, points);
			AddRouteSplitPoints(segment, points);
			AddConnectivityPoints(segment, points);
		}

		return points
			.OrderBy(static point => point.SegmentId, StringComparer.Ordinal)
			.ThenBy(static point => point.DistanceAlongSegmentMeters)
			.ThenBy(static point => point.LaneNumber)
			.ToArray();
	}

	private static void AddTurnIntentPoints(LaneTopologySegment segment, List<CanonicalLaneFrictionPoint> points)
	{
		for (var index = 0; index < segment.LaneIntents.Count; index++)
		{
			LaneTurnIntent intent = segment.LaneIntents[index];
			int laneNumber = index + 1;
			if (intent == LaneTurnIntent.None || intent.HasFlag(LaneTurnIntent.Through))
			{
				continue;
			}

			LaneFrictionContributionKind kind = intent.HasFlag(LaneTurnIntent.MergeToLeft) || intent.HasFlag(LaneTurnIntent.MergeToRight)
				? LaneFrictionContributionKind.AdjacentMerge
				: LaneFrictionContributionKind.ExitOnlyLane;
			string description = kind == LaneFrictionContributionKind.AdjacentMerge
				? string.Format(CultureInfo.InvariantCulture, "Lane {0} has merge-only lane guidance on {1}.", laneNumber, segment.SegmentId)
				: string.Format(CultureInfo.InvariantCulture, "Lane {0} does not continue through {1}; it requires leaving or changing lanes.", laneNumber, segment.SegmentId);

			points.Add(new CanonicalLaneFrictionPoint(
				segment.SegmentId,
				laneNumber,
				segment.LengthMeters,
				kind,
				kind == LaneFrictionContributionKind.AdjacentMerge ? 7 : 9,
				description,
				TruckSensitive: segment.TruckSensitive));
		}
	}

	private static void AddRouteSplitPoints(
		LaneTopologySegment segment,
		List<CanonicalLaneFrictionPoint> points)
	{
		bool hasThroughLane = segment.LaneIntents.Any(static intent =>
			intent.HasFlag(LaneTurnIntent.Through));
		bool hasBranchLane = segment.LaneIntents.Any(static intent =>
			intent != LaneTurnIntent.None && !intent.HasFlag(LaneTurnIntent.Through));
		if (!hasThroughLane || !hasBranchLane)
		{
			return;
		}

		for (var laneNumber = 1; laneNumber <= segment.LaneCount; laneNumber++)
		{
			points.Add(new CanonicalLaneFrictionPoint(
				segment.SegmentId,
				laneNumber,
				segment.LengthMeters,
				LaneFrictionContributionKind.RouteSplit,
				5,
				string.Format(
					CultureInfo.InvariantCulture,
					"Lane {0} approaches a graph-derived through/branch route split on {1}.",
					laneNumber,
					segment.SegmentId),
				TruckSensitive: segment.TruckSensitive));
		}
	}

	private static void AddConnectivityPoints(LaneTopologySegment segment, List<CanonicalLaneFrictionPoint> points)
	{
		var normalized = segment.IncomingConnections
			.Select(connection => new
			{
				connection.FromSegmentId,
				FromLanes = NormalizeLanes(connection.FromLanes),
				ToLanes = NormalizeLanes(connection.ToLanes),
			})
			.Where(static connection => connection.FromLanes.Count > 0 && connection.ToLanes.Count > 0)
			.GroupBy(
				static connection => string.Concat(
					connection.FromSegmentId,
					":",
					string.Join(',', connection.FromLanes),
					">",
					string.Join(',', connection.ToLanes)),
				StringComparer.Ordinal)
			.Select(static group => group.First())
			.ToArray();

		foreach (var connection in normalized)
		{
			if (connection.FromLanes.Count <= connection.ToLanes.Count)
			{
				continue;
			}

			foreach (int toLane in connection.ToLanes)
			{
				points.Add(new CanonicalLaneFrictionPoint(
					segment.SegmentId,
					toLane,
					Math.Max(0d, segment.LengthMeters * 0.5d),
					LaneFrictionContributionKind.LaneDrop,
					10,
					string.Format(
						CultureInfo.InvariantCulture,
						"{0} lanes from source {1} reduce to {2} continuing lane(s) at lane {3} on {4}.",
						connection.FromLanes.Count,
						connection.FromSegmentId,
						connection.ToLanes.Count,
						toLane,
						segment.SegmentId),
					TruckSensitive: segment.TruckSensitive));
			}
		}

		foreach (var target in normalized
			.SelectMany(connection => connection.ToLanes.Select(toLane => new
			{
				ToLane = toLane,
				Source = connection.FromSegmentId,
			}))
			.Distinct()
			.GroupBy(static connection => connection.ToLane)
			.Where(static group => group.Select(connection => connection.Source).Distinct(StringComparer.Ordinal).Count() > 1))
		{
			int sourceCount = target
				.Select(static connection => connection.Source)
				.Distinct(StringComparer.Ordinal)
				.Count();
			points.Add(new CanonicalLaneFrictionPoint(
				segment.SegmentId,
				target.Key,
				Math.Max(0d, segment.LengthMeters * 0.5d),
				LaneFrictionContributionKind.AdjacentMerge,
				7,
				string.Format(
					CultureInfo.InvariantCulture,
					"{0} distinct incoming sources converge on lane {1} of {2}.",
					sourceCount,
					target.Key,
					segment.SegmentId),
				TruckSensitive: segment.TruckSensitive));
		}
	}

	private static IReadOnlyList<int> NormalizeLanes(IReadOnlyList<int> lanes)
		=> lanes.Where(static lane => lane > 0).Distinct().OrderBy(static lane => lane).ToArray();
}

public sealed record LaneTopologySegment(
	string SegmentId,
	int LaneCount,
	double LengthMeters,
	IReadOnlyList<LaneTurnIntent> LaneIntents,
	IReadOnlyList<LaneTopologyConnection> IncomingConnections,
	bool TruckSensitive = false)
{
	/// <summary>Gets the packed canonical Valhalla directed-edge GraphId when graph-backed.</summary>
	public ulong? CanonicalDirectedEdgeId { get; init; }

	/// <summary>Gets the OSM way identifier stored in the graph edge info when graph-backed.</summary>
	public ulong? OsmWayId { get; init; }

	/// <summary>Gets immutable graph evidence used for sound lane-transition derivation.</summary>
	public LaneTopologyGraphEvidence? GraphEvidence { get; init; }
}

public sealed record LaneTopologyConnection(
	string FromSegmentId,
	IReadOnlyList<int> FromLanes,
	IReadOnlyList<int> ToLanes);

[Flags]
public enum LaneTurnIntent
{
	None = 0,
	Through = 1 << 0,
	Left = 1 << 1,
	Right = 1 << 2,
	SlightLeft = 1 << 3,
	SlightRight = 1 << 4,
	MergeToLeft = 1 << 5,
	MergeToRight = 1 << 6,
	SharpLeft = 1 << 7,
	SharpRight = 1 << 8,
	Reverse = 1 << 9,
}
