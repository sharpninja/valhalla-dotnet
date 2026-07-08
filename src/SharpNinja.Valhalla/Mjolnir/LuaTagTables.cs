// Faithful C# port of the lookup tables and helper functions in Valhalla's graph.lua.
// Source: lua/graph.lua @ 3.7.0
//
// graph.lua normalizes raw OSM tags into Valhalla's tag set. The tables below are the
// exact key/value maps from the top of graph.lua, and the helpers (any_in, any_in_num,
// numeric_prefix, normalize_speed/weight/measurement, restriction_prefix/suffix,
// is_cash_only_payment, round) reproduce the Lua semantics including its truthiness:
// in Lua, table lookups that miss return nil; nil and "false" are falsey while any
// non-nil string (including "false"? no -- the strings stored are "true"/"false") are
// returned verbatim. We model "nil" as C# null and keep "true"/"false" as strings.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace SharpNinja.Valhalla.Mjolnir;

/// <summary>
/// Lookup tables and helper functions ported verbatim from <c>lua/graph.lua</c>. Used by
/// <see cref="WayTagTransform"/> and <see cref="NodeTagTransform"/>.
/// </summary>
internal static class LuaTagTables
{
    // highway -> default per-mode forward access ("true"/"false") for each highway type.
    // Mirrors the graph.lua `highway` table. Each entry is the inner kv pairs.
    public static readonly Dictionary<string, Dictionary<string, string>> Highway = BuildHighway();

    public static readonly Dictionary<string, int> RoadClass = new()
    {
        ["motorway"] = 0,
        ["motorway_link"] = 0,
        ["trunk"] = 1,
        ["trunk_link"] = 1,
        ["primary"] = 2,
        ["primary_link"] = 2,
        ["secondary"] = 3,
        ["secondary_link"] = 3,
        ["tertiary"] = 4,
        ["tertiary_link"] = 4,
        ["unclassified"] = 5,
        ["residential"] = 6,
        ["residential_link"] = 6,
    };

    public static readonly Dictionary<string, int> Restriction = new()
    {
        ["no_left_turn"] = 0,
        ["no_right_turn"] = 1,
        ["no_straight_on"] = 2,
        ["no_u_turn"] = 3,
        ["only_right_turn"] = 4,
        ["only_left_turn"] = 5,
        ["only_straight_on"] = 6,
        ["no_entry"] = 7,
        ["no_exit"] = 8,
        ["no_turn"] = 9,
    };

    // default_speed is indexed by road class (0..7).
    public static readonly Dictionary<int, int> DefaultSpeed = new()
    {
        [0] = 105,
        [1] = 90,
        [2] = 75,
        [3] = 60,
        [4] = 50,
        [5] = 40,
        [6] = 35,
        [7] = 25,
    };

    public static readonly Dictionary<string, string> Access = new()
    {
        ["yes"] = "true",
        ["private"] = "true",
        ["no"] = "false",
        ["permissive"] = "true",
        ["agricultural"] = "false",
        ["use_sidepath"] = "true",
        ["delivery"] = "true",
        ["designated"] = "true",
        ["dismount"] = "true",
        ["discouraged"] = "false",
        ["forestry"] = "false",
        ["destination"] = "true",
        ["customers"] = "true",
        ["official"] = "true",
        ["public"] = "true",
        ["restricted"] = "true",
        ["allowed"] = "true",
        ["emergency"] = "false",
        ["psv"] = "false",
        ["permit"] = "true",
        ["residents"] = "true",
    };

    public static readonly Dictionary<string, string> Private = new()
    {
        ["private"] = "true",
        ["destination"] = "true",
        ["customers"] = "true",
        ["delivery"] = "true",
        ["permit"] = "true",
        ["residents"] = "true",
    };

    public static readonly Dictionary<string, string> NoThruTraffic = new()
    {
        ["destination"] = "true",
        ["customers"] = "true",
        ["delivery"] = "true",
        ["permit"] = "true",
        ["residents"] = "true",
    };

