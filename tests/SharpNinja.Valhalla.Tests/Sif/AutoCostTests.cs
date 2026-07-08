// xUnit port of the Valhalla sif autocost INLINE_TEST (valhalla @ 3.7.0).
// Source: the `#ifdef INLINE_TEST` block at the bottom of src/sif/autocost.cc
//   (namespace { class TestAutoCost; make_autocost_from_json; make_distributor_from_range;
//    TEST(AutoCost, testAutoCostParams) }).
//
// This is a FAITHFUL port of testAutoCostParams: same properties, same ranges, same iteration count
// (250), same RNG seed (0), and the same flow_mask test-case table.
//
// PORT-NOTE: the gtest builds an AutoCost from a JSON request via ParseApi (the full worker pipeline)
// and exposes AutoCost's protected fields through a TestAutoCost subclass. There is no full ParseApi
// here, so MakeAutoCostFromJson reproduces the slice the test depends on:
//   1. ParseAutoCostOptions (the auto option parser) over the {"costing_options":{"auto":{...}}} JSON.
//   2. The worker.cc flow-mask rule: when the request is NOT time-dependent (no `date_time`), the
//      predicted + current flow masks are stripped from the costing's flow_mask
//      (src/worker.cc ~line 1062). The `date_time:{type:0}` cases ARE time-dependent (type 0 maps to
//      Options::current), so their flow masks survive.
// TestAutoCost then exposes the same fields the gtest's TestAutoCost exposes.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using SharpNinja.Valhalla.Baldr;
using SharpNinja.Valhalla.Sif;
using Xunit;

namespace SharpNinja.Valhalla.Tests.Sif;

public sealed class AutoCostTests
{
    private const uint TestIterations = 250;
    private const int Seed = 0;

    // ---- TestAutoCost: exposes the protected fields the gtest's TestAutoCost exposes ----
    // (using AutoCost::alley_penalty_; ... etc.)
    private sealed class TestAutoCost : AutoCost
    {
        public TestAutoCost(Costing costingOptions) : base(costingOptions)
        {
        }

        public float AlleyPenalty => AlleyPenalty_;
        public Cost CountryCrossingCost => CountryCrossingCost_;
        public float DestinationOnlyPenalty => DestinationOnlyPenalty_;
        public Cost FerryTransitionCost => FerryTransitionCost_;
        public new byte FlowMask => FlowMask_;
        public Cost GateCost => GateCost_;
        public float Height => Height_;
        public float Length => Length_;
        public float ManeuverPenalty => ManeuverPenalty_;
        public float ServiceFactor => ServiceFactor_;
        public float ServicePenalty => ServicePenalty_;
        public Cost TollBoothCost => TollBoothCost_;
        public float Weight => Weight_;
        public float Width => Width_;

        // AutoCost-local (already public on AutoCost, surfaced with the gtest's field name)
        public float AlleyFactor => AlleyFactor_;

        // TruckMate custom costing (FR-OSMNAV-022 / TR-OSMNAV-LEFTTURN-033).
        public float UnprotectedLeftAvoidanceMeters => UnprotectedLeftAvoidanceMeters_;
    }

    // ---- Faithful port of make_autocost_from_json<T> ----
    // ss << R"({"costing": "auto", "costing_options":{"auto":{")" << property << R"(":)" << testVal
    //    << "}}" << extra_json << "}";
    private static TestAutoCost MakeAutoCostFromJson<T>(string property, T testVal, string extraJson = "")
    {
        string testValJson = FormatJsonScalar(testVal);
        string requestJson =
            "{\"costing\": \"auto\", \"costing_options\":{\"auto\":{\"" + property + "\":" + testValJson +
            "}}" + extraJson + "}";

        using JsonDocument doc = JsonDocument.Parse(requestJson);
        JsonElement root = doc.RootElement;

        // ParseApi -> ParseAutoCostOptions over costing_options.auto
        JsonElement costingOptions = root.GetProperty("costing_options");
        var costing = new Costing();
        var warnings = new List<string>();
        AutoCostFactory.ParseAutoCostOptions(costingOptions, "auto", costing, warnings);

        // worker.cc: if not a time-dependent route, strip predicted + current flow masks.
        bool hasDateTime = root.TryGetProperty("date_time", out _);
        if (!hasDateTime)
        {
            costing.Options.FlowMask = (uint)((byte)costing.Options.FlowMask &
                ~(GraphConstants.PredictedFlowMask | GraphConstants.CurrentFlowMask));
        }

        return new TestAutoCost(costing);
    }

