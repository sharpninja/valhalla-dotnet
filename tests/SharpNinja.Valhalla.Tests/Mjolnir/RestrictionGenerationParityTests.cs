using System.Collections.Generic;

using SharpNinja.Valhalla.Baldr;
using SharpNinja.Valhalla.Mjolnir;

namespace SharpNinja.Valhalla.Tests.Mjolnir;

public sealed class RestrictionGenerationParityTests
{
    [Fact]
    public void RestrictionMatrix_MatchesOfficial()
    {
        var builder = new PbfGraphParserTests.PbfBuilder();
        ulong nodeId = 1;

        (ulong From, ulong To) AddSimpleRestriction(
            ulong relationId,
            ulong fromWayId,
            ulong toWayId,
            string restriction)
        {
            ulong first = nodeId++;
            ulong via = nodeId++;
            ulong last = nodeId++;
            builder.AddNode(first, 36.0 + (first * 0.0001), -86.0);
            builder.AddNode(via, 36.0 + (via * 0.0001), -86.0);
            builder.AddNode(last, 36.0 + (last * 0.0001), -86.0);
            builder.AddWay(fromWayId, new[] { first, via }, RoadTags());
            builder.AddWay(toWayId, new[] { via, last }, RoadTags());
            builder.AddRelation(
                relationId,
                new Dictionary<string, string>
                {
                    ["type"] = "restriction",
                    ["restriction"] = restriction,
                },
                new[]
                {
                    (fromWayId, OsmMemberType.Way, "from"),
                    (via, OsmMemberType.Node, "via"),
                    (toWayId, OsmMemberType.Way, "to"),
                });
            return (fromWayId, toWayId);
        }

        var simpleMatrix = new[]
        {
            (Relation: 900UL, From: 1000UL, To: 1001UL, Tag: "no_left_turn", Type: RestrictionType.NoLeftTurn),
            (Relation: 901UL, From: 1010UL, To: 1011UL, Tag: "no_right_turn", Type: RestrictionType.NoRightTurn),
            (Relation: 902UL, From: 1020UL, To: 1021UL, Tag: "no_straight_on", Type: RestrictionType.NoStraightOn),
            (Relation: 903UL, From: 1030UL, To: 1031UL, Tag: "no_u_turn", Type: RestrictionType.NoUTurn),
            (Relation: 904UL, From: 1040UL, To: 1041UL, Tag: "only_right_turn", Type: RestrictionType.OnlyRightTurn),
            (Relation: 905UL, From: 1050UL, To: 1051UL, Tag: "only_left_turn", Type: RestrictionType.OnlyLeftTurn),
            (Relation: 906UL, From: 1060UL, To: 1061UL, Tag: "only_straight_on", Type: RestrictionType.OnlyStraightOn),
            (Relation: 907UL, From: 1070UL, To: 1071UL, Tag: "no_entry", Type: RestrictionType.NoEntry),
            (Relation: 908UL, From: 1080UL, To: 1081UL, Tag: "no_exit", Type: RestrictionType.NoExit),
            (Relation: 909UL, From: 1090UL, To: 1091UL, Tag: "no_turn", Type: RestrictionType.NoTurn),
        };

        foreach (var item in simpleMatrix)
        {
            AddSimpleRestriction(item.Relation, item.From, item.To, item.Tag);
        }

        AddNodeChain(builder, 1200, 1201, 200, out ulong conditionalVia);
        builder.AddRelation(
            920,
            new Dictionary<string, string>
            {
                ["type"] = "restriction",
                ["restriction:conditional"] = "no_right_turn @ (Mo-Fr 07:00-09:00)",
            },
            SimpleMembers(1200, conditionalVia, 1201));

        AddNodeChain(builder, 1300, 1301, 210, out ulong truckVia);
        builder.AddRelation(
            921,
            new Dictionary<string, string>
            {
                ["type"] = "restriction",
                ["restriction:hgv"] = "no_u_turn",
            },
            SimpleMembers(1300, truckVia, 1301));

        AddNodeChain(builder, 1400, 1401, 220, out ulong exceptVia);
        builder.AddRelation(
            922,
            new Dictionary<string, string>
            {
                ["type"] = "restriction",
                ["restriction"] = "only_left_turn",
                ["except"] = "hgv",
            },
            SimpleMembers(1400, exceptVia, 1401));

        AddNodeChain(builder, 1500, 1501, 230, out ulong probableVia);
        builder.AddRelation(
            923,
            new Dictionary<string, string>
            {
                ["type"] = "restriction",
                ["restriction:probable"] = "only_right_turn @ probability=73",
            },
            SimpleMembers(1500, probableVia, 1501));

        AddMultiViaRestriction(builder);

        (PbfGraphParser parser, OSMData data) = PbfGraphParserTests.Run(builder);

        foreach (var item in simpleMatrix)
        {
            OSMRestriction restriction = Assert.Single(data.RestrictionsFor(item.From));
            Assert.Equal(item.Type, restriction.TypeValue());
            Assert.Equal(item.To, restriction.To());
            Assert.NotEqual(0UL, restriction.Via());
        }
        OSMRestriction conditional = Assert.Single(
            parser.ComplexRestrictionsFrom,
            restriction => restriction.From() == 1200);
        Assert.Equal(RestrictionType.NoRightTurn, conditional.TypeValue());
        Assert.NotEqual(0UL, conditional.TimeDomain());
        Assert.NotEqual(0u, conditional.Modes() & GraphConstants.AutoAccess);

        OSMRestriction truck = Assert.Single(
            parser.ComplexRestrictionsFrom,
            restriction => restriction.From() == 1300);
        Assert.Equal(RestrictionType.NoUTurn, truck.TypeValue());
        Assert.Equal((uint)GraphConstants.TruckAccess, truck.Modes());

        OSMRestriction exceptTruck = Assert.Single(
            parser.ComplexRestrictionsFrom,
            restriction => restriction.From() == 1400);
        Assert.Equal(RestrictionType.OnlyLeftTurn, exceptTruck.TypeValue());
        Assert.Equal(0u, exceptTruck.Modes() & GraphConstants.TruckAccess);
        Assert.NotEqual(0u, exceptTruck.Modes() & GraphConstants.AutoAccess);

        OSMRestriction probable = Assert.Single(
            parser.ComplexRestrictionsFrom,
            restriction => restriction.From() == 1500);
        Assert.Equal(RestrictionType.OnlyProbable, probable.TypeValue());
        Assert.Equal((byte)73, probable.Probability());

        OSMRestriction multiVia = Assert.Single(
            parser.ComplexRestrictionsFrom,
            restriction => restriction.From() == 1600);
        Assert.Equal(RestrictionType.NoTurn, multiVia.TypeValue());
        Assert.Equal(new ulong[] { 1601, 1602 }, multiVia.Vias());

        foreach (ulong fromWayId in new ulong[] { 1200, 1300, 1400, 1500, 1600 })
        {
            OSMRestriction forward = Assert.Single(
                parser.ComplexRestrictionsFrom,
                restriction => restriction.From() == fromWayId);
            OSMRestriction reverse = Assert.Single(
                parser.ComplexRestrictionsTo,
                restriction => restriction.To() == fromWayId && restriction.From() == forward.To());

            Assert.Equal(forward.Modes(), reverse.Modes());
        }
    }

