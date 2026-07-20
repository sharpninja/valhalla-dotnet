using SharpNinja.Valhalla.Traffic.Routing;

namespace SharpNinja.Valhalla.Tests.Traffic;

public sealed class FrictionModelTrafficPolicyTests
{
    [Fact]
    public void TrafficDisabled_ExcludesDelayFromFriction()
    {
        var candidate = Candidate(RouteDurationSource.FreeFlow);

        RouteFrictionScore score = FrictionModel.Score(candidate, TrafficPolicy.Disabled);

        Assert.Equal(0, score.TrafficDelaySeconds);
        Assert.Equal(0, score.IncidentPenaltySeconds);
    }

    [Fact]
    public void TrafficEnabled_IncludesDelayInFriction()
    {
        var candidate = Candidate(RouteDurationSource.FreeFlow);

        RouteFrictionScore score = FrictionModel.Score(candidate, TrafficPolicy.Enabled);

        Assert.Equal(180, score.TrafficDelaySeconds);
        Assert.True(score.IncidentPenaltySeconds > 0);
    }

    [Fact]
    public void TrafficTileAdjustedBaseDuration_PreservesDelayInFriction()
    {
        var candidate = Candidate(RouteDurationSource.ValhallaTrafficTileAdjusted);

        RouteFrictionScore score = FrictionModel.Score(candidate, TrafficPolicy.Enabled);

        Assert.Equal(180, score.TrafficDelaySeconds);
        Assert.Equal(
            FrictionModel.Score(Candidate(RouteDurationSource.FreeFlow), TrafficPolicy.Enabled).TotalCost,
            score.TotalCost);
    }


    [Fact]
    public void NoTraffic_DurationAndDistanceDoNotCreateFriction()
    {
        var candidate = new RouteCandidateMetrics(
            ProviderId: "valhalla",
            Index: 0,
            DistanceMeters: 100_000,
            DurationSeconds: 7_200,
            StaticFrictionScore: 12);

        RouteFrictionScore score = FrictionModel.Score(candidate, TrafficPolicy.Disabled);

        Assert.Equal(12, score.TotalCost);
    }

    private static RouteCandidateMetrics Candidate(RouteDurationSource durationSource)
        => new(
            ProviderId: "valhalla",
            Index: 0,
            DistanceMeters: 5_000,
            DurationSeconds: 600,
            TrafficDelaySeconds: 180,
            IncidentCount: 1,
            DurationSource: durationSource,
            DirectedEdgeIds: [10, 11, 12]);
}