    public static readonly Dictionary<string, string> Sidewalk = new()
    {
        ["both"] = "true",
        ["none"] = "false",
        ["no"] = "false",
        ["right"] = "true",
        ["left"] = "true",
        ["separate"] = "false",
        ["yes"] = "true",
        ["shared"] = "true",
        ["this"] = "true",
        ["detached"] = "false",
        ["raised"] = "true",
        ["separate_double"] = "false",
        ["sidepath"] = "false",
        ["explicit"] = "true",
    };

    public static readonly Dictionary<string, int> Use = new()
    {
        ["driveway"] = 4,
        ["alley"] = 5,
        ["parking_aisle"] = 6,
        ["emergency_access"] = 7,
        ["drive-through"] = 8,
    };

    public static readonly Dictionary<string, string> MotorVehicle = new()
    {
        ["yes"] = "true",
        ["private"] = "true",
        ["no"] = "false",
        ["permissive"] = "true",
        ["agricultural"] = "false",
        ["delivery"] = "true",
        ["designated"] = "true",
        ["discouraged"] = "false",
        ["forestry"] = "false",
        ["destination"] = "true",
        ["customers"] = "true",
        ["official"] = "true",
        ["public"] = "true",
        ["restricted"] = "true",
        ["allowed"] = "true",
        ["permit"] = "true",
        ["residents"] = "true",
    };

    // vehicle = motor_vehicle (graph.lua aliases them).
    public static readonly Dictionary<string, string> Vehicle = MotorVehicle;

    public static readonly Dictionary<string, string> Moped = new()
    {
        ["yes"] = "true",
        ["designated"] = "true",
        ["private"] = "true",
        ["permissive"] = "true",
        ["destination"] = "true",
        ["delivery"] = "true",
        ["dismount"] = "true",
        ["no"] = "false",
        ["unknown"] = "false",
        ["agricultural"] = "false",
        ["permit"] = "true",
        ["residents"] = "true",
    };

    public static readonly Dictionary<string, string> Foot = new()
    {
        ["yes"] = "true",
        ["private"] = "true",
        ["no"] = "false",
        ["permissive"] = "true",
        ["agricultural"] = "false",
        ["use_sidepath"] = "true",
        ["delivery"] = "true",
        ["designated"] = "true",
        ["discouraged"] = "false",
        ["forestry"] = "false",
        ["destination"] = "true",
        ["customers"] = "true",
        ["official"] = "true",
        ["public"] = "true",
        ["restricted"] = "true",
        ["crossing"] = "true",
        ["sidewalk"] = "true",
        ["allowed"] = "true",
        ["passable"] = "true",
        ["footway"] = "true",
        ["permit"] = "true",
        ["residents"] = "true",
    };

    public static readonly Dictionary<string, string> Wheelchair = new()
    {
        ["no"] = "false",
        ["yes"] = "true",
        ["designated"] = "true",
        ["limited"] = "true",
        ["official"] = "true",
        ["destination"] = "true",
        ["public"] = "true",
        ["permissive"] = "true",
        ["only"] = "true",
        ["private"] = "true",
        ["impassable"] = "false",
        ["partial"] = "false",
        ["bad"] = "false",
        ["half"] = "false",
        ["assisted"] = "true",
        ["permit"] = "true",
        ["residents"] = "true",
    };

    public static readonly Dictionary<string, string> Bus = new()
    {
        ["no"] = "false",
        ["yes"] = "true",
        ["designated"] = "true",
        ["urban"] = "true",
        ["permissive"] = "true",
        ["restricted"] = "true",
        ["destination"] = "true",
        ["delivery"] = "false",
        ["official"] = "true",
        ["permit"] = "true",
    };

    public static readonly Dictionary<string, string> Taxi = new()
    {
        ["no"] = "false",
        ["yes"] = "true",
        ["designated"] = "true",
        ["urban"] = "true",
        ["permissive"] = "true",
        ["restricted"] = "true",
        ["destination"] = "true",
        ["delivery"] = "false",
        ["official"] = "true",
        ["permit"] = "true",
    };

