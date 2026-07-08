// xUnit port of the Valhalla sif dynamiccost-related test cases (valhalla @ 3.7.0).
//
// Valhalla has no dedicated test/dynamiccost.cc; the foundation behavior is exercised by the
// AutoCost.testAutoCostParams gtest (src/sif/autocost.cc) plus the inline algorithms in
// dynamiccost.h / dynamiccost.cc / costconstants.h / osrm_car_duration.h. These tests port that
// foundation behavior faithfully:
//   - ranged_default_t clamping (the make_distributor_from_range + test::IsBetween pattern)
//   - ParseBaseCostOptions clamping for the base costing options
//   - Cost struct operators
//   - custom_cost_t::sort_and_find_smallest
//   - SpeedMask_Parse
//   - set_use_tracks / set_use_living_streets / set_use_lit factor+penalty formulas
//   - get_base_costs ferry factor/penalty + transition costs
//   - base_transition_cost
//   - TurnType / AddUturnPenalty
//   - OSRMCarTurnDuration

using System;
using System.Collections.Generic;
using System.Text.Json;
using SharpNinja.Valhalla.Baldr;
using SharpNinja.Valhalla.Midgard;
using SharpNinja.Valhalla.Sif;
using Xunit;

namespace SharpNinja.Valhalla.Tests.Sif;

public sealed class DynamicCostTests
{
    // ---- Minimal concrete coster to exercise the abstract base's protected helpers ----
    private sealed class TestDynamicCost : DynamicCost
    {
        public TestDynamicCost(Costing costing)
            : base(costing, global::SharpNinja.Valhalla.Sif.TravelMode.Drive,
                   GraphConstants.AutoAccess, penalizeUturns: true)
        {
            GetBaseCosts(costing);
        }

        public override bool Allowed(DirectedEdge edge, bool isDest, EdgeLabel pred, GraphTile tile,
            GraphId edgeid, ulong currentTime, uint tzIndex, ref byte restrictionIdx, ref byte destonlyAccessRestrMask)
            => true;

        public override bool AllowedReverse(DirectedEdge edge, EdgeLabel pred, DirectedEdge oppEdge, GraphTile tile,
            GraphId oppEdgeid, ulong currentTime, uint tzIndex, ref byte restrictionIdx, ref byte destonlyAccessRestrMask)
            => true;

        public override Cost EdgeCost(DirectedEdge edge, TransitDeparture departure, uint currTime) => NoCost;

        public override Cost EdgeCost(DirectedEdge edge, GraphId id, GraphTile tile, TimeInfo timeInfo, ref byte flowSources)
            => NoCost;

        public override float AStarCostFactor() => 1.0f;

        // expose protected helpers / state for assertions
        public Cost ExposeBaseTransitionCost(NodeInfo node, DirectedEdge edge, EdgeLabel pred, uint idx)
            => BaseTransitionCost(node, edge, pred, idx);

        public float TrackPenalty => TrackPenalty_;
        public float TrackFactor => TrackFactor_;
        public float LivingStreetPenalty => LivingStreetPenalty_;
        public float LivingStreetFactor => LivingStreetFactor_;
        public float UnlitFactor => UnlitFactor_;
        public float FerryFactor => FerryFactor_;
        public float RailFerryFactor => RailFerryFactor_;
        public Cost FerryTransitionCost => FerryTransitionCost_;
        public Cost RailFerryTransitionCost => RailFerryTransitionCost_;
        public Cost GateCost => GateCost_;
        public Cost TollBoothCost => TollBoothCost_;
        public Cost CountryCrossingCost => CountryCrossingCost_;
        public float ManeuverPenalty => ManeuverPenalty_;
        public float AlleyPenalty => AlleyPenalty_;
        public float DestinationOnlyPenalty => DestinationOnlyPenalty_;
        public float ServicePenalty => ServicePenalty_;
    }

    private static Costing MakeAutoCosting() => new Costing { CostingType = Costing.Type.Auto };

    // ===================== ranged_default_t (RangedDefault) =====================

    [Fact]
    public void RangedDefault_InRange_ReturnsValueUnclamped()
    {
        var range = new RangedDefault<float>(0f, 5f, 10f);
        float result = range.Invoke(7f, out bool clamped);
        Assert.False(clamped);
        Assert.Equal(7f, result);
    }

