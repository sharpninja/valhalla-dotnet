// Faithful C# port of the Valhalla sif costing-options foundation (valhalla @ 3.7.0).
// Sources:
//   - proto/descriptors/options.proto  (message Costing, Costing.Options, HierarchyLimits)
//   - valhalla/sif/dynamiccost.h        (ranged_default_t, BaseCostingOptionsConfig, ParseBaseCostOptions decl)
//   - src/sif/dynamiccost.cc            (BaseCostingOptionsConfig ctor, ParseBaseCostOptions, SpeedMask_Parse)
//   - valhalla/sif/hierarchylimits.h    (RelaxHierarchyLimits, kUnlimitedTransitions, kMaxDistance)
//
// In C++ the costing reads its options out of a protobuf message (Costing.Options). There is no
// wire-format requirement here: this is an in-process plain data class with the SAME getters the
// costing reads, including the two TruckMate custom fields:
//   - unprotected_left_avoidance_meters (float, field 97)
//   - enable_static_friction            (bool,  field 98)
//
// Defaults match the proto3 zero defaults exactly: numeric fields default to 0 and bool fields to
// false, EXCEPT the ones that ParseBaseCostOptions / get_base_costs assign a non-zero default to.
// To preserve the C++ behavior where a value is only treated as "user provided" when present, the
// data class exposes per-field "has_*" flags that the macro-equivalent parser consults, mirroring
// the protobuf `oneof has_*` cases.

using System;
using System.Collections.Generic;
using System.Text.Json;
using SharpNinja.Valhalla.Baldr;

namespace SharpNinja.Valhalla.Sif;

/// <summary>
/// Holds a range plus a default value for that range. Faithful port of the C++
/// <c>template &lt;class T&gt; struct ranged_default_t</c>. Snaps a value to the default when it is
/// outside <c>[min, max]</c> and reports whether clamping occurred.
/// </summary>
public readonly struct RangedDefault<T> where T : IComparable<T>
{
    public readonly T Min;
    public readonly T Def;
    public readonly T Max;

    public RangedDefault(T min, T def, T max)
    {
        Min = min;
        Def = def;
        Max = max;
    }

    /// <summary>Returns the value snapped to the default if outside of the range.</summary>
    public T Invoke(T value, out bool clamped)
    {
        if (value.CompareTo(Min) < 0 || value.CompareTo(Max) > 0)
        {
            clamped = true;
            return Def;
        }

        clamped = false;
        return value;
    }
}

/// <summary>
/// Hierarchy limits. Faithful port of the protobuf <c>message HierarchyLimits</c>. Controls
/// expansion and transitions between hierarchy levels.
/// </summary>
public sealed class HierarchyLimits
{
    public uint UpTransitionCount { get; set; }
    public uint MaxUpTransitions { get; set; }
    public float ExpandWithinDist { get; set; }

    public void SetUpTransitionCount(uint count) => UpTransitionCount = count;
    public void SetMaxUpTransitions(uint count) => MaxUpTransitions = count;
    public void SetExpandWithinDist(float dist) => ExpandWithinDist = dist;
}

/// <summary>
/// Sif hierarchy-limits free functions. Faithful port of <c>valhalla/sif/hierarchylimits.h</c>.
/// </summary>
public static class HierarchyLimitsFunctions
{
    /// <summary><c>std::numeric_limits&lt;uint32_t&gt;::max()</c>.</summary>
    public const uint UnlimitedTransitions = uint.MaxValue;

    /// <summary><c>std::numeric_limits&lt;float&gt;::max()</c>.</summary>
    public const float MaxDistance = float.MaxValue;

    /// <summary>Determine if expansion of a hierarchy level should stop (distance-aware).</summary>
    public static bool StopExpanding(HierarchyLimits hierarchyLimits, float dist)
        => hierarchyLimits.UpTransitionCount > hierarchyLimits.MaxUpTransitions
           && dist > hierarchyLimits.ExpandWithinDist;

    /// <summary>Determine if expansion of a hierarchy level should stop (bidirectional).</summary>
    public static bool StopExpanding(HierarchyLimits hierarchyLimits)
        => hierarchyLimits.UpTransitionCount > hierarchyLimits.MaxUpTransitions;

