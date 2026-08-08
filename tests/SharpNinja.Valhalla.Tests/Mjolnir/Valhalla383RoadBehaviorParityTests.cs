using SharpNinja.Valhalla.Mjolnir;

namespace SharpNinja.Valhalla.Tests.Mjolnir;

public sealed class GraphEnhancerParityTests
{
    [Fact]
    public void DefaultSpeedAndAccessMatrix_MatchesOfficial()
    {
        Dictionary<string, string> residential = Transform(("highway", "residential"));
        Assert.Equal("35", residential["default_speed"]);
        Assert.Equal("6", residential["road_class"]);
        Assert.Equal("true", residential["auto_forward"]);
        Assert.Equal("true", residential["truck_forward"]);
        Assert.Equal("true", residential["pedestrian_forward"]);

        Dictionary<string, string> motorway = Transform(("highway", "motorway"));
        Assert.Equal("105", motorway["default_speed"]);
        Assert.Equal("0", motorway["road_class"]);
        Assert.Equal("true", motorway["auto_forward"]);
        Assert.Equal("true", motorway["truck_forward"]);
        Assert.Equal("false", motorway["pedestrian_forward"]);

        Dictionary<string, string> busOnly = Transform(
            ("highway", "residential"),
            ("access", "no"),
            ("bus", "yes"));
        Assert.Equal("false", busOnly["auto_forward"]);
        Assert.Equal("false", busOnly["truck_forward"]);
        Assert.Equal("true", busOnly["bus_forward"]);
    }

    private static Dictionary<string, string> Transform(params (string Key, string Value)[] values)
    {
        var tags = values.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        Assert.Equal(0, WayTagTransform.Transform(tags));
        return tags;
    }
}

public sealed class LinkAndFerryParityTests
{
    [Fact]
    public void ManagedPipeline_MatchesOfficialClassification()
    {
        var motorwayLink = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["highway"] = "motorway_link",
        };
        Assert.Equal(0, WayTagTransform.Transform(motorwayLink));
        Assert.Equal("true", motorwayLink["link"]);
        Assert.Equal("0", motorwayLink["road_class"]);

        var ferry = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["route"] = "ferry",
        };
        Assert.Equal(0, WayTagTransform.Transform(ferry));
        Assert.Equal("true", ferry["ferry"]);
        Assert.Equal("2", ferry["road_class"]);
        Assert.Equal("75", ferry["default_speed"]);
        Assert.Equal("true", ferry["auto_forward"]);
        Assert.Equal("true", ferry["auto_backward"]);
    }
}

public sealed class PedestrianAreaParityTests
{
    [Fact]
    public void PedestrianAreaTransform_IsRetainedAndMarked()
    {
        var tags = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["highway"] = "pedestrian",
            ["area"] = "yes",
        };

        Assert.Equal(0, WayTagTransform.Transform(tags));
        Assert.Equal("true", tags["pedestrian_area"]);
    }

    [Fact]
    public void NonPedestrianAreaTransform_RemainsFiltered()
    {
        var tags = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["highway"] = "residential",
            ["area"] = "yes",
        };

        Assert.Equal(1, WayTagTransform.Transform(tags));
    }
}