    public static readonly Dictionary<string, string> Psv = new()
    {
        ["bus"] = "true",
        ["taxi"] = "true",
        ["no"] = "false",
        ["yes"] = "true",
        ["designated"] = "true",
        ["permissive"] = "true",
        ["1"] = "true",
        ["2"] = "true",
    };

    public static readonly Dictionary<string, string> Truck = new()
    {
        ["designated"] = "true",
        ["yes"] = "true",
        ["no"] = "false",
        ["destination"] = "true",
        ["delivery"] = "true",
        ["local"] = "true",
        ["agricultural"] = "false",
        ["private"] = "true",
        ["discouraged"] = "false",
        ["permissive"] = "true",
        ["unsuitable"] = "false",
        ["official"] = "true",
        ["forestry"] = "false",
        ["permit"] = "true",
        ["residents"] = "true",
    };

    public static readonly Dictionary<string, string> TruckHgv = new()
    {
        ["designated"] = "true",
        ["local"] = "true",
    };

    public static readonly Dictionary<string, string> Hazmat = new()
    {
        ["designated"] = "true",
        ["yes"] = "true",
        ["no"] = "false",
        ["destination"] = "false",
        ["delivery"] = "false",
    };

    public static readonly Dictionary<string, string> Shoulder = new()
    {
        ["yes"] = "true",
        ["both"] = "true",
        ["no"] = "false",
    };

    public static readonly Dictionary<string, string> ShoulderRight = new()
    {
        ["right"] = "true",
    };

    public static readonly Dictionary<string, string> ShoulderLeft = new()
    {
        ["left"] = "true",
    };

    public static readonly Dictionary<string, string> Bicycle = new()
    {
        ["yes"] = "true",
        ["designated"] = "true",
        ["use_sidepath"] = "true",
        ["no"] = "false",
        ["permissive"] = "true",
        ["destination"] = "true",
        ["dismount"] = "true",
        ["lane"] = "true",
        ["track"] = "true",
        ["shared"] = "true",
        ["shared_lane"] = "true",
        ["sidepath"] = "true",
        ["share_busway"] = "true",
        ["none"] = "false",
        ["allowed"] = "true",
        ["private"] = "true",
        ["official"] = "true",
        ["permit"] = "true",
        ["residents"] = "true",
    };

    public static readonly Dictionary<string, string> Cycleway = new()
    {
        ["yes"] = "true",
        ["designated"] = "true",
        ["use_sidepath"] = "true",
        ["permissive"] = "true",
        ["destination"] = "true",
        ["dismount"] = "true",
        ["lane"] = "true",
        ["track"] = "true",
        ["shared"] = "true",
        ["shared_lane"] = "true",
        ["sidepath"] = "true",
        ["share_busway"] = "true",
        ["allowed"] = "true",
        ["private"] = "true",
        ["cyclestreet"] = "true",
        ["crossing"] = "true",
    };

    public static readonly Dictionary<string, string> BikeReverse = new()
    {
        ["opposite"] = "true",
        ["opposite_lane"] = "true",
        ["opposite_track"] = "true",
    };

    public static readonly Dictionary<string, string> BusReverse = new()
    {
        ["opposite"] = "true",
        ["opposite_lane"] = "true",
    };

    public static readonly Dictionary<string, int> Shared = new()
    {
        ["shared_lane"] = 1,
        ["share_busway"] = 1,
        ["shared"] = 1,
    };

    public static readonly Dictionary<string, int> Buffer = new()
    {
        ["yes"] = 2,
    };

    public static readonly Dictionary<string, int> Dedicated = new()
    {
        ["opposite_lane"] = 2,
        ["lane"] = 2,
        ["buffered_lane"] = 2,
    };

    public static readonly Dictionary<string, int> Separated = new()
    {
        ["opposite_track"] = 3,
        ["track"] = 3,
    };

    public static readonly Dictionary<string, string> Oneway = new()
    {
        ["no"] = "false",
        ["false"] = "false",
        ["-1"] = "true",
        ["yes"] = "true",
        ["true"] = "true",
        ["1"] = "true",
        ["reversible"] = "false",
        ["alternating"] = "false",
    };