    /// <summary>Relax hierarchy limits to try to find a route when initial attempt fails.</summary>
    public static void RelaxHierarchyLimits(HierarchyLimits hierarchyLimits, float factor, float expansionWithinFactor)
    {
        if (hierarchyLimits.MaxUpTransitions != UnlimitedTransitions)
        {
            hierarchyLimits.SetMaxUpTransitions((uint)(hierarchyLimits.MaxUpTransitions * factor));
            hierarchyLimits.SetExpandWithinDist(hierarchyLimits.ExpandWithinDist * expansionWithinFactor);
        }
    }
}

/// <summary>
/// Edge to avoid (proto <c>message AvoidEdge</c>): edge graph id + percent along.
/// </summary>
public sealed class AvoidEdgeOption
{
    public ulong Id { get; set; }
    public float PercentAlong { get; set; }
}

/// <summary>
/// Linear-feature cost edge (proto <c>message CostFactorEdge</c>).
/// </summary>
public sealed class CostFactorEdge
{
    public ulong Id { get; set; }
    public double Factor { get; set; }
    public double Start { get; set; }
    public double End { get; set; }
}

/// <summary>
/// Plain C# data class mirroring the protobuf <c>Costing.Options</c> message. Exposes the SAME
/// getters the costing reads (see <c>DynamicCost.GetBaseCosts</c>), including the per-field
/// <c>Has*</c> flags that stand in for protobuf <c>oneof has_*</c> cases. Field numbers from the
/// proto are noted in comments. The two TruckMate custom fields are <see cref="UnprotectedLeftAvoidanceMeters"/>
/// (field 97) and <see cref="EnableStaticFriction"/> (field 98).
/// </summary>
public sealed class CostingOptions
{
    // ----- transition costs / penalties (used by get_base_costs) -----
    public float ManeuverPenalty { get; set; }                 // 1
    public bool HasManeuverPenalty { get; set; }
    public float DestinationOnlyPenalty { get; set; }          // 2
    public bool HasDestinationOnlyPenalty { get; set; }
    public float GateCost { get; set; }                        // 3
    public bool HasGateCost { get; set; }
    public float GatePenalty { get; set; }                     // 4
    public bool HasGatePenalty { get; set; }
    public float TollBoothCost { get; set; }                   // 5
    public bool HasTollBoothCost { get; set; }
    public float TollBoothPenalty { get; set; }                // 6
    public bool HasTollBoothPenalty { get; set; }
    public float AlleyPenalty { get; set; }                    // 7
    public bool HasAlleyPenalty { get; set; }
    public float CountryCrossingCost { get; set; }             // 8
    public bool HasCountryCrossingCost { get; set; }
    public float CountryCrossingPenalty { get; set; }          // 9
    public bool HasCountryCrossingPenalty { get; set; }
    public float FerryCost { get; set; }                       // 10
    public bool HasFerryCost { get; set; }
    public float UseFerry { get; set; }                        // 12
    public bool HasUseFerry { get; set; }
    public float TopSpeed { get; set; }                        // 30

    // ----- bike share (used by get_base_costs) -----
    public float BikeShareCost { get; set; }                   // 56
    public bool HasBikeShareCost { get; set; }
    public float BikeSharePenalty { get; set; }                // 57
    public bool HasBikeSharePenalty { get; set; }

    // ----- rail ferry (used by get_base_costs) -----
    public float RailFerryCost { get; set; }                   // 58
    public bool HasRailFerryCost { get; set; }
    public float UseRailFerry { get; set; }                    // 59
    public bool HasUseRailFerry { get; set; }

    // ----- traversability / ignore flags (read by DynamicCost ctor) -----
    public bool IgnoreRestrictions { get; set; }               // 60
    public bool IgnoreOneways { get; set; }                    // 61
    public bool IgnoreAccess { get; set; }                     // 62
    public bool IgnoreClosures { get; set; }                   // 63
    public bool HasIgnoreClosures { get; set; }
    public bool Shortest { get; set; }                         // 64

    // ----- service / track / living-street / lit -----
    public float ServicePenalty { get; set; }                  // 65
    public bool HasServicePenalty { get; set; }
    public float UseTracks { get; set; }                       // 66
    public bool HasUseTracks { get; set; }
    public float UseLivingStreets { get; set; }                // 68
    public bool HasUseLivingStreets { get; set; }
    public float ServiceFactor { get; set; }                   // 69
    public bool HasServiceFactor { get; set; }
    public float ClosureFactor { get; set; }                   // 70
    public bool HasClosureFactor { get; set; }
    public float PrivateAccessPenalty { get; set; }            // 71
    public bool HasPrivateAccessPenalty { get; set; }

