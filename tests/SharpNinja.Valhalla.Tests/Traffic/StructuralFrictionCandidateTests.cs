using SharpNinja.Valhalla.Odin;
using SharpNinja.Valhalla.Traffic.Routing;
using SharpNinja.Valhalla.Traffic.Tiles;

namespace SharpNinja.Valhalla.Tests.Traffic;

public sealed class StructuralFrictionCandidateTests
{
    [Fact]
    public void Score_CandidateOverloadCountsEveryRequiredRampExitMergeAndStayManeuver()
    {
        DirectionsLegManeuverType[] requiredTypes =
        [
            DirectionsLegManeuverType.RampStraight,
            DirectionsLegManeuverType.RampRight,
            DirectionsLegManeuverType.RampLeft,
            DirectionsLegManeuverType.ExitRight,
            DirectionsLegManeuverType.ExitLeft,
            DirectionsLegManeuverType.StayStraight,
            DirectionsLegManeuverType.StayRight,
            DirectionsLegManeuverType.StayLeft,
            DirectionsLegManeuverType.Merge,
            DirectionsLegManeuverType.MergeRight,
            DirectionsLegManeuverType.MergeLeft,
        ];
        OsmRouteCandidate route = CreateRoute(requiredTypes);
        var controls = new ValhallaRouteTrafficControlCounts(2, 3, 4, []);
        RouteTrafficEvaluation traffic = EmptyTraffic();

        RouteStructuralFrictionScore score = FrictionModel.Score(
            route,
            controls,
            traffic,
            TrafficPolicy.Disabled);

        RouteFrictionContribution contribution = Assert.Single(
            score.Contributions,
            static item => item.Kind == FrictionContributionKind.RequiredRampMergeExit);
        Assert.Equal(requiredTypes.Length, contribution.Count);
        Assert.DoesNotContain(
            score.Contributions,
            static item => item.Kind == FrictionContributionKind.HighwayExposure);
    }

    [Fact]
    public void Score_LaneProfileComposesIntoStaticFrictionAndExposesGuidance()
    {
        var guidance = new LaneGuidancePoint("edge-1", 125d, "Move from lane 1 to lane 2.");
        var profile = new LaneFrictionProfile(
            Score: 23,
            CanonicalPointCount: 1,
            RouteLaneChangeCount: 1,
            AdjacentMergeCount: 0,
            Contributions:
            [
                new LaneFrictionContribution(
                    LaneFrictionContributionKind.RouteLaneChange,
                    23,
                    "edge-1",
                    1,
                    "route-specific lane change"),
            ],
            Guidance: [guidance]);

        RouteStructuralFrictionScore score = FrictionModel.Score(
            CreateRoute([]),
            new ValhallaRouteTrafficControlCounts(0, 0, 0, []),
            EmptyTraffic(),
            profile,
            TrafficPolicy.Disabled);

        RouteFrictionContribution contribution = Assert.Single(
            score.Contributions,
            static item => item.Kind == FrictionContributionKind.LaneTopology);
        Assert.Equal(23d, contribution.Score);
        Assert.Equal(score.TotalScore, score.StaticScore);
        Assert.Same(guidance, Assert.Single(score.LaneGuidance));
    }

    private static OsmRouteCandidate CreateRoute(IReadOnlyList<DirectionsLegManeuverType> types)
    {
        OsmRouteManeuver[] maneuvers = types
            .Select(static type => new OsmRouteManeuver(
                (int)type,
                type.ToString(),
                100d,
                10,
                0,
                1))
            .ToArray();
        return new OsmRouteCandidate(
            1_000d,
            100,
            null,
            [],
            maneuvers,
            new OsmRouteFrictionInputs(
                maneuvers.Length,
                TollManeuverCount: 0,
                HighwayManeuverCount: 99,
                FerryManeuverCount: 0,
                HasToll: false,
                HasHighway: true,
                HasFerry: false));
    }

    private static RouteTrafficEvaluation EmptyTraffic()
        => new(
            "edges:test",
            TrafficPolicy.Disabled,
            0,
            0,
            0,
            0,
            false,
            false,
            [],
            [],
            []);
}