    public static readonly Dictionary<string, string> Bridge = new()
    {
        ["yes"] = "true",
        ["no"] = "false",
        ["1"] = "true",
    };

    public static readonly Dictionary<string, string> Tunnel = new()
    {
        ["yes"] = "true",
        ["no"] = "false",
        ["1"] = "true",
        ["building_passage"] = "true",
    };

    public static readonly Dictionary<string, string> Toll = new()
    {
        ["yes"] = "true",
        ["no"] = "false",
        ["true"] = "true",
        ["false"] = "false",
        ["1"] = "true",
        ["interval"] = "true",
        ["snowmobile"] = "true",
    };

    public static readonly Dictionary<string, string> Lit = new()
    {
        ["yes"] = "true",
        ["no"] = "false",
        ["24/7"] = "true",
        ["automatic"] = "true",
        ["limited"] = "false",
        ["disused"] = "false",
        ["dusk-dawn"] = "true",
        ["sunset-sunrise"] = "true",
    };

    public static readonly Dictionary<string, int> ConditionalAccessRestriction = new()
    {
        ["none @ destination"] = 1,
        ["none @ delivery"] = 1,
        ["no @ destination"] = 1,
        ["none @ (destination)"] = 1,
    };

    // ---- Node access-mask tables (values are bit masks) -----------------------

    public static readonly Dictionary<string, int> MotorVehicleNode = new()
    {
        ["yes"] = 1,
        ["private"] = 1,
        ["no"] = 0,
        ["permissive"] = 1,
        ["agricultural"] = 0,
        ["delivery"] = 1,
        ["designated"] = 1,
        ["discouraged"] = 0,
        ["forestry"] = 0,
        ["destination"] = 1,
        ["customers"] = 1,
        ["official"] = 1,
        ["public"] = 1,
        ["restricted"] = 1,
        ["allowed"] = 1,
        ["permit"] = 1,
        ["residents"] = 1,
    };

    public static readonly Dictionary<string, int> BicycleNode = new()
    {
        ["yes"] = 4,
        ["designated"] = 4,
        ["use_sidepath"] = 4,
        ["no"] = 0,
        ["permissive"] = 4,
        ["destination"] = 4,
        ["dismount"] = 4,
        ["lane"] = 4,
        ["track"] = 4,
        ["shared"] = 4,
        ["shared_lane"] = 4,
        ["sidepath"] = 4,
        ["share_busway"] = 4,
        ["none"] = 0,
        ["allowed"] = 4,
        ["private"] = 4,
        ["official"] = 4,
        ["permit"] = 4,
        ["residents"] = 4,
    };

    public static readonly Dictionary<string, int> FootNode = new()
    {
        ["yes"] = 2,
        ["private"] = 2,
        ["no"] = 0,
        ["permissive"] = 2,
        ["agricultural"] = 0,
        ["use_sidepath"] = 2,
        ["delivery"] = 2,
        ["designated"] = 2,
        ["discouraged"] = 0,
        ["forestry"] = 0,
        ["destination"] = 2,
        ["customers"] = 2,
        ["official"] = 2,
        ["public"] = 2,
        ["restricted"] = 2,
        ["crossing"] = 2,
        ["sidewalk"] = 2,
        ["allowed"] = 2,
        ["passable"] = 2,
        ["footway"] = 2,
        ["permit"] = 2,
        ["residents"] = 2,
    };

    public static readonly Dictionary<string, int> WheelchairNode = new()
    {
        ["no"] = 0,
        ["yes"] = 256,
        ["designated"] = 256,
        ["limited"] = 256,
        ["official"] = 256,
        ["destination"] = 256,
        ["public"] = 256,
        ["permissive"] = 256,
        ["only"] = 256,
        ["private"] = 256,
        ["impassable"] = 0,
        ["partial"] = 0,
        ["bad"] = 0,
        ["half"] = 0,
        ["assisted"] = 256,
        ["permit"] = 256,
        ["residents"] = 256,
    };