    // ----- exclusions / HOT-HOV -----
    public bool ExcludeUnpaved { get; set; }                   // 72
    public bool IncludeHot { get; set; }                       // 73
    public bool IncludeHov2 { get; set; }                      // 74
    public bool IncludeHov3 { get; set; }                      // 75
    public bool ExcludeCashOnlyTolls { get; set; }             // 76
    public uint RestrictionProbability { get; set; }           // 77

    public List<AvoidEdgeOption> ExcludeEdges { get; } = new();    // 78

    public uint FixedSpeed { get; set; }                       // 80
    public float UseLit { get; set; }                          // 82
    public bool DisableHierarchyPruning { get; set; }          // 83
    public bool IgnoreNonVehicularRestrictions { get; set; }   // 84

    public bool ExcludeBridges { get; set; }                   // 87
    public bool ExcludeTunnels { get; set; }                   // 88
    public bool ExcludeTolls { get; set; }                     // 89
    public bool ExcludeHighways { get; set; }                  // 90
    public bool ExcludeFerries { get; set; }                   // 91

    public Dictionary<uint, HierarchyLimits> HierarchyLimits { get; } = new(); // 92
    public bool IgnoreConstruction { get; set; }               // 93
    public List<CostFactorEdge> CostFactorEdges { get; } = new();  // 94
    public float SpeedPenaltyFactor { get; set; }              // 95
    public bool HasSpeedPenaltyFactor { get; set; }

    // ----- flow mask (proto field 55) -----
    public uint FlowMask { get; set; }                         // 55
    public bool HasFlowMask { get; set; }

    // ----- dimensions -----
    public float Height { get; set; }                          // 38
    public float Width { get; set; }                           // 39
    public float Length { get; set; }                          // 40
    public float Weight { get; set; }                          // 36

    // ----- truck-specific options (read by TruckCost) -----
    public float LowClassPenalty { get; set; }                 // 31
    public bool HasLowClassPenalty { get; set; }
    public bool Hazmat { get; set; }                           // 32
    public float AxleLoad { get; set; }                        // 33
    public bool HasAxleLoad { get; set; }
    public float UseTolls { get; set; }                        // 35
    public bool HasUseTolls { get; set; }
    public float UseHighways { get; set; }                     // 37
    public bool HasUseHighways { get; set; }

    // ----- auto-specific options (autocost.cc) -----
    public float AlleyFactor { get; set; }                     // 24
    public bool HasAlleyFactor { get; set; }
    public float UseDistance { get; set; }                     // 67
    public bool HasUseDistance { get; set; }

    public uint AxleCount { get; set; }                        // 53
    public bool HasTopSpeed { get; set; }
    public float HgvNoAccessPenalty { get; set; }              // 85
    public bool HasHgvNoAccessPenalty { get; set; }
    public float UseTruckRoute { get; set; }                   // 86

    // ===== TruckMate custom fields =====

    /// <summary>
    /// Custom field 97: meters of look-ahead distance over which to avoid unprotected left turns.
    /// </summary>
    public float UnprotectedLeftAvoidanceMeters { get; set; }  // 97

    /// <summary>Whether <see cref="UnprotectedLeftAvoidanceMeters"/> was user provided (proto <c>has_*</c> case).</summary>
    public bool HasUnprotectedLeftAvoidanceMeters { get; set; }

    /// <summary>
    /// Custom field 98: enables the deterministic static-friction route ranking model.
    /// </summary>
    public bool EnableStaticFriction { get; set; }             // 98

    public int HierarchyLimitsSize => HierarchyLimits.Count;
}

/// <summary>
/// Plain C# data class mirroring the protobuf <c>message Costing</c>: a costing type, a name, the
/// internal-only <c>filter_closures</c> flag, and the nested <see cref="CostingOptions"/>.
/// </summary>
public sealed class Costing
{
    /// <summary>Costing types. Faithful port of proto <c>enum Costing.Type</c>.</summary>
    public enum Type
    {
        None = 0,    // proto: none_
        Bicycle = 1,
        Bus = 2,
        MotorScooter = 3,
        Multimodal = 4,
        Pedestrian = 5,
        Transit = 6,
        Truck = 7,
        Motorcycle = 8,
        Taxi = 9,
        Auto = 10,   // proto: auto_
        Bikeshare = 11,
        AutoPedestrian = 12,
    }

