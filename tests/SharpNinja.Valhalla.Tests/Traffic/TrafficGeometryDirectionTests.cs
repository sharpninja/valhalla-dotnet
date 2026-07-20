using SharpNinja.Valhalla.Traffic;
using SharpNinja.Valhalla.Traffic.Providers.Here;
using SharpNinja.Valhalla.Traffic.Providers.TomTom;

namespace SharpNinja.Valhalla.Tests.Traffic;

public sealed class TrafficGeometryDirectionTests
{
    private static readonly DateTimeOffset EvaluationTime =
        DateTimeOffset.Parse("2026-07-18T12:00:00Z");
    private static readonly DateTimeOffset FetchedAt =
        DateTimeOffset.Parse("2026-07-18T11:59:30Z");

    [Fact]
    public void Constructor_DefaultsUnknown_AndCopyPreservesExplicitDirection()
    {
        IReadOnlyList<GeoCoordinate> points =
        [
            new GeoCoordinate(36.1, -86.7),
            new GeoCoordinate(36.2, -86.7),
        ];

        var unknown = new TrafficGeometry(TrafficGeometryKind.LineString, points);
        var along = new TrafficGeometry(
            TrafficGeometryKind.LineString,
            points,
            TrafficGeometryDirection.AlongCoordinates);
        var both = new TrafficGeometry(
            TrafficGeometryKind.LineString,
            points,
            TrafficGeometryDirection.BothDirections);

        Assert.Equal(TrafficGeometryDirection.Unknown, unknown.Direction);
        Assert.Equal(TrafficGeometryDirection.AlongCoordinates, along.Copy().Direction);
        Assert.Equal(TrafficGeometryDirection.BothDirections, both.Copy().Direction);
    }

    [Fact]
    public async Task TomTomAdapter_DoesNotInventCoordinateDirectionForFlowOrClosure()
    {
        var adapter = new TomTomTrafficFeedAdapter();
        foreach ((TrafficFeedKind Kind, string File) fixture in
                 new[]
                 {
                     (TrafficFeedKind.Flow, "flow.json"),
                     (TrafficFeedKind.Closure, "closure.json"),
                 })
        {
            RawTrafficFeedPayload payload = TrafficNormalizationFixture.Load(
                "tomtom",
                fixture.Kind,
                "TomTom",
                fixture.File,
                FetchedAt);
            TrafficFeedNormalizationResult result = await adapter.NormalizeAsync(
                payload,
                new TrafficNormalizationContext(EvaluationTime),
                TestContext.Current.CancellationToken);

            Assert.NotEmpty(result.Events);
            Assert.All(
                result.Events,
                trafficEvent => Assert.Equal(
                    TrafficGeometryDirection.Unknown,
                    trafficEvent.Geometry.Direction));
        }
    }

    [Fact]
    public async Task HereAdapter_DoesNotInventCoordinateDirectionForFlowOrClosure()
    {
        var adapter = new HereTrafficFeedAdapter();
        foreach ((TrafficFeedKind Kind, string File) fixture in
                 new[]
                 {
                     (TrafficFeedKind.Flow, "flow.json"),
                     (TrafficFeedKind.Closure, "closure.json"),
                 })
        {
            RawTrafficFeedPayload payload = TrafficNormalizationFixture.Load(
                "here",
                fixture.Kind,
                "Here",
                fixture.File,
                FetchedAt);
            TrafficFeedNormalizationResult result = await adapter.NormalizeAsync(
                payload,
                new TrafficNormalizationContext(EvaluationTime),
                TestContext.Current.CancellationToken);

            Assert.NotEmpty(result.Events);
            Assert.All(
                result.Events,
                trafficEvent => Assert.Equal(
                    TrafficGeometryDirection.Unknown,
                    trafficEvent.Geometry.Direction));
        }
    }
}
