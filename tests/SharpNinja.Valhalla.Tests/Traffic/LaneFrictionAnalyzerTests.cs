using SharpNinja.Valhalla.Traffic.Routing;

namespace SharpNinja.Valhalla.Tests.Traffic;

public sealed class LaneFrictionAnalyzerTests
{
	[Fact]
	public void Analyze_WithNoCanonicalPointsOrLaneChanges_DoesNotPenalizeFreeway()
	{
		var profile = LaneFrictionAnalyzer.Analyze(new LaneFrictionRequest(
			CanonicalPoints: Array.Empty<CanonicalLaneFrictionPoint>(),
			RouteSegments:
			[
				new RouteLaneSegment("I40-BNA-to-TN155", EntryLane: 2, ExitLane: 2, DistanceAlongRouteMeters: 0d),
			],
			VehicleClass: LaneFrictionVehicleClass.Car));

		Assert.Equal(0, profile.Score);
		Assert.Equal(0, profile.CanonicalPointCount);
		Assert.Equal(0, profile.RouteLaneChangeCount);
		Assert.Empty(profile.Contributions);
	}

	[Fact]
	public void Analyze_ChainsCanonicalLanePointsAndRouteSpecificLaneChanges()
	{
		var profile = LaneFrictionAnalyzer.Analyze(new LaneFrictionRequest(
			CanonicalPoints:
			[
				new CanonicalLaneFrictionPoint(
					"I40-BNA-to-TN155",
					LaneNumber: 2,
					DistanceAlongSegmentMeters: 450d,
					LaneFrictionContributionKind.ExitOnlyLane,
					Severity: 9,
					"Lane 1 is exit-only to TN-155 South; through traffic must hold or reach lane 2."),
				new CanonicalLaneFrictionPoint(
					"I40-TN155-to-Spence",
					LaneNumber: 2,
					DistanceAlongSegmentMeters: 300d,
					LaneFrictionContributionKind.AdjacentMerge,
					Severity: 7,
					"TN-155 merge traffic enters beside the route lane."),
				new CanonicalLaneFrictionPoint(
					"I40-Spence-to-I24",
					LaneNumber: 1,
					DistanceAlongSegmentMeters: 500d,
					LaneFrictionContributionKind.LaneDrop,
					Severity: 10,
					"Lane 1 drops toward Spence Lane before the I-40/I-24 merge."),
			],
			RouteSegments:
			[
				new RouteLaneSegment("I40-BNA-to-TN155", EntryLane: 1, ExitLane: 2, DistanceAlongRouteMeters: 0d),
				new RouteLaneSegment("I40-TN155-to-Spence", EntryLane: 2, ExitLane: 1, DistanceAlongRouteMeters: 1_100d),
				new RouteLaneSegment("I40-Spence-to-I24", EntryLane: 1, ExitLane: 2, DistanceAlongRouteMeters: 2_100d),
			],
			VehicleClass: LaneFrictionVehicleClass.Car));

		Assert.Equal(3, profile.RouteLaneChangeCount);
		Assert.Equal(3, profile.CanonicalPointCount);
		Assert.Equal(1, profile.AdjacentMergeCount);
		Assert.True(profile.Score >= 50);
		Assert.Contains(profile.Contributions, static contribution => contribution.Kind == LaneFrictionContributionKind.ExitOnlyLane);
		Assert.Contains(profile.Contributions, static contribution => contribution.Kind == LaneFrictionContributionKind.AdjacentMerge);
		Assert.Contains(profile.Guidance, static point => point.Instruction.Contains("Move from lane 1 to lane 2.", StringComparison.Ordinal));
	}