    public Type CostingType { get; set; } = Type.None;
    public string Name { get; set; } = string.Empty;
    public bool FilterClosures { get; set; } = true;
    public CostingOptions Options { get; set; } = new();

    public void SetType(Type type) => CostingType = type;
    public void SetName(string name) => Name = name;
}

/// <summary>
/// Structure that stores default values for costing options that are common for most costing
/// models. Faithful port of <c>struct BaseCostingOptionsConfig</c> with the same default values
/// assigned by its C++ constructor.
/// </summary>
public sealed class BaseCostingOptionsConfig
{
    public RangedDefault<float> DestOnlyPenalty;
    public RangedDefault<float> ManeuverPenalty;
    public RangedDefault<float> AlleyPenalty;
    public RangedDefault<float> GateCost;
    public RangedDefault<float> GatePenalty;
    public RangedDefault<float> PrivateAccessPenalty;
    public RangedDefault<float> CountryCrossingCost;
    public RangedDefault<float> CountryCrossingPenalty;

    public bool DisableTollBooth;
    public RangedDefault<float> TollBoothCost;
    public RangedDefault<float> TollBoothPenalty;

    public bool DisableFerry;
    public RangedDefault<float> FerryCost;
    public RangedDefault<float> UseFerry;

    public bool DisableRailFerry;
    public RangedDefault<float> RailFerryCost;
    public RangedDefault<float> UseRailFerry;

    public RangedDefault<float> ServicePenalty;
    public RangedDefault<float> ServiceFactor;

    public RangedDefault<float> UseTracks;
    public RangedDefault<float> UseLivingStreets;
    public RangedDefault<float> UseLit;

    public RangedDefault<float> ClosureFactor;
    public RangedDefault<float> SpeedPenaltyFactor;

    public bool ExcludeUnpaved;
    public bool ExcludeBridges;
    public bool ExcludeTunnels;
    public bool ExcludeTolls;
    public bool ExcludeHighways;
    public bool ExcludeFerries;
    public bool HasExcludes;

    public bool ExcludeCashOnlyTolls;

    public bool IncludeHot;
    public bool IncludeHov2;
    public bool IncludeHov3;

    public RangedDefault<float> Height;
    public RangedDefault<float> Width;
    public RangedDefault<float> Length;
    public RangedDefault<float> Weight;

