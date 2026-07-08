// Faithful C# port of Valhalla sif costfactory.h (valhalla @ 3.7.0).
// Source: F:/github/valhalla/valhalla/sif/costfactory.h
//   plus kCostingTypeMapping / cost_ptr_t / mode_costing_t from valhalla/sif/dynamiccost.h
//   plus Costing_Enum_Name / Costing_Enum_Parse from valhalla/proto_conversions.h
//
// Generic factory for creating costing objects based on type. Derived costers (Auto, Truck, ...)
// belong to later port slices; their factory functions are registered here but throw a clear
// NotImplementedException until ported. The registration table, type mapping, and CreateModeCosting
// flow mirror the C++ exactly.

using System;
using System.Collections.Generic;

namespace SharpNinja.Valhalla.Sif;

/// <summary>
/// The date-time tracking mode for a request. Faithful port of the proto
/// <c>Options::DateTimeType</c> enum values that the thor path algorithms branch on.
/// </summary>
public enum DateTimeType
{
    /// <summary>No date/time specified (time-independent routing).</summary>
    NoTime = 0,

    /// <summary>Depart at the given local date/time.</summary>
    DepartAt = 1,

    /// <summary>Arrive by the given local date/time.</summary>
    ArriveBy = 2,

    /// <summary>Use the given local date/time but do not advance it as the path lengthens.</summary>
    Invariant = 3,
}

/// <summary>
/// Strategy for tracking time on the non-time-aware end of a bidirectional search. Faithful port of
/// the proto <c>Options::ReverseTimeTracking</c> enum.
/// </summary>
public enum ReverseTimeTracking
{
    /// <summary>Do not track time on the reverse expansion (use an invalid TimeInfo).</summary>
    RttDisabled = 0,

    /// <summary>Estimate the reverse start time from the beeline duration heuristic.</summary>
    RttHeuristic = 1,
}

/// <summary>
/// Top-level request options placeholder mirroring the parts of the protobuf <c>Options</c> message
/// that the cost factory AND the thor path algorithms read: the main costing type, the per-type
/// costing map, the date-time tracking mode, the alternate-route count, and the reverse-time-tracking
/// strategy.
/// PORT-NOTE: only the fields the ported engine surface needs are modeled here (no wire format).
/// </summary>
public sealed class Options
{
    public Costing.Type CostingType { get; set; } = Costing.Type.None;
    public Dictionary<Costing.Type, Costing> Costings { get; } = new();

    /// <summary>Date-time tracking mode (proto <c>date_time_type</c>). Defaults to time-independent.</summary>
    public DateTimeType DateTimeType { get; set; } = DateTimeType.NoTime;

    /// <summary>Whether <see cref="DateTimeType"/> has been set (proto <c>has_date_time_type_case</c>).</summary>
    public bool HasDateTimeType { get; set; }

    /// <summary>Number of alternate routes requested (proto <c>alternates</c>); 0 means none.</summary>
    public uint Alternates { get; set; }

    /// <summary>Whether <see cref="Alternates"/> was provided (proto <c>has_alternates_case</c>).</summary>
    public bool HasAlternates { get; set; }

    /// <summary>Reverse-time-tracking strategy (proto <c>reverse_time_tracking</c>).</summary>
    public ReverseTimeTracking ReverseTimeTracking { get; set; } = ReverseTimeTracking.RttDisabled;

    // ---- odin directions fields (proto Options) ------------------------------------------------
    // The C++ Options message (options.proto) carries both costing and directions fields. odin's
    // ManeuversBuilder / DirectionsBuilder read these three; they live on the same Options class to
    // mirror the single proto message.

    /// <summary>The distance units. Faithful port of <c>units()</c> (defaults to kilometers).</summary>
    public OptionsUnits Units { get; set; } = OptionsUnits.Kilometers;

