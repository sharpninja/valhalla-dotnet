using SharpNinja.Valhalla.Traffic.Routing;

namespace SharpNinja.Valhalla.Tests.Traffic;

public sealed class StructuralFrictionModelTests
{
    [Fact]
    public void Score_ReturnsNamedStructuralContributionsAndExplanations()
    {
        var input = new RouteStructuralFrictionInput(
            ManeuverCount: 10,
            TrafficSignalCount: 2,
            StopSignCount: 3,
            YieldSignCount: 1,
            RequiredRampMergeExitCount: 4,
            TollManeuverCount: 1,
            FerryManeuverCount: 1,
            HasToll: true,
            HasFerry: true);

        RouteStructuralFrictionScore score = FrictionModel.Score(input, TrafficPolicy.Disabled);

        Assert.Equal(score.Contributions.Sum(static contribution => contribution.Score), score.TotalScore);
        Assert.Equal(score.TotalScore, score.StaticScore);
        Assert.Equal(0, score.DynamicScore);
        Assert.Contains(score.Contributions, item => item.Kind == FrictionContributionKind.Maneuvers);
        Assert.Contains(score.Contributions, item => item.Kind == FrictionContributionKind.TrafficSignals);
        Assert.Contains(score.Contributions, item => item.Kind == FrictionContributionKind.StopSigns);
        Assert.Contains(score.Contributions, item => item.Kind == FrictionContributionKind.YieldSigns);
        Assert.Contains(score.Contributions, item => item.Kind == FrictionContributionKind.RequiredRampMergeExit);
        Assert.Contains(score.Contributions, item => item.Kind == FrictionContributionKind.Tolls);
        Assert.Contains(score.Contributions, item => item.Kind == FrictionContributionKind.Ferry);
        Assert.All(score.Contributions, item => Assert.False(string.IsNullOrWhiteSpace(item.Explanation)));
    }

    [Fact]
    public void Score_DoesNotApplyBlanketHighwayPenalty()
    {
        var input = new RouteStructuralFrictionInput(HighwayManeuverCount: 50);

        RouteStructuralFrictionScore score = FrictionModel.Score(input, TrafficPolicy.Disabled);

        Assert.Equal(0, score.TotalScore);
        Assert.DoesNotContain(score.Contributions, item => item.Kind == FrictionContributionKind.HighwayExposure);
    }

    [Fact]
    public void TrafficDisabled_ExcludesDynamicEvaluationFromStructuralFriction()
    {
        RouteTrafficEvaluation traffic = new(
            RouteKey: "edges:route",
            Policy: TrafficPolicy.Enabled,
            ObservedTrafficDelaySeconds: 300,
            TrafficDelaySeconds: 300,
            ObservedIncidentCount: 2,
            IncidentCount: 2,
            HasClosureHardDeny: false,
            HasRestrictionHardDeny: false,
            Impacts: [],
            Sources: [],
            AffectedEdges: []);
        var input = new RouteStructuralFrictionInput(
            ManeuverCount: 5,
            Traffic: traffic);

        RouteStructuralFrictionScore enabled = FrictionModel.Score(input, TrafficPolicy.Enabled);
        RouteStructuralFrictionScore disabled = FrictionModel.Score(input, TrafficPolicy.Disabled);

        Assert.True(enabled.DynamicScore > 0);
        Assert.Equal(0, disabled.DynamicScore);
        Assert.Equal(disabled.StaticScore, disabled.TotalScore);
        Assert.DoesNotContain(
            disabled.Contributions,
            item => item.Kind is FrictionContributionKind.TrafficDelay or FrictionContributionKind.Incidents);
    }
}