    /// <summary>
    /// Assign default values for costing options. Faithful port of the C++ constructor
    /// initializer list, including the kDefault* constants defined in dynamiccost.cc.
    /// </summary>
    public BaseCostingOptionsConfig()
    {
        DestOnlyPenalty = new RangedDefault<float>(0f, DynamicCost.DefaultDestinationOnlyPenalty, DynamicCost.MaxPenalty);
        ManeuverPenalty = new RangedDefault<float>(0f, DynamicCost.DefaultManeuverPenalty, DynamicCost.MaxPenalty);
        AlleyPenalty = new RangedDefault<float>(0f, DynamicCost.DefaultAlleyPenalty, DynamicCost.MaxPenalty);
        GateCost = new RangedDefault<float>(0f, DynamicCost.DefaultGateCost, DynamicCost.MaxPenalty);
        GatePenalty = new RangedDefault<float>(0f, DynamicCost.DefaultGatePenalty, DynamicCost.MaxPenalty);
        PrivateAccessPenalty = new RangedDefault<float>(0f, DynamicCost.DefaultPrivateAccessPenalty, DynamicCost.MaxPenalty);
        CountryCrossingCost = new RangedDefault<float>(0f, DynamicCost.DefaultCountryCrossingCost, DynamicCost.MaxPenalty);
        CountryCrossingPenalty = new RangedDefault<float>(0f, DynamicCost.DefaultCountryCrossingPenalty, DynamicCost.MaxPenalty);
        TollBoothCost = new RangedDefault<float>(0f, DynamicCost.DefaultTollBoothCost, DynamicCost.MaxPenalty);
        TollBoothPenalty = new RangedDefault<float>(0f, DynamicCost.DefaultTollBoothPenalty, DynamicCost.MaxPenalty);
        FerryCost = new RangedDefault<float>(0f, DynamicCost.DefaultFerryCost, DynamicCost.MaxPenalty);
        UseFerry = new RangedDefault<float>(0f, DynamicCost.DefaultUseFerry, 1f);
        RailFerryCost = new RangedDefault<float>(0f, DynamicCost.DefaultRailFerryCost, DynamicCost.MaxPenalty);
        UseRailFerry = new RangedDefault<float>(0f, DynamicCost.DefaultUseRailFerry, 1f);
        ServicePenalty = new RangedDefault<float>(0f, DynamicCost.DefaultServicePenalty, DynamicCost.MaxPenalty);
        ServiceFactor = new RangedDefault<float>(DynamicCost.MinFactor, DynamicCost.DefaultServiceFactor, DynamicCost.MaxFactor);
        UseTracks = new RangedDefault<float>(0f, DynamicCost.DefaultUseTracks, 1f);
        UseLivingStreets = new RangedDefault<float>(0f, DynamicCost.DefaultUseLivingStreets, 1f);
        UseLit = new RangedDefault<float>(0f, DynamicCost.DefaultUseLit, 1f);
        ClosureFactor = new RangedDefault<float>(1.0f, DynamicCost.DefaultClosureFactor, 10.0f);
        SpeedPenaltyFactor = new RangedDefault<float>(0.0f, DynamicCost.DefaultSpeedPenaltyFactor, 1.0f);
        ExcludeUnpaved = false;
        ExcludeBridges = false;
        ExcludeTunnels = false;
        ExcludeTolls = false;
        ExcludeHighways = false;
        ExcludeFerries = false;
        HasExcludes = false;
        ExcludeCashOnlyTolls = false;
        IncludeHot = false;
        IncludeHov2 = false;
        IncludeHov3 = false;
        Height = new RangedDefault<float>(0f, DynamicCost.DefaultHeight, 10.0f);
        Width = new RangedDefault<float>(0f, DynamicCost.DefaultWidth, 10.0f);
        Length = new RangedDefault<float>(0f, DynamicCost.DefaultLength, 50.0f);
        Weight = new RangedDefault<float>(0f, DynamicCost.DefaultWeight, 100.0f);
    }
}

/// <summary>
/// Parser for base cost options common to most costing models. Faithful port of
/// <c>SpeedMask_Parse</c> and <c>ParseBaseCostOptions</c> from <c>src/sif/dynamiccost.cc</c>.
/// </summary>
/// <remarks>
/// PORT-NOTE: The C++ implementation reads from a rapidjson DOM via JSON pointer paths (e.g.
/// "/maneuver_penalty"). This port reads from a <see cref="JsonElement"/> object with the same
/// top-level keys (no leading slash). The protobuf macros JSON_PBF_RANGED_DEFAULT /
/// JSON_PBF_DEFAULT semantics are reproduced: a value already present on the options object is
/// used as the fallback default, otherwise the config default is used; ranged values are clamped
/// through <see cref="RangedDefault{T}.Invoke"/>.
/// </remarks>
public static class CostOptionsParser
{
    /// <summary>Faithful port of the file-local <c>SpeedMask_Parse</c>.</summary>
    public static byte SpeedMaskParse(JsonElement? speedTypes)
    {
        // static const std::unordered_map<std::string, uint8_t> types
        if (speedTypes is not { } st)
            return GraphConstants.DefaultFlowMask;

        bool hadValue = false;
        byte mask = 0;
        if (st.ValueKind == JsonValueKind.Array)
        {
            hadValue = true;
            foreach (var speedType in st.EnumerateArray())
            {
                if (speedType.ValueKind == JsonValueKind.String)
                {
                    switch (speedType.GetString())
                    {
                        case "freeflow": mask |= GraphConstants.FreeFlowMask; break;
                        case "constrained": mask |= GraphConstants.ConstrainedFlowMask; break;
                        case "predicted": mask |= GraphConstants.PredictedFlowMask; break;
                        case "current": mask |= GraphConstants.CurrentFlowMask; break;
                    }
                }
            }
        }

        return hadValue ? mask : GraphConstants.DefaultFlowMask;
    }

