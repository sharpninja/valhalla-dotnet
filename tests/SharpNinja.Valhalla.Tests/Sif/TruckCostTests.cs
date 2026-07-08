// xUnit port of the Valhalla sif TruckCost INLINE_TEST (sharpninja/valhalla fork, branch
// feature/unprotected-left-costing, based on valhalla @ 3.7.0).
// Source: src/sif/truckcost.cc  (the #ifdef INLINE_TEST block: TEST(TruckCost, testTruckCostParams))
//
// Faithfully reproduces the gtest's parameter-clamping coverage:
//   - the make_truckcost_from_json(property, testVal) + make_distributor_from_range + test::IsBetween
//     pattern, driven by a deterministic PRNG over [min - range, max + range] for 250 iterations
//   - every option the gtest exercises: maneuver_penalty, alley_penalty, destination_only_penalty,
//     gate_cost/gate_penalty, toll_booth_cost/penalty, country_crossing_cost/penalty, ferry_cost,
//     low_class_penalty, service_penalty, service_factor, axle_load
//   - the TruckMate CUSTOM unprotected_left_avoidance_meters parse test (TR-OSMNAV-COSTING-032 /
//     TEST-OSMNAV-044) that was added in the fork
//
// PORT-NOTE: the gtest reads the protected/file-local members through a TestTruckCost subclass; the
// C# TruckCost exposes the same members as public trailing-underscore fields (mirroring the gtest's
// `using TruckCost::member_;` declarations), so the subclass simply forwards the base members.
// make_truckcost_from_json runs the ported ParseTruckCostOptions (the equivalent of the gtest's
// ParseApi -> request.options().costings()) and constructs a TruckCost.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using SharpNinja.Valhalla.Baldr;
using SharpNinja.Valhalla.Sif;
using Xunit;

namespace SharpNinja.Valhalla.Tests.Sif;

public sealed class TruckCostTests
{
    // ---- TestTruckCost: faithful analogue of the gtest's TestTruckCost (exposes the members the
    // INLINE_TEST reads). The C++ test pulls in the protected base members via `using`; here the
    // TruckCost coster already exposes them as public fields, so we forward them. ----
    private sealed class TestTruckCost : TruckCost
    {
        public TestTruckCost(Costing costing) : base(costing)
        {
        }

        public float AlleyPenalty => AlleyPenalty_;
        public Cost CountryCrossingCost => CountryCrossingCost_;
        public float DestinationOnlyPenalty => DestinationOnlyPenalty_;
        public Cost FerryTransitionCost => FerryTransitionCost_;
        public Cost GateCost => GateCost_;
        public float ManeuverPenalty => ManeuverPenalty_;
        public float ServiceFactor => ServiceFactor_;
        public float ServicePenalty => ServicePenalty_;
        public Cost TollBoothCost => TollBoothCost_;
    }

    // ---- make_truckcost_from_json: build {"<property>": <val>} options, parse them, construct. ----
    private static TestTruckCost MakeTruckCostFromJson(string property, float testVal)
    {
        // R"({"costing": "truck", "costing_options":{"truck":{"<prop>":<val>}}})" -> here we hand the
        // inner truck options object straight to ParseTruckCostOptions (the same values the gtest's
        // ParseApi would have routed into request.options().costings()[truck]).
        string json = $"{{\"{property}\":{testVal.ToString("R", CultureInfo.InvariantCulture)}}}";
        JsonElement element = JsonDocument.Parse(json).RootElement;

        var costing = new Costing();
        TruckCostFactory.ParseTruckCostOptions(element, costing, new List<string>());
        return new TestTruckCost(costing);
    }

    // ---- make_distributor_from_range: uniform over [min - rangeLength, max + rangeLength]. ----
    // Faithful analogue: the C++ uses std::mt19937(seed=0) + uniform_real_distribution. We use a
    // seeded System.Random; the test only requires that values both inside and well outside the
    // valid range are exercised so the clamp-to-[min,max] invariant is verified each iteration.
    private const int TestIterations = 250;
    private const int Seed = 0;

    private static IEnumerable<float> SampleRange(float min, float max)
    {
        float rangeLength = max - min;
        double lo = (double)min - rangeLength;
        double hi = (double)max + rangeLength;
        var rng = new Random(Seed);
        for (int i = 0; i < TestIterations; ++i)
        {
            yield return (float)(lo + (rng.NextDouble() * (hi - lo)));
        }
    }

    // ---- test::IsBetween(min, max): inclusive bounds. ----
    private static void AssertBetween(float value, float min, float max)
    {
        Assert.True(value >= min && value <= max,
            $"expected {value} to be within [{min}, {max}]");
    }