    /// <summary>The directions type. Faithful port of <c>directions_type()</c> (defaults to instructions).</summary>
    public DirectionsType DirectionsType { get; set; } = DirectionsType.Instructions;

    /// <summary>
    /// True if roundabout exit maneuvers should be produced (vs. collapsed into the enter maneuver).
    /// Faithful port of <c>roundabout_exits()</c> (defaults to true).
    /// </summary>
    public bool RoundaboutExits { get; set; } = true;

    /// <summary>Convenience: true if <see cref="Units"/> selects miles.</summary>
    public bool UnitsAreMiles => Units == OptionsUnits.Miles;
}

/// <summary>
/// Distance units. Faithful port of <c>Options.Units</c> (options.proto). Underlying values match
/// the proto exactly.
/// </summary>
public enum OptionsUnits
{
    /// <summary>Kilometers.</summary>
    Kilometers = 0,

    /// <summary>Miles.</summary>
    Miles = 1,
}

/// <summary>
/// The directions type requested. Faithful port of <c>DirectionsType</c> (options.proto). Underlying
/// values match the proto exactly.
/// </summary>
public enum DirectionsType
{
    /// <summary>No instructions or maneuvers are produced (shape only).</summary>
    None = 0,

    /// <summary>Maneuvers are produced but no localized prose instructions.</summary>
    Maneuvers = 1,

    /// <summary>Maneuvers and localized prose instructions are produced.</summary>
    Instructions = 2,
}

/// <summary>cost_ptr_t: a reference to a costing object. Faithful alias of <c>std::shared_ptr&lt;DynamicCost&gt;</c>.</summary>
public sealed class ModeCosting
{
    // mode_costing_t: std::array<cost_ptr_t, kMaxTravelMode>
    private readonly DynamicCost?[] _costing = new DynamicCost?[(int)TravelMode.MaxTravelMode];

    public DynamicCost? this[int index]
    {
        get => _costing[index];
        set => _costing[index] = value;
    }
}

/// <summary>
/// Costing-type helpers and the costing-type mapping table. Faithful port of
/// <c>kCostingTypeMapping</c> (dynamiccost.h) and <c>Costing_Enum_Name</c>/<c>Costing_Enum_Parse</c>
/// (proto_conversions.h).
/// </summary>
public static class CostingTypes
{
    /// <summary>
    /// Maps a costing type to the set of costings it expands into. Faithful port of
    /// <c>kCostingTypeMapping</c>.
    /// </summary>
    public static readonly IReadOnlyDictionary<Costing.Type, IReadOnlyList<Costing.Type>> CostingTypeMapping =
        new Dictionary<Costing.Type, IReadOnlyList<Costing.Type>>
        {
            [Costing.Type.None] = new[] { Costing.Type.None },
            [Costing.Type.Bicycle] = new[] { Costing.Type.Bicycle },
            [Costing.Type.Bus] = new[] { Costing.Type.Bus },
            [Costing.Type.MotorScooter] = new[] { Costing.Type.MotorScooter },
            [Costing.Type.Multimodal] = new[] { Costing.Type.Multimodal, Costing.Type.Transit, Costing.Type.Pedestrian },
            [Costing.Type.Pedestrian] = new[] { Costing.Type.Pedestrian },
            [Costing.Type.Transit] = new[] { Costing.Type.Transit, Costing.Type.Pedestrian },
            [Costing.Type.Truck] = new[] { Costing.Type.Truck },
            [Costing.Type.Motorcycle] = new[] { Costing.Type.Motorcycle },
            [Costing.Type.Taxi] = new[] { Costing.Type.Taxi },
            [Costing.Type.Auto] = new[] { Costing.Type.Auto },
            [Costing.Type.Bikeshare] = new[] { Costing.Type.Bikeshare, Costing.Type.Pedestrian, Costing.Type.Bicycle },
            [Costing.Type.AutoPedestrian] = new[] { Costing.Type.AutoPedestrian, Costing.Type.Pedestrian, Costing.Type.Auto },
        };