    /// <summary>
    /// Parses base cost options that are common for most costing models. Faithful port of
    /// <c>ParseBaseCostOptions</c>.
    /// </summary>
    /// <param name="json">JSON object holding user provided costing options (top-level keys, no leading slash).</param>
    /// <param name="c">Mutable <see cref="Costing"/> where parsed values are stored.</param>
    /// <param name="cfg">Default values with enable/disable parsing indicators.</param>
    /// <param name="warnings">List of warning descriptions; a warning is appended when a value is clamped.</param>
    public static void ParseBaseCostOptions(
        JsonElement json,
        Costing c,
        BaseCostingOptionsConfig cfg,
        List<string> warnings)
    {
        CostingOptions co = c.Options;

        // ignore bogus input
        if (co.HasFlowMask && co.FlowMask > GraphConstants.DefaultFlowMask)
        {
            co.HasFlowMask = false;
            co.FlowMask = 0;
        }

        // defer to json or defaults if no pbf is present
        JsonElement? speedTypes = GetChild(json, "speed_types");
        if (speedTypes is not null || !co.HasFlowMask)
        {
            co.FlowMask = SpeedMaskParse(speedTypes);
            co.HasFlowMask = true;
        }

        // named costing
        if (TryGetString(json, "name", out string name))
            c.SetName(name);

        // various traversability flags (V2 - no oneof)
        co.IgnoreRestrictions = GetBool(json, "ignore_restrictions", co.IgnoreRestrictions || false);
        co.IgnoreOneways = GetBool(json, "ignore_oneways", co.IgnoreOneways || false);
        co.IgnoreAccess = GetBool(json, "ignore_access", co.IgnoreAccess || false);
        co.IgnoreClosures = GetBool(json, "ignore_closures", co.IgnoreClosures || false);
        co.HasIgnoreClosures = true;
        co.IgnoreConstruction = GetBool(json, "ignore_construction", co.IgnoreConstruction || false);
        co.IgnoreNonVehicularRestrictions =
            GetBool(json, "ignore_non_vehicular_restrictions", co.IgnoreNonVehicularRestrictions || false);

        // shortest
        co.Shortest = GetBool(json, "shortest", co.Shortest || false);

        // disable hierarchy pruning
        co.DisableHierarchyPruning = GetBool(json, "disable_hierarchy_pruning", co.DisableHierarchyPruning);

        // destination only penalty
        RangedDefault(co, cfg.DestOnlyPenalty, json, "destination_only_penalty",
            co.HasDestinationOnlyPenalty, co.DestinationOnlyPenalty,
            (v, h) => { co.DestinationOnlyPenalty = v; co.HasDestinationOnlyPenalty = h; }, warnings);

        // maneuver_penalty
        RangedDefault(co, cfg.ManeuverPenalty, json, "maneuver_penalty",
            co.HasManeuverPenalty, co.ManeuverPenalty,
            (v, h) => { co.ManeuverPenalty = v; co.HasManeuverPenalty = h; }, warnings);

        // alley_penalty
        RangedDefault(co, cfg.AlleyPenalty, json, "alley_penalty",
            co.HasAlleyPenalty, co.AlleyPenalty,
            (v, h) => { co.AlleyPenalty = v; co.HasAlleyPenalty = h; }, warnings);

        // gate_cost
        RangedDefault(co, cfg.GateCost, json, "gate_cost",
            co.HasGateCost, co.GateCost,
            (v, h) => { co.GateCost = v; co.HasGateCost = h; }, warnings);

        // gate_penalty
        RangedDefault(co, cfg.GatePenalty, json, "gate_penalty",
            co.HasGatePenalty, co.GatePenalty,
            (v, h) => { co.GatePenalty = v; co.HasGatePenalty = h; }, warnings);

        // private_access_penalty
        RangedDefault(co, cfg.PrivateAccessPenalty, json, "private_access_penalty",
            co.HasPrivateAccessPenalty, co.PrivateAccessPenalty,
            (v, h) => { co.PrivateAccessPenalty = v; co.HasPrivateAccessPenalty = h; }, warnings);

        // country_crossing_cost
        RangedDefault(co, cfg.CountryCrossingCost, json, "country_crossing_cost",
            co.HasCountryCrossingCost, co.CountryCrossingCost,
            (v, h) => { co.CountryCrossingCost = v; co.HasCountryCrossingCost = h; }, warnings);

        // country_crossing_penalty
        RangedDefault(co, cfg.CountryCrossingPenalty, json, "country_crossing_penalty",
            co.HasCountryCrossingPenalty, co.CountryCrossingPenalty,
            (v, h) => { co.CountryCrossingPenalty = v; co.HasCountryCrossingPenalty = h; }, warnings);

        if (!cfg.DisableTollBooth)
        {
            RangedDefault(co, cfg.TollBoothCost, json, "toll_booth_cost",
                co.HasTollBoothCost, co.TollBoothCost,
                (v, h) => { co.TollBoothCost = v; co.HasTollBoothCost = h; }, warnings);

            RangedDefault(co, cfg.TollBoothPenalty, json, "toll_booth_penalty",
                co.HasTollBoothPenalty, co.TollBoothPenalty,
                (v, h) => { co.TollBoothPenalty = v; co.HasTollBoothPenalty = h; }, warnings);
        }

        if (!cfg.DisableFerry)
        {
            RangedDefault(co, cfg.FerryCost, json, "ferry_cost",
                co.HasFerryCost, co.FerryCost,
                (v, h) => { co.FerryCost = v; co.HasFerryCost = h; }, warnings);

            RangedDefault(co, cfg.UseFerry, json, "use_ferry",
                co.HasUseFerry, co.UseFerry,
                (v, h) => { co.UseFerry = v; co.HasUseFerry = h; }, warnings);
        }

        if (!cfg.DisableRailFerry)
        {
            RangedDefault(co, cfg.RailFerryCost, json, "rail_ferry_cost",
                co.HasRailFerryCost, co.RailFerryCost,
                (v, h) => { co.RailFerryCost = v; co.HasRailFerryCost = h; }, warnings);

            RangedDefault(co, cfg.UseRailFerry, json, "use_rail_ferry",
                co.HasUseRailFerry, co.UseRailFerry,
                (v, h) => { co.UseRailFerry = v; co.HasUseRailFerry = h; }, warnings);
        }

        co.ExcludeUnpaved = GetBool(json, "exclude_unpaved", cfg.ExcludeUnpaved);
        co.ExcludeBridges = GetBool(json, "exclude_bridges", cfg.ExcludeBridges);
        co.ExcludeTunnels = GetBool(json, "exclude_tunnels", cfg.ExcludeTunnels);
        co.ExcludeTolls = GetBool(json, "exclude_tolls", cfg.ExcludeTolls);
        co.ExcludeHighways = GetBool(json, "exclude_highways", cfg.ExcludeHighways);
        co.ExcludeFerries = GetBool(json, "exclude_ferries", cfg.ExcludeFerries);
        co.ExcludeCashOnlyTolls = GetBool(json, "exclude_cash_only_tolls", cfg.ExcludeCashOnlyTolls);

        // service_penalty
        RangedDefault(co, cfg.ServicePenalty, json, "service_penalty",
            co.HasServicePenalty, co.ServicePenalty,
            (v, h) => { co.ServicePenalty = v; co.HasServicePenalty = h; }, warnings);

        // service_factor
        RangedDefault(co, cfg.ServiceFactor, json, "service_factor",
            co.HasServiceFactor, co.ServiceFactor,
            (v, h) => { co.ServiceFactor = v; co.HasServiceFactor = h; }, warnings);

        // use_tracks
        RangedDefault(co, cfg.UseTracks, json, "use_tracks",
            co.HasUseTracks, co.UseTracks,
            (v, h) => { co.UseTracks = v; co.HasUseTracks = h; }, warnings);

        // use_living_streets
        RangedDefault(co, cfg.UseLivingStreets, json, "use_living_streets",
            co.HasUseLivingStreets, co.UseLivingStreets,
            (v, h) => { co.UseLivingStreets = v; co.HasUseLivingStreets = h; }, warnings);

        // use_lit (V2 - no oneof; fallback to the bare value, not has_case)
        RangedDefaultV2(co, cfg.UseLit, json, "use_lit", co.UseLit,
            v => co.UseLit = v, warnings);

        // closure_factor
        RangedDefault(co, cfg.ClosureFactor, json, "closure_factor",
            co.HasClosureFactor, co.ClosureFactor,
            (v, h) => { co.ClosureFactor = v; co.HasClosureFactor = h; }, warnings);

        // speed_penalty_factor
        RangedDefault(co, cfg.SpeedPenaltyFactor, json, "speed_penalty_factor",
            co.HasSpeedPenaltyFactor, co.SpeedPenaltyFactor,
            (v, h) => { co.SpeedPenaltyFactor = v; co.HasSpeedPenaltyFactor = h; }, warnings);

        // HOT/HOV (V2)
        co.IncludeHot = GetBool(json, "include_hot", cfg.IncludeHot);
        co.IncludeHov2 = GetBool(json, "include_hov2", cfg.IncludeHov2);
        co.IncludeHov3 = GetBool(json, "include_hov3", cfg.IncludeHov3);

        // fixed_speed (V2, uint range)
        RangedDefaultUintV2(co, DynamicCost.FixedSpeedRange, json, "fixed_speed", co.FixedSpeed,
            v => co.FixedSpeed = v, warnings);

        // Dimensions
        RangedDefault(co, cfg.Height, json, "height", false, co.Height,
            (v, h) => co.Height = v, warnings);
        RangedDefault(co, cfg.Width, json, "width", false, co.Width,
            (v, h) => co.Width = v, warnings);
        RangedDefault(co, cfg.Length, json, "length", false, co.Length,
            (v, h) => co.Length = v, warnings);
        RangedDefault(co, cfg.Weight, json, "weight", false, co.Weight,
            (v, h) => co.Weight = v, warnings);
    }