    // ===================== TEST(TruckCost, testTruckCostParams) =====================

    [Fact]
    public void TestTruckCostParams()
    {
        BaseCostingOptionsConfig defaults = TruckCostConstants.BaseCostOptsConfig;

        // maneuver_penalty_
        foreach (float v in SampleRange(defaults.ManeuverPenalty.Min, defaults.ManeuverPenalty.Max))
        {
            TestTruckCost ctorTester = MakeTruckCostFromJson("maneuver_penalty", v);
            AssertBetween(ctorTester.ManeuverPenalty, defaults.ManeuverPenalty.Min, defaults.ManeuverPenalty.Max);
        }

        // TruckMate: unprotected_left_avoidance_meters_ (TR-OSMNAV-COSTING-032 / TEST-OSMNAV-044)
        foreach (float v in SampleRange(
            TruckCostConstants.UnprotectedLeftAvoidanceRange.Min,
            TruckCostConstants.UnprotectedLeftAvoidanceRange.Max))
        {
            TestTruckCost ctorTester = MakeTruckCostFromJson("unprotected_left_avoidance_meters", v);
            AssertBetween(ctorTester.UnprotectedLeftAvoidanceMeters_,
                TruckCostConstants.UnprotectedLeftAvoidanceRange.Min,
                TruckCostConstants.UnprotectedLeftAvoidanceRange.Max);
        }

        // alley_penalty_
        foreach (float v in SampleRange(defaults.AlleyPenalty.Min, defaults.AlleyPenalty.Max))
        {
            TestTruckCost ctorTester = MakeTruckCostFromJson("alley_penalty", v);
            AssertBetween(ctorTester.AlleyPenalty, defaults.AlleyPenalty.Min, defaults.AlleyPenalty.Max);
        }

        // destination_only_penalty_
        foreach (float v in SampleRange(defaults.DestOnlyPenalty.Min, defaults.DestOnlyPenalty.Max))
        {
            TestTruckCost ctorTester = MakeTruckCostFromJson("destination_only_penalty", v);
            AssertBetween(ctorTester.DestinationOnlyPenalty, defaults.DestOnlyPenalty.Min, defaults.DestOnlyPenalty.Max);
        }

        // gate_cost_ (Cost.secs)
        foreach (float v in SampleRange(defaults.GateCost.Min, defaults.GateCost.Max))
        {
            TestTruckCost ctorTester = MakeTruckCostFromJson("gate_cost", v);
            AssertBetween(ctorTester.GateCost.Secs, defaults.GateCost.Min, defaults.GateCost.Max);
        }

        // gate_penalty_ (Cost.cost)
        foreach (float v in SampleRange(defaults.GatePenalty.Min, defaults.GatePenalty.Max))
        {
            TestTruckCost ctorTester = MakeTruckCostFromJson("gate_penalty", v);
            AssertBetween(ctorTester.GateCost.CostValue, defaults.GatePenalty.Min, defaults.GatePenalty.Max);
        }

        // tollbooth_cost_ (Cost.secs)
        foreach (float v in SampleRange(defaults.TollBoothCost.Min, defaults.TollBoothCost.Max))
        {
            TestTruckCost ctorTester = MakeTruckCostFromJson("toll_booth_cost", v);
            AssertBetween(ctorTester.TollBoothCost.Secs, defaults.TollBoothCost.Min, defaults.TollBoothCost.Max);
        }

        // tollbooth_penalty_ (Cost.cost)
        foreach (float v in SampleRange(defaults.TollBoothPenalty.Min, defaults.TollBoothPenalty.Max))
        {
            TestTruckCost ctorTester = MakeTruckCostFromJson("toll_booth_penalty", v);
            AssertBetween(ctorTester.TollBoothCost.CostValue, defaults.TollBoothPenalty.Min,
                defaults.TollBoothPenalty.Max + defaults.TollBoothCost.Def);
        }

        // country_crossing_cost_ (Cost.secs)
        foreach (float v in SampleRange(defaults.CountryCrossingCost.Min, defaults.CountryCrossingCost.Max))
        {
            TestTruckCost ctorTester = MakeTruckCostFromJson("country_crossing_cost", v);
            AssertBetween(ctorTester.CountryCrossingCost.Secs, defaults.CountryCrossingCost.Min,
                defaults.CountryCrossingCost.Max);
        }

        // country_crossing_penalty_ (Cost.cost)
        foreach (float v in SampleRange(defaults.CountryCrossingPenalty.Min, defaults.CountryCrossingPenalty.Max))
        {
            TestTruckCost ctorTester = MakeTruckCostFromJson("country_crossing_penalty", v);
            AssertBetween(ctorTester.CountryCrossingCost.CostValue, defaults.CountryCrossingPenalty.Min,
                defaults.CountryCrossingPenalty.Max + defaults.CountryCrossingCost.Def);
        }

        // ferry_transition_cost_ (Cost.secs)
        foreach (float v in SampleRange(defaults.FerryCost.Min, defaults.FerryCost.Max))
        {
            TestTruckCost ctorTester = MakeTruckCostFromJson("ferry_cost", v);
            AssertBetween(ctorTester.FerryTransitionCost.Secs, defaults.FerryCost.Min, defaults.FerryCost.Max);
        }

        // low_class_penalty_
        foreach (float v in SampleRange(TruckCostConstants.LowClassPenaltyRange.Min, TruckCostConstants.LowClassPenaltyRange.Max))
        {
            TestTruckCost ctorTester = MakeTruckCostFromJson("low_class_penalty", v);
            AssertBetween(ctorTester.LowClassPenalty_, TruckCostConstants.LowClassPenaltyRange.Min,
                TruckCostConstants.LowClassPenaltyRange.Max);
        }

        // service_penalty_
        foreach (float v in SampleRange(defaults.ServicePenalty.Min, defaults.ServicePenalty.Max))
        {
            TestTruckCost ctorTester = MakeTruckCostFromJson("service_penalty", v);
            AssertBetween(ctorTester.ServicePenalty, defaults.ServicePenalty.Min, defaults.ServicePenalty.Max);
        }

        // service_factor_
        foreach (float v in SampleRange(defaults.ServiceFactor.Min, defaults.ServiceFactor.Max))
        {
            TestTruckCost ctorTester = MakeTruckCostFromJson("service_factor", v);
            AssertBetween(ctorTester.ServiceFactor, defaults.ServiceFactor.Min, defaults.ServiceFactor.Max);
        }

        // axle_load_
        foreach (float v in SampleRange(TruckCostConstants.TruckAxleLoadRange.Min, TruckCostConstants.TruckAxleLoadRange.Max))
        {
            TestTruckCost ctorTester = MakeTruckCostFromJson("axle_load", v);
            AssertBetween(ctorTester.AxleLoad_, TruckCostConstants.TruckAxleLoadRange.Min,
                TruckCostConstants.TruckAxleLoadRange.Max);
        }
    }