    private static Dictionary<string, string> RoadTags() => new()
    {
        ["highway"] = "residential",
    };

    private static void AddNodeChain(
        PbfGraphParserTests.PbfBuilder builder,
        ulong fromWayId,
        ulong toWayId,
        ulong firstNodeId,
        out ulong viaNodeId)
    {
        viaNodeId = firstNodeId + 1;
        builder.AddNode(firstNodeId, 36.0 + (firstNodeId * 0.0001), -86.0);
        builder.AddNode(viaNodeId, 36.0 + (viaNodeId * 0.0001), -86.0);
        builder.AddNode(firstNodeId + 2, 36.0 + ((firstNodeId + 2) * 0.0001), -86.0);
        builder.AddWay(fromWayId, new[] { firstNodeId, viaNodeId }, RoadTags());
        builder.AddWay(toWayId, new[] { viaNodeId, firstNodeId + 2 }, RoadTags());
    }

    private static (ulong id, OsmMemberType type, string role)[] SimpleMembers(
        ulong fromWayId,
        ulong viaNodeId,
        ulong toWayId) =>
        new[]
        {
            (fromWayId, OsmMemberType.Way, "from"),
            (viaNodeId, OsmMemberType.Node, "via"),
            (toWayId, OsmMemberType.Way, "to"),
        };

    private static void AddMultiViaRestriction(PbfGraphParserTests.PbfBuilder builder)
    {
        builder.AddNode(300, 36.0300, -86.0);
        builder.AddNode(301, 36.0301, -86.0);
        builder.AddNode(302, 36.0302, -86.0);
        builder.AddNode(303, 36.0303, -86.0);
        builder.AddNode(304, 36.0304, -86.0);
        builder.AddWay(1600, new ulong[] { 300, 301 }, RoadTags());
        builder.AddWay(1601, new ulong[] { 301, 302 }, RoadTags());
        builder.AddWay(1602, new ulong[] { 302, 303 }, RoadTags());
        builder.AddWay(1603, new ulong[] { 303, 304 }, RoadTags());
        builder.AddRelation(
            924,
            new Dictionary<string, string>
            {
                ["type"] = "restriction",
                ["restriction"] = "no_turn",
            },
            new[]
            {
                (1600UL, OsmMemberType.Way, "from"),
                (1601UL, OsmMemberType.Way, "via"),
                (1602UL, OsmMemberType.Way, "via"),
                (1603UL, OsmMemberType.Way, "to"),
            });
    }
}