    /// <summary>Returns the proto enum name for a costing type (e.g. <c>auto_</c>, <c>none_</c>).</summary>
    public static string EnumName(Costing.Type type) => type switch
    {
        Costing.Type.None => "none_",
        Costing.Type.Bicycle => "bicycle",
        Costing.Type.Bus => "bus",
        Costing.Type.MotorScooter => "motor_scooter",
        Costing.Type.Multimodal => "multimodal",
        Costing.Type.Pedestrian => "pedestrian",
        Costing.Type.Transit => "transit",
        Costing.Type.Truck => "truck",
        Costing.Type.Motorcycle => "motorcycle",
        Costing.Type.Taxi => "taxi",
        Costing.Type.Auto => "auto",
        Costing.Type.Bikeshare => "bikeshare",
        Costing.Type.AutoPedestrian => "auto_pedestrian",
        _ => string.Empty,
    };

    /// <summary>Parses a proto enum name into a costing type. Returns false if unknown.</summary>
    public static bool EnumParse(string name, out Costing.Type type)
    {
        switch (name)
        {
            case "none_": type = Costing.Type.None; return true;
            case "bicycle": type = Costing.Type.Bicycle; return true;
            case "bus": type = Costing.Type.Bus; return true;
            case "motor_scooter": type = Costing.Type.MotorScooter; return true;
            case "multimodal": type = Costing.Type.Multimodal; return true;
            case "pedestrian": type = Costing.Type.Pedestrian; return true;
            case "transit": type = Costing.Type.Transit; return true;
            case "truck": type = Costing.Type.Truck; return true;
            case "motorcycle": type = Costing.Type.Motorcycle; return true;
            case "taxi": type = Costing.Type.Taxi; return true;
            case "auto": type = Costing.Type.Auto; return true;
            case "bikeshare": type = Costing.Type.Bikeshare; return true;
            case "auto_pedestrian": type = Costing.Type.AutoPedestrian; return true;
            default: type = Costing.Type.None; return false;
        }
    }
}

/// <summary>
/// Generic factory class for creating costing objects based on type name. Faithful port of
/// <c>valhalla::sif::CostFactory</c>.
/// </summary>
public sealed class CostFactory
{
    /// <summary>factory_function_t: creates a costing from its options.</summary>
    public delegate DynamicCost FactoryFunction(Costing options);

    private readonly Dictionary<Costing.Type, FactoryFunction> _factoryFuncs = new();

    /// <summary>
    /// Constructor. Registers the factory functions for each costing type. Composite costings are
    /// registered with the no-cost dummy. PORT-NOTE: the concrete <c>Create*Cost</c> functions live
    /// in later port slices (autocost.cc, truckcost.cc, ...); they are wired here to a throwing stub
    /// so the registration shape matches the C++ <c>CostFactory()</c> constructor exactly.
    /// </summary>
    public CostFactory()
    {
        Register(Costing.Type.Auto, CreateAutoCost);
        // auto_data_fix was deprecated
        // auto_shorter was deprecated
        Register(Costing.Type.Bicycle, CreateBicycleCost);
        Register(Costing.Type.Bus, CreateBusCost);
        Register(Costing.Type.Taxi, CreateTaxiCost);
        Register(Costing.Type.MotorScooter, CreateMotorScooterCost);
        Register(Costing.Type.Motorcycle, CreateMotorcycleCost);
        Register(Costing.Type.Pedestrian, CreatePedestrianCost);
        Register(Costing.Type.Truck, CreateTruckCost);
        Register(Costing.Type.Transit, CreateTransitCost);
        Register(Costing.Type.Multimodal, CreateNoCost); // dummy so it behaves like the rest
        Register(Costing.Type.None, CreateNoCost);
        Register(Costing.Type.Bikeshare, CreateNoCost);       // dummy
        Register(Costing.Type.AutoPedestrian, CreateNoCost);  // dummy
    }