    // ===================== explicit coverage of the TruckMate custom parse =====================

    [Fact]
    public void UnprotectedLeftAvoidanceMeters_InRange_IsKept()
    {
        TestTruckCost ctorTester = MakeTruckCostFromJson("unprotected_left_avoidance_meters", 1609.34f);
        Assert.Equal(1609.34f, ctorTester.UnprotectedLeftAvoidanceMeters_, 2);
    }

    [Fact]
    public void UnprotectedLeftAvoidanceMeters_OutOfRange_SnapsToDefaultZero()
    {
        // above kUnprotectedLeftAvoidanceRange.max (1,000,000) -> snapped to default 0 (rule disabled)
        TestTruckCost ctorTester = MakeTruckCostFromJson("unprotected_left_avoidance_meters", 2_000_000f);
        Assert.Equal(0f, ctorTester.UnprotectedLeftAvoidanceMeters_);
    }

    [Fact]
    public void UnprotectedLeftAvoidanceMeters_AbsentDefaultsToZero()
    {
        var costing = new Costing();
        TruckCostFactory.ParseTruckCostOptions(JsonDocument.Parse("{}").RootElement, costing, new List<string>());
        var coster = new TestTruckCost(costing);
        Assert.Equal(0f, coster.UnprotectedLeftAvoidanceMeters_);
    }

    [Fact]
    public void EnableStaticFriction_DefaultsToTrue()
    {
        var costing = new Costing();
        TruckCostFactory.ParseTruckCostOptions(JsonDocument.Parse("{}").RootElement, costing, new List<string>());
        var coster = new TestTruckCost(costing);
        Assert.True(coster.EnableStaticFriction_);
    }

    [Fact]
    public void EnableStaticFriction_HonorsFalse()
    {
        var costing = new Costing();
        TruckCostFactory.ParseTruckCostOptions(
            JsonDocument.Parse("{\"enable_static_friction\":false}").RootElement, costing, new List<string>());
        var coster = new TestTruckCost(costing);
        Assert.False(coster.EnableStaticFriction_);
    }
}