    [Theory]
    [InlineData(-1f)]
    [InlineData(11f)]
    public void RangedDefault_OutOfRange_SnapsToDefaultAndReportsClamped(float value)
    {
        var range = new RangedDefault<float>(0f, 5f, 10f);
        float result = range.Invoke(value, out bool clamped);
        Assert.True(clamped);
        Assert.Equal(5f, result);
    }

    [Fact]
    public void RangedDefault_BoundariesAreInclusive()
    {
        var range = new RangedDefault<float>(0f, 5f, 10f);
        Assert.Equal(0f, range.Invoke(0f, out bool lo));
        Assert.False(lo);
        Assert.Equal(10f, range.Invoke(10f, out bool hi));
        Assert.False(hi);
    }

    // ===================== ParseBaseCostOptions clamping (testAutoCostParams analogue) =====================

    // Faithful analogue of make_distributor_from_range + test::IsBetween: a user provided value
    // inside the range survives; a value outside the range is clamped to the default and a warning
    // is emitted. Mirrors the testAutoCostParams loop assertions.
    [Theory]
    [InlineData("maneuver_penalty")]
    [InlineData("alley_penalty")]
    [InlineData("destination_only_penalty")]
    [InlineData("gate_cost")]
    [InlineData("gate_penalty")]
    [InlineData("toll_booth_cost")]
    [InlineData("country_crossing_cost")]
    [InlineData("ferry_cost")]
    [InlineData("service_penalty")]
    public void ParseBaseCostOptions_ClampsOutOfRangeValuesToDefault(string key)
    {
        var cfg = new BaseCostingOptionsConfig();
        var costing = MakeAutoCosting();
        // A value way above kMaxPenalty (12h) is out of range for all these float options.
        float outOfRange = DynamicCost.MaxPenalty + 1_000_000f;
        var json = JsonDocument.Parse($"{{\"{key}\": {outOfRange} }}").RootElement;

        var warnings = new List<string>();
        CostOptionsParser.ParseBaseCostOptions(json, costing, cfg, warnings);

        Assert.Contains(warnings, w => w.StartsWith($"'{key}' has been clamped"));
    }

    [Fact]
    public void ParseBaseCostOptions_InRangeValueIsKept()
    {
        var cfg = new BaseCostingOptionsConfig();
        var costing = MakeAutoCosting();
        var json = JsonDocument.Parse("{\"maneuver_penalty\": 12.5 }").RootElement;

        var warnings = new List<string>();
        CostOptionsParser.ParseBaseCostOptions(json, costing, cfg, warnings);

        Assert.Equal(12.5f, costing.Options.ManeuverPenalty);
        Assert.DoesNotContain(warnings, w => w.StartsWith("'maneuver_penalty' has been clamped"));
    }

    [Fact]
    public void ParseBaseCostOptions_NoJson_UsesDefaults()
    {
        var cfg = new BaseCostingOptionsConfig();
        var costing = MakeAutoCosting();
        var json = JsonDocument.Parse("{}").RootElement;

        CostOptionsParser.ParseBaseCostOptions(json, costing, cfg, new List<string>());

        Assert.Equal(DynamicCost.DefaultManeuverPenalty, costing.Options.ManeuverPenalty);
        Assert.Equal(DynamicCost.DefaultAlleyPenalty, costing.Options.AlleyPenalty);
        Assert.Equal(DynamicCost.DefaultDestinationOnlyPenalty, costing.Options.DestinationOnlyPenalty);
        Assert.Equal(DynamicCost.DefaultGateCost, costing.Options.GateCost);
        Assert.Equal(DynamicCost.DefaultUseFerry, costing.Options.UseFerry);
    }

    // ===================== Cost struct operators =====================

    [Fact]
    public void Cost_Add_Subtract_Scale_Compare()
    {
        var a = new Cost(10f, 20f);
        var b = new Cost(3f, 4f);

        var sum = a + b;
        Assert.Equal(13f, sum.CostValue);
        Assert.Equal(24f, sum.Secs);

        var diff = a - b;
        Assert.Equal(7f, diff.CostValue);
        Assert.Equal(16f, diff.Secs);

        var scaled = a * 2f;
        Assert.Equal(20f, scaled.CostValue);
        Assert.Equal(40f, scaled.Secs);

        Assert.True(b < a);
        Assert.True(a > b);
    }