    public static readonly Dictionary<string, int> MopedNode = new()
    {
        ["yes"] = 512,
        ["designated"] = 512,
        ["private"] = 512,
        ["permissive"] = 512,
        ["destination"] = 512,
        ["delivery"] = 512,
        ["dismount"] = 512,
        ["no"] = 0,
        ["unknown"] = 0,
        ["agricultural"] = 0,
        ["permit"] = 512,
        ["residents"] = 512,
    };

    public static readonly Dictionary<string, int> MotorCycleNode = new()
    {
        ["yes"] = 1024,
        ["private"] = 1024,
        ["no"] = 0,
        ["permissive"] = 1024,
        ["agricultural"] = 0,
        ["delivery"] = 1024,
        ["designated"] = 1024,
        ["discouraged"] = 0,
        ["forestry"] = 0,
        ["destination"] = 1024,
        ["customers"] = 1024,
        ["official"] = 1024,
        ["public"] = 1024,
        ["restricted"] = 1024,
        ["allowed"] = 1024,
        ["permit"] = 1024,
    };

    public static readonly Dictionary<string, int> BusNode = new()
    {
        ["no"] = 0,
        ["yes"] = 64,
        ["designated"] = 64,
        ["urban"] = 64,
        ["permissive"] = 64,
        ["restricted"] = 64,
        ["destination"] = 64,
        ["delivery"] = 0,
        ["official"] = 64,
        ["permit"] = 64,
    };

    public static readonly Dictionary<string, int> TaxiNode = new()
    {
        ["no"] = 0,
        ["yes"] = 32,
        ["designated"] = 32,
        ["urban"] = 32,
        ["permissive"] = 32,
        ["restricted"] = 32,
        ["destination"] = 32,
        ["delivery"] = 0,
        ["official"] = 32,
        ["permit"] = 32,
    };

    public static readonly Dictionary<string, int> TruckNode = new()
    {
        ["designated"] = 8,
        ["yes"] = 8,
        ["no"] = 0,
        ["destination"] = 8,
        ["delivery"] = 8,
        ["local"] = 8,
        ["agricultural"] = 0,
        ["private"] = 8,
        ["discouraged"] = 0,
        ["permissive"] = 8,
        ["unsuitable"] = 0,
        ["official"] = 8,
        ["forestry"] = 0,
        ["permit"] = 8,
        ["residents"] = 8,
    };

    public static readonly Dictionary<string, int> PsvBusNode = new()
    {
        ["bus"] = 64,
        ["no"] = 0,
        ["yes"] = 64,
        ["designated"] = 64,
        ["permissive"] = 64,
        ["1"] = 64,
        ["2"] = 64,
    };

    public static readonly Dictionary<string, int> PsvTaxiNode = new()
    {
        ["taxi"] = 32,
        ["no"] = 0,
        ["yes"] = 32,
        ["designated"] = 32,
        ["permissive"] = 32,
        ["1"] = 32,
        ["2"] = 32,
    };

