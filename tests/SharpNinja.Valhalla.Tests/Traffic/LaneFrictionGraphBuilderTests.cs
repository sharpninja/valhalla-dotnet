using SharpNinja.Valhalla.Traffic.Routing;

namespace SharpNinja.Valhalla.Tests.Traffic;

public sealed class LaneFrictionGraphBuilderTests
{
	[Fact]
	public void BuildCanonicalPoints_DetectsExitOnlyLaneFromTurnIntent()
	{
		var points = LaneFrictionGraphBuilder.BuildCanonicalPoints(
		[
			new LaneTopologySegment(
				"I40-BNA-to-TN155",
				LaneCount: 3,
				LengthMeters: 1_200d,
				LaneIntents: [LaneTurnIntent.Right, LaneTurnIntent.Through, LaneTurnIntent.Through],
				IncomingConnections: Array.Empty<LaneTopologyConnection>()),
		]);

		var point = Assert.Single(points, static candidate => candidate.Kind == LaneFrictionContributionKind.ExitOnlyLane);
		Assert.Equal(LaneFrictionContributionKind.ExitOnlyLane, point.Kind);
		Assert.Equal(1, point.LaneNumber);
		Assert.Contains("does not continue through", point.Description, StringComparison.Ordinal);
	}

	[Fact]
	public void BuildCanonicalPoints_DetectsLaneDropFromConnectivityReduction()
	{
		var points = LaneFrictionGraphBuilder.BuildCanonicalPoints(
		[
			new LaneTopologySegment(
				"I40-TN155-merge",
				LaneCount: 2,
				LengthMeters: 900d,
				LaneIntents: [LaneTurnIntent.Through, LaneTurnIntent.Through],
				IncomingConnections:
				[
					new LaneTopologyConnection("TN155-ramp", FromLanes: [1, 2], ToLanes: [2]),
				],
				TruckSensitive: true),
		]);

		var point = Assert.Single(points);
		Assert.Equal(LaneFrictionContributionKind.LaneDrop, point.Kind);
		Assert.Equal(2, point.LaneNumber);
		Assert.True(point.TruckSensitive);
	}

	[Fact]
	public void BuildCanonicalPoints_DoesNotCreatePointsForNormalThroughLanes()
	{
		var points = LaneFrictionGraphBuilder.BuildCanonicalPoints(
		[
			new LaneTopologySegment(
				"I40-through",
				LaneCount: 3,
				LengthMeters: 1_000d,
				LaneIntents: [LaneTurnIntent.Through, LaneTurnIntent.Through, LaneTurnIntent.Through],
				IncomingConnections: [new LaneTopologyConnection("prev", FromLanes: [1, 2, 3], ToLanes: [1, 2, 3])]),
		]);

		Assert.Empty(points);
	}

	[Fact]
	public void BuildCanonicalPoints_GroupsDistinctIncomingSourcesByTargetLaneAsAdjacentMerge()
	{
		var points = LaneFrictionGraphBuilder.BuildCanonicalPoints(
		[
			new LaneTopologySegment(
				"target-edge",
				LaneCount: 3,
				LengthMeters: 800d,
				LaneIntents: [LaneTurnIntent.Through, LaneTurnIntent.Through, LaneTurnIntent.Through],
				IncomingConnections:
				[
					new LaneTopologyConnection("source-way-100", FromLanes: [2], ToLanes: [2]),
					new LaneTopologyConnection("source-way-200", FromLanes: [1], ToLanes: [2]),
					new LaneTopologyConnection("source-way-200", FromLanes: [1], ToLanes: [2]),
				]),
		]);

		CanonicalLaneFrictionPoint point = Assert.Single(points, static candidate => candidate.Kind == LaneFrictionContributionKind.AdjacentMerge);
		Assert.Equal(LaneFrictionContributionKind.AdjacentMerge, point.Kind);
		Assert.Equal(2, point.LaneNumber);
		Assert.Contains("2 distinct incoming source", point.Description, StringComparison.Ordinal);
	}

	[Fact]
	public void BuildCanonicalPoints_DistinguishesWithinSourceLaneDropFromAdjacentMerge()
	{
		var points = LaneFrictionGraphBuilder.BuildCanonicalPoints(
		[
			new LaneTopologySegment(
				"target-edge",
				LaneCount: 2,
				LengthMeters: 500d,
				LaneIntents: [LaneTurnIntent.Through, LaneTurnIntent.Through],
				IncomingConnections:
				[
					new LaneTopologyConnection("source-way-100", FromLanes: [1, 2], ToLanes: [2]),
				]),
		]);

		CanonicalLaneFrictionPoint point = Assert.Single(points, static candidate => candidate.Kind == LaneFrictionContributionKind.LaneDrop);
		Assert.Equal(LaneFrictionContributionKind.LaneDrop, point.Kind);
		Assert.DoesNotContain(points, static candidate => candidate.Kind == LaneFrictionContributionKind.AdjacentMerge);
	}


	[Fact]
	public void BuildCanonicalPoints_MixedThroughAndBranchLanes_EmitsRouteSplitForEveryLane()
	{
		var segments = new[]
		{
			new LaneTopologySegment(
				"split-segment",
				LaneCount: 3,
				LengthMeters: 400d,
				LaneIntents: [LaneTurnIntent.Right, LaneTurnIntent.Through, LaneTurnIntent.Through],
				IncomingConnections: [],
				TruckSensitive: true),
		};

		IReadOnlyList<CanonicalLaneFrictionPoint> points =
			LaneFrictionGraphBuilder.BuildCanonicalPoints(segments);

		Assert.Equal(
			[1, 2, 3],
			points
				.Where(static point => point.Kind == LaneFrictionContributionKind.RouteSplit)
				.Select(static point => point.LaneNumber)
				.ToArray());
		Assert.All(
			points.Where(static point => point.Kind == LaneFrictionContributionKind.RouteSplit),
			static point => Assert.True(point.TruckSensitive));
	}

	[Fact]
	public void BuildCanonicalPoints_DeduplicatesRepeatedSourceConnectivityRecords()
	{
		var points = LaneFrictionGraphBuilder.BuildCanonicalPoints(
		[
			new LaneTopologySegment(
				"target-edge",
				LaneCount: 2,
				LengthMeters: 500d,
				LaneIntents: [LaneTurnIntent.Through, LaneTurnIntent.Through],
				IncomingConnections:
				[
					new LaneTopologyConnection("source-way-100", FromLanes: [1, 2], ToLanes: [2]),
					new LaneTopologyConnection("source-way-100", FromLanes: [2, 1], ToLanes: [2]),
				]),
		]);

		Assert.Single(points);
	}

}