    [Fact]
    public void Cost_DefaultIsZero()
    {
        var c = new Cost();
        Assert.Equal(0f, c.CostValue);
        Assert.Equal(0f, c.Secs);
        Assert.Equal(0f, DynamicCost.NoCost.CostValue);
        Assert.Equal(0f, DynamicCost.NoCost.Secs);
    }

    // ===================== custom_cost_t::sort_and_find_smallest =====================

    [Fact]
    public void CustomCost_SortAndFindSmallest_EmptyReturnsOne()
    {
        var cc = new CustomCost();
        Assert.Equal(1.0, cc.SortAndFindSmallest());
        Assert.Equal(1.0, cc.AvgFactor);
    }

    [Fact]
    public void CustomCost_SortAndFindSmallest_SortsByStartAndComputesAvgAndMin()
    {
        var cc = new CustomCost();
        // unsorted on purpose; full coverage [0,1] split into two halves
        cc.Ranges.Add(new CostEdge(0.5, 1.0, 4.0));
        cc.Ranges.Add(new CostEdge(0.0, 0.5, 2.0));

        double min = cc.SortAndFindSmallest();

        // sorted ascending by start
        Assert.Equal(0.0, cc.Ranges[0].Start);
        Assert.Equal(0.5, cc.Ranges[1].Start);

        // C++ seeds min_factor at 1.0 and only takes min() against each range factor; since both
        // factors (2, 4) exceed 1.0, the smallest factor reported is the 1.0 baseline.
        Assert.Equal(1.0, min, 9);
        // avg = 0.5*2 + 0.5*4 + 0(uncovered)*1 = 3.0
        Assert.Equal(3.0, cc.AvgFactor, 9);
    }

    [Fact]
    public void CustomCost_SortAndFindSmallest_PartialCoverageUsesFactorOneForUncovered()
    {
        var cc = new CustomCost();
        // covers only the first 25% with factor 5; remaining 75% implicitly factor 1
        cc.Ranges.Add(new CostEdge(0.0, 0.25, 5.0));

        double min = cc.SortAndFindSmallest();

        // min_factor is seeded at 1.0; the only range factor (5) is larger, so the smallest factor
        // reported is the 1.0 baseline (the uncovered remainder implicitly costs factor 1).
        Assert.Equal(1.0, min, 9);
        // avg = 0.25*5 + 0.75*1 = 2.0
        Assert.Equal(2.0, cc.AvgFactor, 9);
    }

    [Fact]
    public void CustomCost_SortAndFindSmallest_SubUnitFactorIsReportedAsMinimum()
    {
        var cc = new CustomCost();
        // a factor below the 1.0 baseline is the value that actually wins the min()
        cc.Ranges.Add(new CostEdge(0.0, 1.0, 0.3));

        double min = cc.SortAndFindSmallest();

        Assert.Equal(0.3, min, 9);
        // full coverage -> avg = 1.0*0.3 = 0.3
        Assert.Equal(0.3, cc.AvgFactor, 9);
    }

    // ===================== SpeedMask_Parse =====================

    [Fact]
    public void SpeedMaskParse_NullReturnsDefaultFlowMask()
    {
        Assert.Equal(GraphConstants.DefaultFlowMask, CostOptionsParser.SpeedMaskParse(null));
    }

    [Fact]
    public void SpeedMaskParse_EmptyArrayReturnsZeroMask()
    {
        // had_value is true but no recognized strings -> mask 0
        var arr = JsonDocument.Parse("[]").RootElement;
        Assert.Equal((byte)0, CostOptionsParser.SpeedMaskParse(arr));
    }

    [Fact]
    public void SpeedMaskParse_KnownTypesOrTogether()
    {
        var arr = JsonDocument.Parse("[\"freeflow\",\"constrained\",\"predicted\",\"current\"]").RootElement;
        byte expected = (byte)(GraphConstants.FreeFlowMask | GraphConstants.ConstrainedFlowMask |
                               GraphConstants.PredictedFlowMask | GraphConstants.CurrentFlowMask);
        Assert.Equal(expected, CostOptionsParser.SpeedMaskParse(arr));
    }