    private static Dictionary<string, Dictionary<string, string>> BuildHighway()
    {
        // Helper to build one inner table from the 8 standard mode flags.
        static Dictionary<string, string> Row(
            string a, string truck, string bus, string taxi, string moped, string mc, string ped, string bike) =>
            new()
            {
                ["auto_forward"] = a,
                ["truck_forward"] = truck,
                ["bus_forward"] = bus,
                ["taxi_forward"] = taxi,
                ["moped_forward"] = moped,
                ["motorcycle_forward"] = mc,
                ["pedestrian_forward"] = ped,
                ["bike_forward"] = bike,
            };

        const string T = "true";
        const string F = "false";

        return new Dictionary<string, Dictionary<string, string>>
        {
            ["motorway"] = Row(T, T, T, T, F, T, F, F),
            ["motorway_link"] = Row(T, T, T, T, F, T, F, F),
            ["trunk"] = Row(T, T, T, T, T, T, T, T),
            ["trunk_link"] = Row(T, T, T, T, T, T, T, T),
            ["primary"] = Row(T, T, T, T, T, T, T, T),
            ["primary_link"] = Row(T, T, T, T, T, T, T, T),
            ["secondary"] = Row(T, T, T, T, T, T, T, T),
            ["secondary_link"] = Row(T, T, T, T, T, T, T, T),
            ["residential"] = Row(T, T, T, T, T, T, T, T),
            ["residential_link"] = Row(T, T, T, T, T, T, T, T),
            ["service"] = Row(T, T, T, T, T, T, T, T),
            ["tertiary"] = Row(T, T, T, T, T, T, T, T),
            ["tertiary_link"] = Row(T, T, T, T, T, T, T, T),
            ["road"] = Row(T, T, T, T, T, T, T, T),
            ["track"] = Row(T, T, T, T, T, T, T, T),
            ["unclassified"] = Row(T, T, T, T, T, T, T, T),
            ["undefined"] = Row(F, F, F, F, F, F, F, F),
            ["unknown"] = Row(F, F, F, F, F, F, F, F),
            ["living_street"] = Row(T, T, T, T, T, T, T, T),
            ["footway"] = Row(F, F, F, F, F, F, T, F),
            ["pedestrian"] = Row(F, F, F, F, F, F, T, F),
            ["steps"] = Row(F, F, F, F, F, F, T, T),
            ["bridleway"] = Row(F, F, F, F, F, F, F, F),
            ["cycleway"] = Row(F, F, F, F, F, F, F, T),
            ["path"] = Row(F, F, F, F, F, F, T, T),
            ["bus_guideway"] = Row(F, F, T, F, F, F, F, F),
            ["busway"] = Row(F, F, T, F, F, F, F, F),
            ["corridor"] = Row(F, F, F, F, F, F, T, F),
            ["elevator"] = Row(F, F, F, F, F, F, T, F),
            ["platform"] = Row(F, F, F, F, F, F, T, F),
            ["via_ferrata"] = Row(F, F, F, F, F, F, T, F),
        };
    }

    // ---- Helper functions (faithful to graph.lua) ----------------------------

    /// <summary>
    /// Faithful port of graph.lua <c>round(val, n)</c>. With no precision rounds to the
    /// nearest integer (floor(val + 0.5)); with precision rounds to n decimals.
    /// </summary>
    public static double Round(double val, int? n = null)
    {
        if (n.HasValue)
        {
            double p = Math.Pow(10, n.Value);
            return Math.Floor(val * p + 0.5) / p;
        }

        return Math.Floor(val + 0.5);
    }

    /// <summary>
    /// Faithful port of <c>any_in(table, key)</c>: if key is a ";"-separated list, return
    /// "true" if any part maps to "true", otherwise the last found value, else nil.
    /// </summary>
    public static string? AnyIn(Dictionary<string, string> table, string? key)
    {
        if (key == null)
        {
            return null;
        }

        if (table.TryGetValue(key, out string? direct))
        {
            return direct;
        }

        string? val = null;
        foreach (string part in key.Split(';'))
        {
            if (table.TryGetValue(part, out string? v))
            {
                val = v;
            }

            if (val == "true")
            {
                break;
            }
        }

        return val;
    }

    /// <summary>
    /// Faithful port of <c>any_in_num(table, key)</c>: like <see cref="AnyIn"/> but for
    /// numeric (mask) tables. Returns the value &gt; 0 for any part, 0 for any 0, else nil.
    /// </summary>
    public static int? AnyInNum(Dictionary<string, int> table, string? key)
    {
        if (key == null)
        {
            return null;
        }

        if (table.TryGetValue(key, out int direct))
        {
            return direct;
        }

        int? val = null;
        foreach (string part in key.Split(';'))
        {
            if (table.TryGetValue(part, out int v))
            {
                val = v;
            }

            if ((val ?? 0) > 0)
            {
                break;
            }
        }

        return val;
    }

    /// <summary>
    /// Faithful port of <c>numeric_prefix(num_str, allow_decimals)</c>. Returns the numeric
    /// prefix of the string, or null if there is none.
    /// </summary>
    public static string? NumericPrefix(string? numStr, bool allowDecimals)
    {
        if (numStr == null)
        {
            return null;
        }

        int index = 0;
        bool seenDot = false;
        foreach (char c in numStr)
        {
            if (!char.IsDigit(c))
            {
                if (c == '.')
                {
                    if (!allowDecimals || seenDot)
                    {
                        break;
                    }

                    seenDot = true;
                }
                else
                {
                    break;
                }
            }

            index++;
        }

        if (index == 0)
        {
            return null;
        }

        return numStr.Substring(0, index);
    }