	[Fact]
	public void Analyze_ComplexMergeNetwork_ScoresMoreLaneChangesAndWeavesHigher()
	{
		CanonicalLaneFrictionPoint[] canonicalPoints =
		[
			new(
				"route-a-segment-1",
				LaneNumber: 3,
				DistanceAlongSegmentMeters: 1_800d,
				LaneFrictionContributionKind.AdjacentMerge,
				Severity: 8,
				"Adjacent merge traffic enters beside the selected route lane."),
			new(
				"route-a-segment-2",
				LaneNumber: 2,
				DistanceAlongSegmentMeters: 2_200d,
				LaneFrictionContributionKind.AdjacentMerge,
				Severity: 10,
				"Route traffic must cross toward lane 2 while merging traffic loads the adjacent lanes."),
			new(
				"route-a-segment-3",
				LaneNumber: 3,
				DistanceAlongSegmentMeters: 1_000d,
				LaneFrictionContributionKind.Weave,
				Severity: 12,
				"Route traffic must weave across diverging traffic to remain on the selected route."),

			new(
				"route-b-segment-2",
				LaneNumber: 4,
				DistanceAlongSegmentMeters: 6_000d,
				LaneFrictionContributionKind.AdjacentMerge,
				Severity: 10,
				"The continuous-lane alternative reaches the merge while lane 4 remains the continuing route lane."),
		];

		var complexRoute = LaneFrictionAnalyzer.Analyze(new LaneFrictionRequest(
			CanonicalPoints: canonicalPoints,
			RouteSegments:
			[
				new RouteLaneSegment("route-a-segment-1", EntryLane: 1, ExitLane: 3, DistanceAlongRouteMeters: 0d),
				new RouteLaneSegment("route-a-segment-2", EntryLane: 3, ExitLane: 2, DistanceAlongRouteMeters: 2_500d),
				new RouteLaneSegment("route-a-segment-3", EntryLane: 2, ExitLane: 3, DistanceAlongRouteMeters: 5_500d),
				new RouteLaneSegment("shared-final-segment", EntryLane: 3, ExitLane: 2, DistanceAlongRouteMeters: 7_500d),
			],
			VehicleClass: LaneFrictionVehicleClass.Car,
			RouteModifiers:
			[
				new RouteLaneFrictionModifier(
					"shared-final-segment",
					LaneNumber: 2,
					DistanceAlongSegmentMeters: 700d,
					LaneFrictionContributionKind.Weave,
					Severity: 21,
					"The complex route crosses three lanes of merging traffic to reach continuing lane 2."),
			]));
		var continuousRoute = LaneFrictionAnalyzer.Analyze(new LaneFrictionRequest(
			CanonicalPoints: canonicalPoints,
			RouteSegments:
			[
				new RouteLaneSegment("route-b-segment-1", EntryLane: 1, ExitLane: 4, DistanceAlongRouteMeters: 0d),
				new RouteLaneSegment("route-b-segment-2", EntryLane: 4, ExitLane: 4, DistanceAlongRouteMeters: 4_000d),
				new RouteLaneSegment("shared-final-segment", EntryLane: 2, ExitLane: 2, DistanceAlongRouteMeters: 8_500d),
			],
			VehicleClass: LaneFrictionVehicleClass.Car));

		Assert.Equal(91, complexRoute.Score);
		Assert.Equal(34, continuousRoute.Score);
		Assert.Equal(5, complexRoute.RouteLaneChangeCount);
		Assert.Equal(3, continuousRoute.RouteLaneChangeCount);
		Assert.True(complexRoute.Score > continuousRoute.Score, $"Expected the complex merge route friction {complexRoute.Score} to exceed the continuous-lane route friction {continuousRoute.Score}.");
		Assert.True(complexRoute.RouteLaneChangeCount > continuousRoute.RouteLaneChangeCount);
		Assert.Contains(complexRoute.Contributions, static contribution => contribution.Description.Contains("crosses three lanes of merging traffic", StringComparison.Ordinal));
		Assert.DoesNotContain(continuousRoute.Contributions, static contribution => contribution.Description.Contains("crosses three lanes of merging traffic", StringComparison.Ordinal));
		Assert.Contains(continuousRoute.Guidance, static point => point.Instruction.Contains("Move from lane 1 to lane 4.", StringComparison.Ordinal));
	}

	[Fact]
	public void Analyze_AppliesTruckModifierToTruckSensitiveCanonicalPoints()
	{
		var canonical = new[]
		{
			new CanonicalLaneFrictionPoint(
				"I40-I24-merge",
				LaneNumber: 2,
				DistanceAlongSegmentMeters: 200d,
				LaneFrictionContributionKind.AdjacentMerge,
				Severity: 8,
				"I-24 merge traffic enters the route lane from the left.",
				TruckSensitive: true),
		};
		var routeSegments = new[]
		{
			new RouteLaneSegment("I40-I24-merge", EntryLane: 2, ExitLane: 2, DistanceAlongRouteMeters: 0d),
		};

		var car = LaneFrictionAnalyzer.Analyze(new LaneFrictionRequest(canonical, routeSegments, LaneFrictionVehicleClass.Car));
		var truck = LaneFrictionAnalyzer.Analyze(new LaneFrictionRequest(canonical, routeSegments, LaneFrictionVehicleClass.Truck));

		Assert.Equal(8, car.Score);
		Assert.True(truck.Score > car.Score);
		Assert.Equal(14, truck.Score);
	}
}