    // ---- helpers reproducing the JSON_PBF_* macros ----

    public static void RangedDefault(
        CostingOptions co,
        RangedDefault<float> range,
        JsonElement json,
        string key,
        bool hasCase,
        float currentValue,
        Action<float, bool> setter,
        List<string> warnings)
    {
        float fallback = hasCase ? currentValue : range.Def;
        float requested = GetFloat(json, key, fallback);
        float clampedValue = range.Invoke(requested, out bool clamped);
        setter(clampedValue, true);
        if (clamped)
            warnings.Add($"'{key}' has been clamped to {range.Def}");
    }

    public static void RangedDefaultV2(
        CostingOptions co,
        RangedDefault<float> range,
        JsonElement json,
        string key,
        float currentValue,
        Action<float> setter,
        List<string> warnings)
    {
        // V2 macro: fallback uses the truthy current value (option_name() ? option_name() : def)
        float fallback = currentValue != 0f ? currentValue : range.Def;
        float requested = GetFloat(json, key, fallback);
        float clampedValue = range.Invoke(requested, out bool clamped);
        setter(clampedValue);
        if (clamped)
            warnings.Add($"'{key}' has been clamped to {range.Def}");
    }

    public static void RangedDefaultUintV2(
        CostingOptions co,
        RangedDefault<uint> range,
        JsonElement json,
        string key,
        uint currentValue,
        Action<uint> setter,
        List<string> warnings)
    {
        uint fallback = currentValue != 0u ? currentValue : range.Def;
        uint requested = GetUInt(json, key, fallback);
        uint clampedValue = range.Invoke(requested, out bool clamped);
        setter(clampedValue);
        if (clamped)
            warnings.Add($"'{key}' has been clamped to {range.Def}");
    }

    private static JsonElement? GetChild(JsonElement json, string key)
    {
        if (json.ValueKind == JsonValueKind.Object && json.TryGetProperty(key, out var child))
            return child;
        return null;
    }

    private static bool TryGetString(JsonElement json, string key, out string value)
    {
        if (GetChild(json, key) is { ValueKind: JsonValueKind.String } e)
        {
            value = e.GetString() ?? string.Empty;
            return true;
        }

        value = string.Empty;
        return false;
    }

    public static bool GetBool(JsonElement json, string key, bool def)
    {
        if (GetChild(json, key) is { } e)
        {
            if (e.ValueKind == JsonValueKind.True) return true;
            if (e.ValueKind == JsonValueKind.False) return false;
        }

        return def;
    }

    private static float GetFloat(JsonElement json, string key, float def)
        => GetChild(json, key) is { ValueKind: JsonValueKind.Number } e ? (float)e.GetDouble() : def;

    private static uint GetUInt(JsonElement json, string key, uint def)
        => GetChild(json, key) is { ValueKind: JsonValueKind.Number } e ? e.GetUInt32() : def;
}