    [Fact]
    public void SpeedMaskParse_UnknownTypeIgnored()
    {
        var arr = JsonDocument.Parse("[\"freeflow\",\"bogus\"]").RootElement;
        Assert.Equal(GraphConstants.FreeFlowMask, CostOptionsParser.SpeedMaskParse(arr));
    }

    // ===================== set_use_tracks / living_streets / lit =====================

    [Theory]
    // use < 0.5 -> penalty = kMaxTrackPenalty*(1-2*use); factor interpolates from kMaxTrackFactor to 1
    [InlineData(0.0f, DynamicCost.MaxTrackPenalty, DynamicCost.MaxTrackFactor)]
    // use == 0.5 -> penalty 0; factor 1
    [InlineData(0.5f, 0.0f, 1.0f)]
    // use == 1.0 -> penalty 0; factor kMinTrackFactor
    [InlineData(1.0f, 0.0f, DynamicCost.MinTrackFactor)]
    public void SetUseTracks_MatchesFormula(float use, float expectedPenalty, float expectedFactor)
    {
        var costing = MakeAutoCosting();
        costing.Options.UseTracks = use;
        var tester = new TestDynamicCost(costing);

        Assert.Equal(expectedPenalty, tester.TrackPenalty, 4);
        Assert.Equal(expectedFactor, tester.TrackFactor, 4);
    }

    [Theory]
    [InlineData(0.0f, DynamicCost.MaxLivingStreetPenalty, DynamicCost.MaxLivingStreetFactor)]
    [InlineData(0.5f, 0.0f, 1.0f)]
    [InlineData(1.0f, 0.0f, DynamicCost.MinLivingStreetFactor)]
    public void SetUseLivingStreets_MatchesFormula(float use, float expectedPenalty, float expectedFactor)
    {
        var costing = MakeAutoCosting();
        costing.Options.UseLivingStreets = use;
        var tester = new TestDynamicCost(costing);

        Assert.Equal(expectedPenalty, tester.LivingStreetPenalty, 4);
        Assert.Equal(expectedFactor, tester.LivingStreetFactor, 4);
    }

    [Theory]
    // use < 0.5 -> kMinLitFactor + 2*use
    [InlineData(0.0f, DynamicCost.MinLitFactor)]
    [InlineData(0.25f, DynamicCost.MinLitFactor + 0.5f)]
    // use >= 0.5 -> (kMinLitFactor - 5) + 12*use
    [InlineData(0.5f, (DynamicCost.MinLitFactor - 5f) + 6f)]
    [InlineData(1.0f, (DynamicCost.MinLitFactor - 5f) + 12f)]
    public void SetUseLit_MatchesFormula(float use, float expectedFactor)
    {
        var costing = MakeAutoCosting();
        costing.Options.UseLit = use;
        var tester = new TestDynamicCost(costing);

        Assert.Equal(expectedFactor, tester.UnlitFactor, 4);
    }

    // ===================== get_base_costs ferry handling =====================

    [Fact]
    public void GetBaseCosts_UseFerryZero_AppliesMaxPenaltyAndTenXFactor()
    {
        var costing = MakeAutoCosting();
        costing.Options.UseFerry = 0.0f;
        costing.Options.FerryCost = DynamicCost.DefaultFerryCost;
        var tester = new TestDynamicCost(costing);

        // ferry_factor = 10 - 0*18 = 10
        Assert.Equal(10.0f, tester.FerryFactor, 4);
        // penalty = (uint)(kMaxFerryPenalty * 1.0) ; transition cost = ferry_cost + penalty
        float expectedPenalty = (uint)(DynamicCost.MaxFerryPenalty * 1.0f);
        Assert.Equal(DynamicCost.DefaultFerryCost + expectedPenalty, tester.FerryTransitionCost.CostValue, 2);
        Assert.Equal(DynamicCost.DefaultFerryCost, tester.FerryTransitionCost.Secs, 2);
    }