    /// <summary>Faithful port of <c>normalize_speed(speed)</c>. Returns null if out of range/invalid.</summary>
    public static double? NormalizeSpeed(string? speed)
    {
        string? prefix = NumericPrefix(speed, false);
        if (prefix == null || !TryParseLua(prefix, out double num))
        {
            return null;
        }

        // speed is non-null here because prefix was non-null.
        if (speed!.EndsWith("mph", StringComparison.Ordinal))
        {
            num = Round(num * 1.609344);
        }

        if (num > 150 || num < 10)
        {
            return null;
        }

        return num;
    }

    /// <summary>Faithful port of <c>normalize_weight(weight)</c> (tonnes/tons/lbs/kg suffixes).</summary>
    public static double? NormalizeWeight(string? weight)
    {
        if (weight == null)
        {
            return null;
        }

        // w = weight with whitespace removed.
        string w = RemoveWhitespace(weight);
        string? num = NumericPrefix(w, true);
        if (num == null)
        {
            return null;
        }

        if (!TryParseLua(num, out double numVal))
        {
            return null;
        }

        if (w.EndsWith("t", StringComparison.Ordinal) ||
            w.EndsWith("tonne", StringComparison.Ordinal) ||
            w.EndsWith("tonnes", StringComparison.Ordinal))
        {
            if (num + "t" == w || num + "tonne" == w || num + "tonnes" == w)
            {
                return Round(numVal, 2);
            }
        }

        if (w.EndsWith("ton", StringComparison.Ordinal) || w.EndsWith("tons", StringComparison.Ordinal))
        {
            if (num + "ton" == w || num + "tons" == w)
            {
                return Round(numVal, 2);
            }
        }

        if (w.EndsWith("lb", StringComparison.Ordinal) || w.EndsWith("lbs", StringComparison.Ordinal))
        {
            if (num + "lb" == w || num + "lbs" == w)
            {
                return Round(numVal / 2000.0, 2); // convert to tons
            }
        }

        if (w.EndsWith("kg", StringComparison.Ordinal))
        {
            if (num + "kg" == w)
            {
                return Round(numVal / 1000.0, 2);
            }
        }

        return Round(numVal, 2);
    }

    /// <summary>Faithful port of <c>normalize_measurement(measurement)</c> (m/cm/ft/in compounds).</summary>
    public static double? NormalizeMeasurement(string? measurement)
    {
        if (measurement == null)
        {
            return null;
        }

        // turn commas into dots to handle European-style decimal separators.
        measurement = measurement.Replace(',', '.');

        if (TryParseLua(measurement, out double simple))
        {
            return Round(simple, 2);
        }

        double sum = 0;
        int count = 0;

        // Mirror the Lua pattern "(%d+[.,]?%d*) *([a-zA-Z\"']*)".
        int i = 0;
        while (i < measurement.Length)
        {
            // Match number: %d+ [.,]? %d*
            int start = i;
            int digits = 0;
            while (i < measurement.Length && char.IsDigit(measurement[i]))
            {
                i++;
                digits++;
            }

            if (digits == 0)
            {
                i++;
                continue;
            }

            if (i < measurement.Length && (measurement[i] == '.' || measurement[i] == ','))
            {
                i++;
            }

            while (i < measurement.Length && char.IsDigit(measurement[i]))
            {
                i++;
            }

            string item = measurement.Substring(start, i - start);

            // optional spaces.
            while (i < measurement.Length && measurement[i] == ' ')
            {
                i++;
            }

            // unit: [a-zA-Z"']*
            int unitStart = i;
            while (i < measurement.Length &&
                   (char.IsLetter(measurement[i]) || measurement[i] == '"' || measurement[i] == '\''))
            {
                i++;
            }

            string unit = measurement.Substring(unitStart, i - unitStart).ToLowerInvariant();

            if (!TryParseLua(item, out double itemNum))
            {
                return null;
            }

            if (unit == "m" || unit == "meter" || unit == "meters")
            {
                sum += itemNum;
            }
            else if (unit == "cm")
            {
                sum += itemNum * 0.01;
            }
            else if (unit == "ft" || unit == "feet" || unit == "foot" || unit == "'")
            {
                sum += itemNum * 0.3048;
            }
            else if (unit == "in" || unit == "inches" || unit == "inch" || unit == "\"" || unit == "''")
            {
                sum += itemNum * 0.0254;
            }
            else
            {
                return null;
            }

            count++;
        }

        if (count > 0)
        {
            return Round(sum, 2);
        }

        return null;
    }