    private static string FormatJsonScalar<T>(T value)
    {
        // Mirrors the C++ stringstream insertion: numbers print as numbers; the string test values
        // (already-quoted JSON like "" / "bar" / ["freeflow"]) are emitted verbatim.
        return value switch
        {
            string s => s,
            float f => f.ToString("R", CultureInfo.InvariantCulture),
            double d => d.ToString("R", CultureInfo.InvariantCulture),
            uint u => u.ToString(CultureInfo.InvariantCulture),
            int i => i.ToString(CultureInfo.InvariantCulture),
            _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty,
        };
    }

    // ---- Faithful port of make_distributor_from_range ----
    // float rangeLength = range.max - range.min;
    // uniform_real_distribution<float>(range.min - rangeLength, range.max + rangeLength)
    private sealed class FloatDistributor
    {
        private readonly Random _rng;
        private readonly float _low;
        private readonly float _high;

        public FloatDistributor(Random rng, RangedDefault<float> range)
        {
            _rng = rng;
            float rangeLength = range.Max - range.Min;
            _low = range.Min - rangeLength;
            _high = range.Max + rangeLength;
        }

        public float Next() => _low + (float)_rng.NextDouble() * (_high - _low);
    }

    private static FloatDistributor MakeDistributorFromRange(Random rng, RangedDefault<float> range)
        => new FloatDistributor(rng, range);

    // test::IsBetween(min, max) -> closed interval check (matches the gtest matcher).
    private static void AssertIsBetween(float value, float min, float max)
        => Assert.True(value >= min && value <= max,
            $"Expected {value} to be within [{min}, {max}].");

    // ===================== testAutoCostParams =====================
    // Faithful port of TEST(AutoCost, testAutoCostParams). Each block draws testIterations values
    // from a distribution that straddles the option's [min, max] range, and asserts the resulting
    // (clamped) coster field is within that range.