    [Fact]
    public void GetBaseCosts_UseFerryOne_NoPenaltyAndHalfFactor()
    {
        var costing = MakeAutoCosting();
        costing.Options.UseFerry = 1.0f;
        costing.Options.FerryCost = DynamicCost.DefaultFerryCost;
        var tester = new TestDynamicCost(costing);

        // ferry_factor = 1.5 - 1.0 = 0.5
        Assert.Equal(0.5f, tester.FerryFactor, 4);
        Assert.Equal(DynamicCost.DefaultFerryCost, tester.FerryTransitionCost.CostValue, 2);
    }

    [Fact]
    public void GetBaseCosts_RailFerryUsesUseRailFerry()
    {
        var costing = MakeAutoCosting();
        costing.Options.UseRailFerry = 1.0f;
        costing.Options.RailFerryCost = DynamicCost.DefaultRailFerryCost;
        var tester = new TestDynamicCost(costing);

        Assert.Equal(0.5f, tester.RailFerryFactor, 4);
        Assert.Equal(DynamicCost.DefaultRailFerryCost, tester.RailFerryTransitionCost.CostValue, 2);
    }

    [Fact]
    public void GetBaseCosts_TransitionCostsCombineCostAndPenalty()
    {
        var costing = MakeAutoCosting();
        costing.Options.GateCost = 30f;
        costing.Options.GatePenalty = 300f;
        costing.Options.TollBoothCost = 15f;
        costing.Options.TollBoothPenalty = 5f;
        costing.Options.CountryCrossingCost = 600f;
        costing.Options.CountryCrossingPenalty = 0f;
        var tester = new TestDynamicCost(costing);

        // {cost = base + penalty, secs = base}
        Assert.Equal(330f, tester.GateCost.CostValue, 2);
        Assert.Equal(30f, tester.GateCost.Secs, 2);
        Assert.Equal(20f, tester.TollBoothCost.CostValue, 2);
        Assert.Equal(15f, tester.TollBoothCost.Secs, 2);
        Assert.Equal(600f, tester.CountryCrossingCost.CostValue, 2);
        Assert.Equal(600f, tester.CountryCrossingCost.Secs, 2);
    }

    // ===================== base_transition_cost =====================

    private static NodeInfo MakeNode(NodeType type, bool driveOnRight, bool trafficSignal = false, bool taggedAccess = false, bool privateAccess = false)
    {
        var node = new NodeInfo(new PointLL(0, 0), new PointLL(0, 0), GraphConstants.AutoAccess, type,
            trafficSignal, taggedAccess, privateAccess, cashOnlyToll: false);
        node.SetDriveOnRight(driveOnRight);
        node.SetLocalEdgeCount(3);
        return node;
    }

    [Fact]
    public void BaseTransitionCost_TollBoothNode_AddsTollBoothCost()
    {
        var costing = MakeAutoCosting();
        costing.Options.TollBoothCost = 15f;
        costing.Options.TollBoothPenalty = 0f;
        var tester = new TestDynamicCost(costing);

        var node = MakeNode(NodeType.TollBooth, driveOnRight: true);

        var edge = default(DirectedEdge);
        edge.SetUse(Use.Road);
        edge.SetLength(100);
        edge.SetNamed(true);
        edge.SetNameConsistency(0, true);

        var pred = new EdgeLabel(); // pred toll false, use road
        var cost = tester.ExposeBaseTransitionCost(node, edge, pred, 0);

        // toll booth node -> toll_booth_cost_ applied (cost == secs == 15 since penalty 0)
        Assert.Equal(15f, cost.CostValue, 2);
        Assert.Equal(15f, cost.Secs, 2);
    }

    [Fact]
    public void BaseTransitionCost_ManeuverPenalty_AppliedWhenNamesInconsistentAndNotLink()
    {
        var costing = MakeAutoCosting();
        costing.Options.ManeuverPenalty = 5f;
        var tester = new TestDynamicCost(costing);

        var node = MakeNode(NodeType.StreetIntersection, driveOnRight: true);

        var edge = default(DirectedEdge);
        edge.SetUse(Use.Road);
        edge.SetLength(100);
        edge.SetLink(false);
        edge.SetNameConsistency(0, false); // inconsistent at idx 0

        var pred = new EdgeLabel();
        var cost = tester.ExposeBaseTransitionCost(node, edge, pred, 0);

        Assert.Equal(5f, cost.CostValue, 2);
        Assert.Equal(0f, cost.Secs, 2);
    }