    /// <summary>Faithful port of <c>restriction_prefix(restriction_str)</c>.</summary>
    public static string? RestrictionPrefix(string? restrictionStr)
    {
        if (restrictionStr == null)
        {
            return null;
        }

        int index = 0;
        bool found = false;
        foreach (char c in restrictionStr)
        {
            if (c == '@')
            {
                found = true;
                break;
            }

            if (c != ' ')
            {
                index++;
            }
        }

        if (!found)
        {
            return null;
        }

        // Lua string:sub(0, index) is 1-based and inclusive; sub(0,..) == sub(1,..).
        return LuaSub(restrictionStr, 0, index);
    }

    /// <summary>Faithful port of <c>restriction_suffix(restriction_str)</c>.</summary>
    public static string? RestrictionSuffix(string? restrictionStr)
    {
        if (restrictionStr == null)
        {
            return null;
        }

        int index = 0;
        bool found = false;
        foreach (char c in restrictionStr)
        {
            if (found)
            {
                if (c != ' ')
                {
                    index++;
                    break;
                }
            }
            else if (c == '@')
            {
                found = true;
            }

            index++;
        }

        if (!found)
        {
            return null;
        }

        return LuaSub(restrictionStr, index, restrictionStr.Length);
    }

    /// <summary>
    /// Faithful port of <c>is_cash_only_payment(kv)</c>: true if cash payment types are
    /// present and no non-cash types are present (treating anything that is not "NO" as yes).
    /// </summary>
    public static bool IsCashOnlyPayment(IReadOnlyDictionary<string, string> kv)
    {
        bool allowsCash = false;
        bool allowsNonCash = false;
        foreach (KeyValuePair<string, string> pair in kv)
        {
            string key = pair.Key;
            if (key.Length >= 8 && key.Substring(0, 8) == "payment:")
            {
                string paymentType = key.Substring(8);
                bool isCashType = paymentType == "cash" || paymentType == "notes" || paymentType == "coins";
                if (isCashType && !allowsCash)
                {
                    allowsCash = pair.Value.ToUpperInvariant() != "NO";
                }

                if (!isCashType && !allowsNonCash)
                {
                    allowsNonCash = pair.Value.ToUpperInvariant() != "NO";
                }
            }
        }

        return allowsCash && !allowsNonCash;
    }

    /// <summary>
    /// Lua <c>tonumber</c>-like parse: a leading numeric value with optional sign/decimal,
    /// matching graph.lua's reliance on tonumber. Returns false (and 0) for non-numeric input.
    /// </summary>
    public static bool TryParseLua(string? s, out double value)
    {
        value = 0;
        if (string.IsNullOrEmpty(s))
        {
            return false;
        }

        return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    private static string RemoveWhitespace(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (char c in s)
        {
            if (!char.IsWhiteSpace(c))
            {
                sb.Append(c);
            }
        }

        return sb.ToString();
    }

    // Lua string.sub(s, i, j) with 1-based, inclusive indices. i==0 is treated as 1.
    private static string LuaSub(string s, int i, int j)
    {
        int len = s.Length;
        if (i < 1)
        {
            i = 1;
        }

        if (j > len)
        {
            j = len;
        }

        if (i > j)
        {
            return string.Empty;
        }

        return s.Substring(i - 1, j - (i - 1));
    }
}