    [Fact]
    public void TestAutoCostParams()
    {
        var generator = new Random(Seed);
        var defaults = AutoCostConstants.BaseCostOptsConfig;

        // maneuver_penalty_
        var dist = MakeDistributorFromRange(generator, defaults.ManeuverPenalty);
        for (uint i = 0; i < TestIterations; ++i)
        {
            TestAutoCost tester = MakeAutoCostFromJson("maneuver_penalty", dist.Next());
            AssertIsBetween(tester.ManeuverPenalty, defaults.ManeuverPenalty.Min, defaults.ManeuverPenalty.Max);
        }

        // alley_penalty_
        dist = MakeDistributorFromRange(generator, defaults.AlleyPenalty);
        for (uint i = 0; i < TestIterations; ++i)
        {
            TestAutoCost tester = MakeAutoCostFromJson("alley_penalty", dist.Next());
            AssertIsBetween(tester.AlleyPenalty, defaults.AlleyPenalty.Min, defaults.AlleyPenalty.Max);
        }

        // alley_factor_
        dist = MakeDistributorFromRange(generator, AutoCostConstants.AlleyFactorRange);
        for (uint i = 0; i < TestIterations; ++i)
        {
            TestAutoCost tester = MakeAutoCostFromJson("alley_factor", dist.Next());
            AssertIsBetween(tester.AlleyFactor, AutoCostConstants.AlleyFactorRange.Min, AutoCostConstants.AlleyFactorRange.Max);
        }

        // destination_only_penalty_
        dist = MakeDistributorFromRange(generator, defaults.DestOnlyPenalty);
        for (uint i = 0; i < TestIterations; ++i)
        {
            TestAutoCost tester = MakeAutoCostFromJson("destination_only_penalty", dist.Next());
            AssertIsBetween(tester.DestinationOnlyPenalty, defaults.DestOnlyPenalty.Min, defaults.DestOnlyPenalty.Max);
        }

        // gate_cost_ (Cost.secs)
        dist = MakeDistributorFromRange(generator, defaults.GateCost);
        for (uint i = 0; i < TestIterations; ++i)
        {
            TestAutoCost tester = MakeAutoCostFromJson("gate_cost", dist.Next());
            AssertIsBetween(tester.GateCost.Secs, defaults.GateCost.Min, defaults.GateCost.Max);
        }

        // gate_penalty_ (Cost.cost)
        dist = MakeDistributorFromRange(generator, defaults.GatePenalty);
        for (uint i = 0; i < TestIterations; ++i)
        {
            TestAutoCost tester = MakeAutoCostFromJson("gate_penalty", dist.Next());
            AssertIsBetween(tester.GateCost.CostValue, defaults.GatePenalty.Min, defaults.GatePenalty.Max);
        }

        // tollbooth_cost_ (Cost.secs)
        dist = MakeDistributorFromRange(generator, defaults.TollBoothCost);
        for (uint i = 0; i < TestIterations; ++i)
        {
            TestAutoCost tester = MakeAutoCostFromJson("toll_booth_cost", dist.Next());
            AssertIsBetween(tester.TollBoothCost.Secs, defaults.TollBoothCost.Min, defaults.TollBoothCost.Max);
        }

        // tollbooth_penalty_ (Cost.cost) -- upper bound is penalty.max + cost.def (matches the gtest).
        dist = MakeDistributorFromRange(generator, defaults.TollBoothPenalty);
        for (uint i = 0; i < TestIterations; ++i)
        {
            TestAutoCost tester = MakeAutoCostFromJson("toll_booth_penalty", dist.Next());
            AssertIsBetween(tester.TollBoothCost.CostValue, defaults.TollBoothPenalty.Min,
                defaults.TollBoothPenalty.Max + defaults.TollBoothCost.Def);
        }

        // country_crossing_cost_ (Cost.secs)
        dist = MakeDistributorFromRange(generator, defaults.CountryCrossingCost);
        for (uint i = 0; i < TestIterations; ++i)
        {
            TestAutoCost tester = MakeAutoCostFromJson("country_crossing_cost", dist.Next());
            AssertIsBetween(tester.CountryCrossingCost.Secs, defaults.CountryCrossingCost.Min,
                defaults.CountryCrossingCost.Max);
        }

        // country_crossing_penalty_ (Cost.cost) -- upper bound is penalty.max + cost.def.
        dist = MakeDistributorFromRange(generator, defaults.CountryCrossingPenalty);
        for (uint i = 0; i < TestIterations; ++i)
        {
            TestAutoCost tester = MakeAutoCostFromJson("country_crossing_penalty", dist.Next());
            AssertIsBetween(tester.CountryCrossingCost.CostValue, defaults.CountryCrossingPenalty.Min,
                defaults.CountryCrossingPenalty.Max + defaults.CountryCrossingCost.Def);
        }

        // ferry_cost_ (Cost.secs)
        dist = MakeDistributorFromRange(generator, defaults.FerryCost);
        for (uint i = 0; i < TestIterations; ++i)
        {
            TestAutoCost tester = MakeAutoCostFromJson("ferry_cost", dist.Next());
            AssertIsBetween(tester.FerryTransitionCost.Secs, defaults.FerryCost.Min, defaults.FerryCost.Max);
        }

        // (use_ferry is commented out in the gtest; preserved as a comment here for fidelity.)

        // service_penalty_
        dist = MakeDistributorFromRange(generator, defaults.ServicePenalty);
        for (uint i = 0; i < TestIterations; ++i)
        {
            TestAutoCost tester = MakeAutoCostFromJson("service_penalty", dist.Next());
            AssertIsBetween(tester.ServicePenalty, defaults.ServicePenalty.Min, defaults.ServicePenalty.Max);
        }

        // service_factor_
        dist = MakeDistributorFromRange(generator, defaults.ServiceFactor);
        for (uint i = 0; i < TestIterations; ++i)
        {
            TestAutoCost tester = MakeAutoCostFromJson("service_factor", dist.Next());
            AssertIsBetween(tester.ServiceFactor, defaults.ServiceFactor.Min, defaults.ServiceFactor.Max);
        }

        // height_
        dist = MakeDistributorFromRange(generator, defaults.Height);
        for (uint i = 0; i < TestIterations; ++i)
        {
            TestAutoCost tester = MakeAutoCostFromJson("height", dist.Next());
            AssertIsBetween(tester.Height, defaults.Height.Min, defaults.Height.Max);
        }

        // width_
        dist = MakeDistributorFromRange(generator, defaults.Width);
        for (uint i = 0; i < TestIterations; ++i)
        {
            TestAutoCost tester = MakeAutoCostFromJson("width", dist.Next());
            AssertIsBetween(tester.Width, defaults.Width.Min, defaults.Width.Max);
        }

        // length_
        dist = MakeDistributorFromRange(generator, defaults.Length);
        for (uint i = 0; i < TestIterations; ++i)
        {
            TestAutoCost tester = MakeAutoCostFromJson("length", dist.Next());
            AssertIsBetween(tester.Length, defaults.Length.Min, defaults.Length.Max);
        }

        // weight_
        dist = MakeDistributorFromRange(generator, defaults.Weight);
        for (uint i = 0; i < TestIterations; ++i)
        {
            TestAutoCost tester = MakeAutoCostFromJson("weight", dist.Next());
            AssertIsBetween(tester.Weight, defaults.Weight.Min, defaults.Weight.Max);
        }

        // flow_mask_
        var speedTypeTestCases = new List<(string Value, string ExtraJson, byte Expected)>
        {
            ("", ",\"date_time\":{\"type\":0}", GraphConstants.DefaultFlowMask),
            ("\"\"", ",\"date_time\":{\"type\":0}", GraphConstants.DefaultFlowMask),
            ("[]", ",\"date_time\":{\"type\":0}", 0),
            ("[\"foo\"]", ",\"date_time\":{\"type\":0}", 0),
            ("[\"freeflow\"]", ",\"date_time\":{\"type\":0}", GraphConstants.FreeFlowMask),
            ("[\"constrained\"]", ",\"date_time\":{\"type\":0}", GraphConstants.ConstrainedFlowMask),
            ("[\"predicted\"]", ",\"date_time\":{\"type\":0}", GraphConstants.PredictedFlowMask),
            ("[\"current\"]", ",\"date_time\":{\"type\":0}", GraphConstants.CurrentFlowMask),
            ("[\"freeflow\",\"current\",\"predicted\"]", ",\"date_time\":{\"type\":0}",
                (byte)(GraphConstants.FreeFlowMask | GraphConstants.CurrentFlowMask | GraphConstants.PredictedFlowMask)),
            ("[\"freeflow\",\"constrained\",\"predicted\",\"current\"]", ",\"date_time\":{\"type\":0}",
                GraphConstants.DefaultFlowMask),
            ("[\"constrained\",\"foo\",\"predicted\",\"freeflow\"]", ",\"date_time\":{\"type\":0}",
                (byte)(GraphConstants.FreeFlowMask | GraphConstants.ConstrainedFlowMask | GraphConstants.PredictedFlowMask)),

            ("", "", (byte)(GraphConstants.FreeFlowMask | GraphConstants.ConstrainedFlowMask)),
            ("\"\"", "", (byte)(GraphConstants.FreeFlowMask | GraphConstants.ConstrainedFlowMask)),
            ("[]", "", 0),
            ("[\"foo\"]", "", 0),
            ("[\"freeflow\"]", "", GraphConstants.FreeFlowMask),
            ("[\"constrained\"]", "", GraphConstants.ConstrainedFlowMask),
            ("[\"predicted\"]", "", 0),
            ("[\"current\"]", "", 0),
            ("[\"freeflow\",\"current\",\"predicted\"]", "", GraphConstants.FreeFlowMask),
            ("[\"constrained\",\"current\",\"predicted\"]", "", GraphConstants.ConstrainedFlowMask),
            ("[\"freeflow\",\"constrained\",\"predicted\",\"current\"]", "",
                (byte)(GraphConstants.FreeFlowMask | GraphConstants.ConstrainedFlowMask)),
            ("[\"constrained\",\"foo\",\"predicted\",\"freeflow\"]", "",
                (byte)(GraphConstants.FreeFlowMask | GraphConstants.ConstrainedFlowMask)),
        };

        foreach ((string value0, string extraJson, byte expected) in speedTypeTestCases)
        {
            string key = "speed_types";
            string value = value0;
            if (string.IsNullOrEmpty(value))
            {
                key = "foo";
                value = "\"bar\"";
            }

            TestAutoCost tester = MakeAutoCostFromJson(key, value, extraJson);
            Assert.Equal(expected, tester.FlowMask);
        }
    }