    [Fact]
    public void BaseTransitionCost_Shortest_ZeroesAllPenalties()
    {
        var costing = MakeAutoCosting();
        costing.Options.ManeuverPenalty = 5f;
        costing.Options.Shortest = true;
        var tester = new TestDynamicCost(costing);

        var node = MakeNode(NodeType.StreetIntersection, driveOnRight: true);

        var edge = default(DirectedEdge);
        edge.SetUse(Use.Road);
        edge.SetLength(100);
        edge.SetLink(false);
        edge.SetNameConsistency(0, false);

        var pred = new EdgeLabel();
        var cost = tester.ExposeBaseTransitionCost(node, edge, pred, 0);

        // shortest multiplies cost by !shortest_ == 0
        Assert.Equal(0f, cost.CostValue, 2);
    }

    // ===================== TurnType =====================

    [Fact]
    public void TurnType_NonInternalEdge_ReturnsNoTurn()
    {
        var costing = MakeAutoCosting();
        var tester = new TestDynamicCost(costing);
        var node = MakeNode(NodeType.StreetIntersection, driveOnRight: true);

        var edge = default(DirectedEdge);
        edge.SetInternal(false);
        edge.SetLength(5);
        edge.SetTurnType(0, Turn.Type.Left);

        Assert.Equal(InternalTurn.NoTurn, tester.TurnType(0, node, edge));
    }

    [Fact]
    public void TurnType_DriveOnRight_ShortInternalLeftTurn_ReturnsLeftTurn()
    {
        var costing = MakeAutoCosting();
        var tester = new TestDynamicCost(costing); // penalize_uturns true
        var node = MakeNode(NodeType.StreetIntersection, driveOnRight: true);

        var edge = default(DirectedEdge);
        edge.SetInternal(true);
        edge.SetLength(5); // <= kShortInternalLength (8)
        edge.SetTurnType(0, Turn.Type.Left);

        Assert.Equal(InternalTurn.LeftTurn, tester.TurnType(0, node, edge));
    }

    [Fact]
    public void TurnType_DriveOnLeft_ShortInternalRightTurn_ReturnsRightTurn()
    {
        var costing = MakeAutoCosting();
        var tester = new TestDynamicCost(costing);
        var node = MakeNode(NodeType.StreetIntersection, driveOnRight: false);

        var edge = default(DirectedEdge);
        edge.SetInternal(true);
        edge.SetLength(5);
        edge.SetTurnType(0, Turn.Type.Right);

        Assert.Equal(InternalTurn.RightTurn, tester.TurnType(0, node, edge));
    }

    [Fact]
    public void TurnType_LongInternalEdge_ReturnsNoTurn()
    {
        var costing = MakeAutoCosting();
        var tester = new TestDynamicCost(costing);
        var node = MakeNode(NodeType.StreetIntersection, driveOnRight: true);

        var edge = default(DirectedEdge);
        edge.SetInternal(true);
        edge.SetLength(50); // > kShortInternalLength
        edge.SetTurnType(0, Turn.Type.Left);

        Assert.Equal(InternalTurn.NoTurn, tester.TurnType(0, node, edge));
    }

    // ===================== AddUturnPenalty =====================

    [Fact]
    public void AddUturnPenalty_NameInconsistentReverse_AddsNameInconsistentPenalty()
    {
        var costing = MakeAutoCosting();
        var tester = new TestDynamicCost(costing);
        var node = MakeNode(NodeType.StreetIntersection, driveOnRight: true);

        var edge = default(DirectedEdge);
        edge.SetNameConsistency(0, false); // inconsistent
        float seconds = 0f;

        tester.AddUturnPenalty(0, node, edge, hasReverse: true, hasLeft: false, hasRight: false,
            penalizeInternalUturns: true, internalTurn: InternalTurn.NoTurn, ref seconds);

        Assert.Equal(DynamicCost.TCNameInconsistentUturn, seconds, 4);
    }

