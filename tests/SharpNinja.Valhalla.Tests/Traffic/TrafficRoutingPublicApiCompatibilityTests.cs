using System.Reflection;
using SharpNinja.Valhalla.Traffic;
using SharpNinja.Valhalla.Traffic.Routing;
using SharpNinja.Valhalla.Traffic.Tiles;

namespace SharpNinja.Valhalla.Tests.Traffic;

public sealed class TrafficRoutingPublicApiCompatibilityTests
{
    [Fact]
    public void PositionalConstructors_RemainSourceCompatible()
    {
        var request = new OsmRouteRequest(
            null,
            new GeoCoordinate(36.1263, -86.6774),
            new GeoCoordinate(36.1627, -86.7816));

        var candidate = new OsmRouteCandidate(
            1000d,
            60,
            null,
            Array.Empty<GeoCoordinate>(),
            Array.Empty<OsmRouteManeuver>(),
            new OsmRouteFrictionInputs(0, 0, 0, 0, false, false, false));

        PropertyInfo trafficSnapshot = RequireInitOnlyProperty(typeof(OsmRouteRequest), "TrafficSnapshot");
        PropertyInfo departureTime = RequireInitOnlyProperty(typeof(OsmRouteRequest), "DepartureTimeUtc");
        PropertyInfo durationSource = RequireInitOnlyProperty(typeof(OsmRouteCandidate), "DurationSource");
        PropertyInfo snapshotVersion = RequireInitOnlyProperty(typeof(OsmRouteCandidate), "TrafficSnapshotVersion");
        PropertyInfo engineDelay = RequireInitOnlyProperty(typeof(OsmRouteCandidate), "EngineAppliedTrafficDelaySeconds");

        Assert.NotNull(request);
        Assert.NotNull(candidate);
        Assert.Equal("SharpNinja.Valhalla.Traffic.Tiles.TrafficSnapshotReference", trafficSnapshot.PropertyType.FullName);
        Assert.Equal(typeof(DateTimeOffset?), departureTime.PropertyType);
        Assert.Equal(typeof(RouteDurationSource), durationSource.PropertyType);
        Assert.Equal(typeof(string), snapshotVersion.PropertyType);
        Assert.Equal(typeof(int), engineDelay.PropertyType);
    }

    [Fact]
    public void RouteSelectionCoordinatorContracts_ArePublicAndSourceConstructible()
    {
        var candidate = new OsmRouteCandidate(
            1_000d,
            60,
            null,
            [new GeoCoordinate(36.1263, -86.6774), new GeoCoordinate(36.1627, -86.7816)],
            [],
            new OsmRouteFrictionInputs(0, 0, 0, 0, false, false, false))
        {
            DirectedEdgeIds = [42],
        };
        var laneProjection = new RouteLaneFrictionProjection(
            true,
            false,
            [],
            [],
            new LaneFrictionProfile(0, 0, 0, 0, [], []),
            [])
        {
            FailureReason = LaneProjectionFailureReason.None,
        };
        var input = new RouteSelectionCandidateInput(
            0,
            candidate,
            new ValhallaRouteTrafficControlCounts(0, 0, 0, []),
            laneProjection,
            "valhalla");
        var snapshot = new NormalizedTrafficSnapshot(
            DateTimeOffset.UnixEpoch,
            [],
            [],
            [],
            [],
            null,
            [],
            []);
        var request = new RouteSelectionRequest(
            [input],
            snapshot,
            TrafficPolicy.Disabled,
            RoutePreferenceGoal.Fastest,
            RoutePreferenceWeights.Balanced);
        IRouteSelectionCoordinator coordinator = new RouteSelectionCoordinator();

        RouteSelectionResult result = coordinator.Select(request);

        Assert.True(typeof(RouteSelectionCandidateInput).IsPublic);
        Assert.True(typeof(RouteSelectionRequest).IsPublic);
        Assert.True(typeof(RouteSelectionResult).IsPublic);
        Assert.True(typeof(RouteSelectionRanking).IsPublic);
        Assert.True(typeof(RouteSelectionDecision).IsPublic);
        Assert.NotSame(candidate, result.Selected!.Candidate);
        Assert.Equal(candidate.DistanceMeters, result.Selected.Candidate.DistanceMeters);
        Assert.Equal(candidate.DirectedEdgeIds, result.Selected.Candidate.DirectedEdgeIds);
    }

    private static PropertyInfo RequireInitOnlyProperty(Type type, string name)
    {
        PropertyInfo property = type.GetProperty(name)
            ?? throw new Xunit.Sdk.XunitException($"{type.Name}.{name} is missing.");
        MethodInfo setter = property.SetMethod
            ?? throw new Xunit.Sdk.XunitException($"{type.Name}.{name} has no init accessor.");
        Assert.Contains(
            typeof(System.Runtime.CompilerServices.IsExternalInit),
            setter.ReturnParameter.GetRequiredCustomModifiers());
        return property;
    }
}
