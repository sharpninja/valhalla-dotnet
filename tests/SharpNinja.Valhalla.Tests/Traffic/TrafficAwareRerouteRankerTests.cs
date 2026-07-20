using SharpNinja.Valhalla.Traffic.Routing;

namespace SharpNinja.Valhalla.Tests.Traffic;

public sealed class TrafficAwareRerouteRankerTests
{
    [Fact]
    public void TrafficDisabled_ExcludesDelayFromEta()
    {
        var delayedFastRoute = Candidate(index: 0, durationSeconds: 600, trafficDelaySeconds: 300);
        var slowerRoute = Candidate(index: 1, durationSeconds: 700, trafficDelaySeconds: 0);

        int selected = TrafficAwareRerouteRanker.PickBestRouteIndex(
            [delayedFastRoute, slowerRoute],
            TrafficPolicy.Disabled);

        Assert.Equal(0, selected);
        Assert.Equal(600, TrafficAwareRerouteRanker.AdjustedEtaSeconds(delayedFastRoute, TrafficPolicy.Disabled));
    }

    [Fact]
    public void TrafficEnabled_IncludesDelayInEta()
    {
        var delayedFastRoute = Candidate(index: 0, durationSeconds: 600, trafficDelaySeconds: 300);
        var slowerRoute = Candidate(index: 1, durationSeconds: 700, trafficDelaySeconds: 0);

        int selected = TrafficAwareRerouteRanker.PickBestRouteIndex(
            [delayedFastRoute, slowerRoute],
            TrafficPolicy.Enabled);

        Assert.Equal(1, selected);
        Assert.Equal(900, TrafficAwareRerouteRanker.AdjustedEtaSeconds(delayedFastRoute, TrafficPolicy.Enabled));
    }

    [Fact]
    public void TrafficTileAdjustedBaseDuration_DoesNotDoubleCountDelay()
    {
        var candidate = Candidate(
            index: 0,
            durationSeconds: 900,
            trafficDelaySeconds: 300,
            durationSource: RouteDurationSource.ValhallaTrafficTileAdjusted);

        int eta = TrafficAwareRerouteRanker.AdjustedEtaSeconds(candidate, TrafficPolicy.Enabled);

        Assert.Equal(900, eta);
    }

    [Fact]
    public void AdjustedEtaSeconds_OverflowSaturatesAtIntMaxValue()
    {
        var candidate = Candidate(
            index: 0,
            durationSeconds: int.MaxValue - 5,
            trafficDelaySeconds: 10);

        int eta = TrafficAwareRerouteRanker.AdjustedEtaSeconds(
            candidate,
            TrafficPolicy.Enabled);

        Assert.Equal(int.MaxValue, eta);
    }

    private static RouteCandidateMetrics Candidate(
        int index,
        int durationSeconds,
        int trafficDelaySeconds,
        RouteDurationSource durationSource = RouteDurationSource.FreeFlow)
        => new(
            ProviderId: "valhalla",
            Index: index,
            DistanceMeters: 10_000,
            DurationSeconds: durationSeconds,
            TrafficDelaySeconds: trafficDelaySeconds,
            DurationSource: durationSource,
            DirectedEdgeIds: [(ulong)(index + 1)]);
}
