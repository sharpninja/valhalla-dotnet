using SharpNinja.Valhalla.Baldr;
using SharpNinja.Valhalla.Odin;
using SharpNinja.Valhalla.Thor;

namespace SharpNinja.Valhalla.Tests.Osm;

public sealed class EmbeddedValhallaRoutingClientDirectedEdgeIdTests
{
    [Fact]
    public void MapCandidate_PrimaryRoute_PreservesOrderedCanonicalDirectedEdgeIds()
    {
        GraphId first = new(tileid: 17, level: 2, id: 41);
        GraphId second = new(tileid: 17, level: 2, id: 9);
        GraphId third = new(tileid: 23, level: 1, id: 3);

        OsmRouteCandidate candidate = EmbeddedValhallaRoutingClient.MapCandidate(
            Trip(first, second, third),
            new DirectionsLeg());

        Assert.Equal(
            new[] { first.Value, second.Value, third.Value },
            candidate.DirectedEdgeIds);
    }

    [Fact]
    public void MapCandidate_AlternateRoute_PreservesIndependentOrderedCanonicalDirectedEdgeIds()
    {
        GraphId sharedStart = new(tileid: 31, level: 2, id: 7);
        GraphId primaryMiddle = new(tileid: 31, level: 2, id: 11);
        GraphId alternateMiddle = new(tileid: 44, level: 1, id: 5);
        GraphId sharedEnd = new(tileid: 52, level: 2, id: 19);

        OsmRouteCandidate primary = EmbeddedValhallaRoutingClient.MapCandidate(
            Trip(sharedStart, primaryMiddle, sharedEnd),
            new DirectionsLeg());
        OsmRouteCandidate alternate = EmbeddedValhallaRoutingClient.MapCandidate(
            Trip(sharedStart, alternateMiddle, sharedEnd),
            new DirectionsLeg());

        Assert.Equal(
            new[] { sharedStart.Value, primaryMiddle.Value, sharedEnd.Value },
            primary.DirectedEdgeIds);
        Assert.Equal(
            new[] { sharedStart.Value, alternateMiddle.Value, sharedEnd.Value },
            alternate.DirectedEdgeIds);
        Assert.NotEqual(primary.DirectedEdgeIds, alternate.DirectedEdgeIds);
    }

    [Fact]
    public void OsmRouteCandidate_LegacyConstructorAndDeconstruction_RemainSourceCompatible()
    {
        var candidate = new OsmRouteCandidate(
            DistanceMeters: 1,
            DurationSeconds: 1,
            EncodedPolyline: null,
            RoutePoints: Array.Empty<GeoCoordinate>(),
            Maneuvers: Array.Empty<OsmRouteManeuver>(),
            FrictionInputs: new OsmRouteFrictionInputs(0, 0, 0, 0, false, false, false));

        var (distanceMeters, durationSeconds, encodedPolyline, routePoints, maneuvers, frictionInputs) =
            candidate;

        Assert.Equal(1, distanceMeters);
        Assert.Equal(1, durationSeconds);
        Assert.Null(encodedPolyline);
        Assert.Empty(routePoints);
        Assert.Empty(maneuvers);
        Assert.Equal(0, frictionInputs.ManeuverCount);
        Assert.Null(candidate.DirectedEdgeIds);
    }

    private static TripLeg Trip(params GraphId[] directedEdgeIds)
    {
        var leg = new TripLeg();
        foreach (GraphId directedEdgeId in directedEdgeIds)
        {
            leg.Edges.Add(new TripEdge
            {
                EdgeId = directedEdgeId,
                LengthKm = 1,
            });
        }

        return leg;
    }
}