    /// <summary>Register the callback to create this type of cost.</summary>
    public void Register(Costing.Type costing, FactoryFunction function)
    {
        _factoryFuncs.Remove(costing);
        _factoryFuncs[costing] = function;
    }

    /// <summary>Make a cost from request options (selects the main costing type).</summary>
    public DynamicCost Create(Options options)
    {
        if (options.Costings.TryGetValue(options.CostingType, out var found))
            return Create(found);

        throw new InvalidOperationException("No costing options provided to cost factory");
    }

    /// <summary>Make a default cost from its specified type.</summary>
    public DynamicCost Create(Costing.Type costingType)
    {
        var defaultCosting = new Costing();
        defaultCosting.SetType(costingType);
        return Create(defaultCosting);
    }

    /// <summary>Make a cost from its specified type and options.</summary>
    public DynamicCost Create(Costing costing)
    {
        if (!_factoryFuncs.TryGetValue(costing.CostingType, out var itr))
        {
            string costingStr = CostingTypes.EnumName(costing.CostingType);
            throw new InvalidOperationException($"No costing method found for '{costingStr}'");
        }

        return itr(costing);
    }

    /// <summary>
    /// Construct the costing(s) for an options' main costing type and report the travel mode.
    /// Faithful port of <c>CreateModeCosting</c>.
    /// </summary>
    public ModeCosting CreateModeCosting(Options options, out TravelMode mode)
    {
        var modeCosting = new ModeCosting();
        mode = TravelMode.MaxTravelMode;

        foreach (var costing in CostingTypes.CostingTypeMapping[options.CostingType])
        {
            DynamicCost cost = Create(options.Costings[costing]);
            mode = cost.TravelMode();
            modeCosting[(int)mode] = cost;
        }

        if (options.CostingType == Costing.Type.Multimodal ||
            options.CostingType == Costing.Type.Transit ||
            options.CostingType == Costing.Type.Bikeshare)
        {
            mode = TravelMode.Pedestrian;
            modeCosting[(int)mode]!.SetProjectOnBssConnection(options.CostingType == Costing.Type.Bikeshare);
        }
        else if (options.CostingType == Costing.Type.AutoPedestrian)
        {
            mode = TravelMode.Drive;
        }

        if (mode == TravelMode.MaxTravelMode)
        {
            throw new InvalidOperationException(
                $"sif::CostFactory couldn't find a valid TravelMode for {CostingTypes.EnumName(options.CostingType)}");
        }

        return modeCosting;
    }

    // ---- factory functions ----
    // PORT-NOTE: These map to the C++ free functions Create<Type>Cost declared in the per-coster
    // headers. They are registered to match the C++ constructor; the concrete coster classes are
    // ported in later slices.

    private static DynamicCost CreateAutoCost(Costing options) => throw NotPorted("Auto");
    private static DynamicCost CreateBicycleCost(Costing options) => throw NotPorted("Bicycle");
    private static DynamicCost CreateBusCost(Costing options) => throw NotPorted("Bus");
    private static DynamicCost CreateTaxiCost(Costing options) => throw NotPorted("Taxi");
    private static DynamicCost CreateMotorScooterCost(Costing options) => throw NotPorted("MotorScooter");
    private static DynamicCost CreateMotorcycleCost(Costing options) => throw NotPorted("Motorcycle");
    private static DynamicCost CreatePedestrianCost(Costing options) => throw NotPorted("Pedestrian");
    private static DynamicCost CreateTruckCost(Costing options) => TruckCostFactory.CreateTruckCost(options);
    private static DynamicCost CreateTransitCost(Costing options) => throw NotPorted("Transit");
    private static DynamicCost CreateNoCost(Costing options) => throw NotPorted("NoCost");

    private static NotImplementedException NotPorted(string coster)
        => new NotImplementedException($"{coster} coster is part of a later sif port slice and is not yet ported.");
}
