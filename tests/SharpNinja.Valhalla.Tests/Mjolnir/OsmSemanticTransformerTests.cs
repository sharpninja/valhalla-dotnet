using SharpNinja.Valhalla.Baldr;
using SharpNinja.Valhalla.Mjolnir;

namespace SharpNinja.Valhalla.Tests.Mjolnir;

public sealed class OsmSemanticTransformerTests
{
    [Fact]
    public void OsmWaySemanticTransformer_Transform_ProducesLegacyEquivalentAcceptedOrRejectedWayTags()
    {
        VerifyWay(
            [1UL],
            new Dictionary<string, string> { ["highway"] = "residential" },
            expectedAccepted: false);

        VerifyWay(
            [1UL, 2UL, 1UL],
            new Dictionary<string, string>
            {
                ["highway"] = "service",
                ["building"] = "yes",
            },
            expectedAccepted: false);

        VerifyWay(
            [1UL, 2UL],
            new Dictionary<string, string>
            {
                ["highway"] = "residential",
                ["oneway"] = "yes",
            },
            expectedAccepted: true);
    }

    [Fact]
    public void OsmNodeSemanticTransformer_Transform_ProducesLegacyEquivalentControlTags()
    {
        var empty = OsmNodeSemanticTransformer.CreateEmptyTransformedTags();
        var expectedEmpty = new Dictionary<string, string>(StringComparer.Ordinal);
        NodeTagTransform.Transform(expectedEmpty);
        AssertTagsEqual(expectedEmpty, empty);

        var raw = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["highway"] = "traffic_signals",
            ["traffic_signals:direction"] = "forward",
        };
        var expected = new Dictionary<string, string>(raw, StringComparer.Ordinal);
        NodeTagTransform.Transform(expected);

        IReadOnlyDictionary<string, string> actual =
            OsmNodeSemanticTransformer.Transform(raw, empty);

        AssertTagsEqual(expected, actual);
        Assert.Equal("traffic_signals", raw["highway"]);
        Assert.Equal("forward", raw["traffic_signals:direction"]);
    }

    [Fact]
    public void OsmRelationSemanticTransformer_NormalizeRestrictionTags_MatchesLegacyParser()
    {
        var unrelated = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["type"] = "route",
            ["route"] = "road",
        };
        Assert.True(OsmRelationSemanticTransformer.TryNormalizeRestrictionTags(unrelated, out var unrelatedResult));
        Assert.Same(unrelated, unrelatedResult);

        Assert.False(
            OsmRelationSemanticTransformer.TryNormalizeRestrictionTags(
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["type"] = "multipolygon",
                    ["restriction"] = "no_left_turn",
                },
                out _));

        Assert.True(
            OsmRelationSemanticTransformer.TryNormalizeRestrictionTags(
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["type"] = "restriction",
                    ["restriction:hgv"] = "no_u_turn",
                },
                out var truck));
        Assert.Equal(((byte)RestrictionType.NoUTurn).ToString(), truck["restriction:hgv"]);
        Assert.False(truck.ContainsKey("restriction"));

        Assert.True(
            OsmRelationSemanticTransformer.TryNormalizeRestrictionTags(
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["type"] = "restriction",
                    ["restriction:conditional"] = "no_right_turn @ (Mo-Fr 07:00-09:00)",
                },
                out var conditional));
        Assert.Equal(((byte)RestrictionType.NoRightTurn).ToString(), conditional["restriction"]);
        Assert.Equal("(Mo-Fr 07:00-09:00)", conditional["restriction:conditional"]);

        Assert.True(
            OsmRelationSemanticTransformer.TryNormalizeRestrictionTags(
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["type"] = "route",
                    ["restriction:probable"] = "only_right_turn @ probability=73",
                },
                out var probable));
        Assert.Equal(((byte)RestrictionType.OnlyRightTurn).ToString(), probable["restriction"]);
        Assert.Equal("probability=73", probable["restriction:probable"]);
    }

    private static void VerifyWay(
        ulong[] nodeRefs,
        Dictionary<string, string> rawTags,
        bool expectedAccepted)
    {
        var originalTags = new Dictionary<string, string>(rawTags, StringComparer.Ordinal);
        var expectedTags = new Dictionary<string, string>(rawTags, StringComparer.Ordinal);
        bool expected = nodeRefs.Length >= 2 &&
                        !(nodeRefs[0] == nodeRefs[^1] &&
                          expectedTags.Keys.Any(static key =>
                              key is "building" or "landuse" or "leisure" or "natural"));

        if (expected)
        {
            expected = WayTagTransform.Transform(expectedTags) == 0 &&
                       expectedTags.Count != 0;
        }

        bool actual = OsmWaySemanticTransformer.TryTransform(
            nodeRefs,
            rawTags,
            out IReadOnlyDictionary<string, string>? actualTags);

        Assert.Equal(expectedAccepted, expected);
        Assert.Equal(expected, actual);
        if (expected)
        {
            Assert.NotNull(actualTags);
            AssertTagsEqual(expectedTags, actualTags);
        }
        else
        {
            Assert.Null(actualTags);
        }

        AssertTagsEqual(originalTags, rawTags);
    }

    private static void AssertTagsEqual(
        IReadOnlyDictionary<string, string> expected,
        IReadOnlyDictionary<string, string> actual)
    {
        Assert.Equal(expected.Count, actual.Count);
        foreach ((string key, string value) in expected)
        {
            Assert.True(actual.TryGetValue(key, out string? actualValue));
            Assert.Equal(value, actualValue);
        }
    }
}