    // ===================== TruckMate custom: unprotected-left avoidance for auto/taxi =====================
    // The hard "avoid unprotected left turns" rule (FR-OSMNAV-022 / TR-OSMNAV-LEFTTURN-033) applies to
    // auto/taxi as well as truck. These mirror the TruckCost parse/ctor tests: the option is parsed by
    // the auto parser and read into the coster, and the derived TaxiCost inherits the behavior.

    [Fact]
    public void UnprotectedLeftAvoidanceMeters_InRange_IsKept()
    {
        TestAutoCost coster = MakeAutoCostFromJson("unprotected_left_avoidance_meters", 1609.34f);
        Assert.Equal(1609.34f, coster.UnprotectedLeftAvoidanceMeters, 2);
    }

    [Fact]
    public void UnprotectedLeftAvoidanceMeters_Absent_DefaultsToZero()
    {
        // A request that does not set the option leaves the rule disabled (0), matching truck costing.
        TestAutoCost coster = MakeAutoCostFromJson("alley_factor", 1.0f);
        Assert.Equal(0f, coster.UnprotectedLeftAvoidanceMeters);
    }

    [Fact]
    public void TaxiCost_HonorsUnprotectedLeftAvoidanceMeters()
    {
        var costing = new Costing();
        using JsonDocument doc = JsonDocument.Parse(
            "{\"costing\":\"taxi\",\"costing_options\":{\"taxi\":{\"unprotected_left_avoidance_meters\":1609.34}}}");
        AutoCostFactory.ParseTaxiCostOptions(
            doc.RootElement.GetProperty("costing_options"), "taxi", costing, new List<string>());

        var taxi = (AutoCost)AutoCostFactory.CreateTaxiCost(costing);
        Assert.Equal(1609.34f, taxi.UnprotectedLeftAvoidanceMeters_, 2);
    }
}