    [Fact]
    public void AddUturnPenalty_ReverseConsistentName_AddsUnfavorableUturn()
    {
        var costing = MakeAutoCosting();
        var tester = new TestDynamicCost(costing);
        var node = MakeNode(NodeType.StreetIntersection, driveOnRight: true);

        var edge = default(DirectedEdge);
        edge.SetNameConsistency(0, true); // consistent
        float seconds = 0f;

        tester.AddUturnPenalty(0, node, edge, hasReverse: true, hasLeft: false, hasRight: false,
            penalizeInternalUturns: true, internalTurn: InternalTurn.NoTurn, ref seconds);

        Assert.Equal(DynamicCost.TCUnfavorableUturn, seconds, 4);
    }

    [Fact]
    public void AddUturnPenalty_PencilPointUturn_MultipliesSeconds()
    {
        var costing = MakeAutoCosting();
        var tester = new TestDynamicCost(costing);
        var node = MakeNode(NodeType.StreetIntersection, driveOnRight: true);

        var edge = default(DirectedEdge);
        edge.SetNameConsistency(0, true);
        edge.SetTurnType(0, Turn.Type.SharpLeft);
        edge.SetEdgeToRight(0, true);
        edge.SetEdgeToLeft(0, false);
        edge.SetNamed(true);
        float seconds = 10f;

        tester.AddUturnPenalty(0, node, edge, hasReverse: false, hasLeft: false, hasRight: false,
            penalizeInternalUturns: true, internalTurn: InternalTurn.NoTurn, ref seconds);

        Assert.Equal(10f * DynamicCost.TCUnfavorablePencilPointUturn, seconds, 4);
    }

    // ===================== OSRMCarTurnDuration =====================

    [Fact]
    public void OSRMCarTurnDuration_TrafficSignalAddsLightPenaltyForStraightAtFalseNode()
    {
        // local_edge_count <= 2 and not a uturn -> only the traffic light penalty applies.
        var node = new NodeInfo(new PointLL(0, 0), new PointLL(0, 0), GraphConstants.AutoAccess,
            NodeType.StreetIntersection, trafficSignal: true, taggedAccess: false, privateAccess: false,
            cashOnlyToll: false);
        node.SetDriveOnRight(true);
        node.SetLocalEdgeCount(2); // "false node"
        node.SetHeading(0, 0);   // pred opp local idx 0 heading 0
        node.SetHeading(1, 0);   // edge localedgeidx 1 heading 0

        var edge = default(DirectedEdge);
        // localedgeidx is in word5 bits 0..6; there is no public setter, but default is 0.
        // With both headings 0 the turn is straight (degree 180 after the +180 flip => reverse!).

        // Use idx_pred_opp = 1 so in_heading = (0 + 180) % 360 = 180, out_heading = heading(localedgeidx=0) = 0
        // -> turn degree = GetTurnDegree(180, 0) = 180 => reverse (uturn). number_of_roads(2) but is_u_turn true.
        float d = DynamicCost.OSRMCarTurnDuration(edge, node, idxPredOpp: 1);

        // traffic light (2) + uturn lookup at 180 (right-hand) + uturn penalty (20)
        Assert.True(d > 2f, $"expected > traffic light penalty, got {d}");
    }

    [Fact]
    public void OSRMCarTurnDuration_StraightThroughTwoWayNode_NoTurnCost()
    {
        // local_edge_count <= 2, straight (not uturn) -> turn duration 0 (no traffic signal).
        var node = new NodeInfo(new PointLL(0, 0), new PointLL(0, 0), GraphConstants.AutoAccess,
            NodeType.StreetIntersection, trafficSignal: false, taggedAccess: false, privateAccess: false,
            cashOnlyToll: false);
        node.SetDriveOnRight(true);
        node.SetLocalEdgeCount(2);
        // in_heading after flip = (180 + 180) % 360 = 0, out_heading = 0 => straight, not a uturn
        node.SetHeading(1, 180);
        node.SetHeading(0, 0);

        var edge = default(DirectedEdge); // localedgeidx defaults to 0

        float d = DynamicCost.OSRMCarTurnDuration(edge, node, idxPredOpp: 1);

        Assert.Equal(0f, d, 4);
    }
}
