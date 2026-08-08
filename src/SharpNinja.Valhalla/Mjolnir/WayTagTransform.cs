// Faithful C# port of graph.lua's way tag normalization (filter_tags_generic + ways_proc).
// Source: lua/graph.lua @ 3.7.0, functions filter_tags_generic (line ~920) and ways_proc.
//
// This reproduces, line-for-line, the Lua logic that turns raw OSM way tags into Valhalla's
// normalized tag set: per-mode forward/backward access, oneway handling and direction flips,
// road_class, use, default_speed, cycle lanes, shoulders, surface, lanes, hov, tunnel/toll/
// bridge, truck (HGV) goodies, hazmat, bike network mask, and the construction shut-off.
//
// Lua semantics notes:
//   * Tag lookups return nil (null here) when absent.
//   * `a or b` returns a unless a is falsey; the only string the transform stores that is
//     falsey is *none* ("false" is truthy in Lua), so for string? values `Or(a,b)` returns
//     a unless a is null. We use Or() for that and TruthyOr()/IsTruthy where booleans matter.

using System.Collections.Generic;

namespace SharpNinja.Valhalla.Mjolnir;

/// <summary>
/// Way tag transform. Faithful port of graph.lua <c>filter_tags_generic</c> (invoked from
/// <c>ways_proc</c>). <see cref="Transform"/> returns 1 to filter (drop) the way, 0 to keep it,
/// mutating <paramref name="tags"/> in place into the normalized tag set.
/// </summary>
public static class WayTagTransform
{
    // Lua `a or b` for string? values: returns a unless a is null (nil). Note "false" is
    // truthy in Lua so a literal "false" string is returned as-is.
    private static string? Or(string? a, string? b) => a ?? b;

    private static string? Or(string? a, string? b, string? c) => a ?? b ?? c;

    // Lua truthiness for a string? lookup result: nil is falsey, everything else truthy.
    private static bool Truthy(string? v) => v != null;

    /// <summary>
    /// Normalizes raw OSM way tags. Returns 1 if the way should be filtered out, else 0.
    /// Mirrors <c>ways_proc</c> + <c>filter_tags_generic</c>.
    /// </summary>
    public static int Transform(IDictionary<string, string> tags)
    {
        // ways_proc: if there were no tags passed in, drop the way.
        if (tags.Count == 0)
        {
            return 1;
        }

        var kv = new LuaKv(tags);
        int filter = FilterTagsGeneric(kv);

        // Copy the normalized table back out.
        tags.Clear();
        foreach (KeyValuePair<string, string> p in kv.Raw)
        {
            tags[p.Key] = p.Value;
        }

        return filter;
    }

    private static int FilterTagsGeneric(LuaKv kv)
    {
        // construction without a construction tag, or proposed -> filter.
        if ((kv.Eq("highway", "construction") && !kv.Has("construction")) || kv.Eq("highway", "proposed"))
        {
            return 1;
        }

        // Valhalla 3.8.3 keeps pedestrian areas for the optional pedestrian-area graph pass.
        // Other area rings remain non-routable and are filtered here.
        if (kv.Eq("area", "yes"))
        {
            if (kv.Eq("highway", "pedestrian"))
            {
                kv.SetString("pedestrian_area", "true");
            }
            else
            {
                return 1;
            }
        }

        Dictionary<string, string>? forward = null;
        if (kv.GetString("highway") is { } hw && LuaTagTables.Highway.TryGetValue(hw, out Dictionary<string, string>? f))
        {
            forward = f;
        }

        if (kv.Eq("highway", "construction") && kv.GetString("construction") is { } cons &&
            LuaTagTables.Highway.TryGetValue(cons, out Dictionary<string, string>? cf))
        {
            forward = cf;
        }

        bool ferry = kv.Eq("route", "ferry");
        bool rail = kv.Eq("route", "shuttle_train");
        string? access = LuaTagTables.AnyIn(LuaTagTables.Access, kv.GetString("access"));

        kv.SetBool("emergency_forward", false);
        kv.SetBool("emergency_backward", false);

        if (ferry || rail || kv.Has("highway"))
        {
            if (kv.Eq("access", "emergency") || kv.Eq("emergency", "yes") || kv.Eq("service", "emergency_access"))
            {
                kv.SetBool("emergency_forward", true);
                kv.SetBool("emergency_tag", true);
            }

            if (kv.Eq("emergency", "no"))
            {
                kv.SetBool("emergency_tag", false);
            }
        }

        string? vehicleAccess = LuaTagTables.AnyIn(LuaTagTables.Vehicle, kv.GetString("vehicle"));
        string? motorVehicleAccess = Or(LuaTagTables.AnyIn(LuaTagTables.MotorVehicle, kv.GetString("motor_vehicle")), vehicleAccess);

        if (forward != null)
        {
            foreach (KeyValuePair<string, string> kvp in forward)
            {
                kv.SetString(kvp.Key, kvp.Value);
            }

            // access=private not to be combined with other values.
            if (kv.Eq("impassable", "yes") || access == "false" ||
                (kv.Eq("access", "private") && (kv.Eq("emergency", "yes") || kv.Eq("service", "emergency_access"))))
            {
                kv.SetBool("auto_forward", false);
                kv.SetBool("truck_forward", false);
                kv.SetBool("bus_forward", false);
                kv.SetBool("taxi_forward", false);
                kv.SetBool("moped_forward", false);
                kv.SetBool("motorcycle_forward", false);
                kv.SetBool("pedestrian_forward", false);
                kv.SetBool("bike_forward", false);

                kv.SetBool("auto_backward", false);
                kv.SetBool("truck_backward", false);
                kv.SetBool("bus_backward", false);
                kv.SetBool("taxi_backward", false);
                kv.SetBool("moped_backward", false);
                kv.SetBool("motorcycle_backward", false);
                kv.SetBool("pedestrian_backward", false);
                kv.SetBool("bike_backward", false);
            }
            else if (kv.Eq("smoothness", "impassable"))
            {
                kv.SetBool("auto_forward", false);
                kv.SetBool("truck_forward", false);
                kv.SetBool("bus_forward", false);
                kv.SetBool("taxi_forward", false);
                kv.SetBool("moped_forward", false);
                kv.SetBool("motorcycle_forward", false);
                kv.SetBool("bike_forward", false);

                kv.SetBool("auto_backward", false);
                kv.SetBool("truck_backward", false);
                kv.SetBool("bus_backward", false);
                kv.SetBool("taxi_backward", false);
                kv.SetBool("moped_backward", false);
                kv.SetBool("motorcycle_backward", false);
                kv.SetBool("bike_backward", false);
            }

            // auto_forward overrides.
            kv.SetString("auto_tag", Or(LuaTagTables.AnyIn(LuaTagTables.MotorVehicle, kv.GetString("motorcar")), motorVehicleAccess));
            kv.SetString("auto_forward", Or(kv.GetString("auto_tag"), kv.GetString("auto_forward")));

            // truck_forward override.
            kv.SetString("truck_tag", Or(LuaTagTables.AnyIn(LuaTagTables.Truck, kv.GetString("hgv")), motorVehicleAccess));
            kv.SetString("truck_forward", Or(kv.GetString("truck_tag"), kv.GetString("truck_forward")));

            // bus_forward overrides.
            kv.SetString("bus_tag", Or(
                LuaTagTables.AnyIn(LuaTagTables.Bus, kv.GetString("bus")),
                LuaTagTables.AnyIn(LuaTagTables.Psv, kv.GetString("psv")),
                Or(PsvLookup(kv.GetString("lanes:psv:forward")), motorVehicleAccess)));
            kv.SetString("bus_forward", Or(kv.GetString("bus_tag"), kv.GetString("bus_forward")));

            // taxi_forward overrides.
            kv.SetString("taxi_tag", Or(
                LuaTagTables.AnyIn(LuaTagTables.Taxi, kv.GetString("taxi")),
                LuaTagTables.AnyIn(LuaTagTables.Psv, kv.GetString("psv")),
                Or(PsvLookup(kv.GetString("lanes:psv:forward")), motorVehicleAccess)));
            kv.SetString("taxi_forward", Or(kv.GetString("taxi_tag"), kv.GetString("taxi_forward")));

            // ped overrides.
            kv.SetString("foot_tag", Or(LuaTagTables.AnyIn(LuaTagTables.Foot, kv.GetString("foot")), FootLookup(kv.GetString("pedestrian"))));
            kv.SetString("pedestrian_forward", Or(kv.GetString("foot_tag"), kv.GetString("pedestrian_forward")));

            // bike_forward overrides.
            kv.SetString("bike_tag", Or(
                LuaTagTables.AnyIn(LuaTagTables.Bicycle, kv.GetString("bicycle")),
                LuaTagTables.AnyIn(LuaTagTables.Cycleway, kv.GetString("cycleway")),
                Or(LuaTagTables.AnyIn(LuaTagTables.Bicycle, kv.GetString("bicycle_road")),
                   Or(BicycleLookup(kv.GetString("cyclestreet")), vehicleAccess))));
            kv.SetString("bike_forward", Or(kv.GetString("bike_tag"), kv.GetString("bike_forward")));

            // moped forward overrides.
            kv.SetString("moped_tag", Or(
                LuaTagTables.AnyIn(LuaTagTables.Moped, kv.GetString("moped")),
                Or(LuaTagTables.AnyIn(LuaTagTables.Moped, kv.GetString("mofa")), motorVehicleAccess)));
            kv.SetString("moped_forward", Or(kv.GetString("moped_tag"), kv.GetString("moped_forward")));

            // motorcycle forward overrides.
            kv.SetString("motorcycle_tag", Or(LuaTagTables.AnyIn(LuaTagTables.MotorVehicle, kv.GetString("motorcycle")), motorVehicleAccess));
            kv.SetString("motorcycle_forward", Or(kv.GetString("motorcycle_tag"), kv.GetString("motorcycle_forward")));

            if (kv.Eq("access", "psv"))
            {
                kv.SetBool("taxi_forward", true);
                kv.SetBool("taxi_tag", true);
                kv.SetBool("bus_forward", true);
                kv.SetBool("bus_tag", true);
            }

            if (kv.Eq("motorroad", "yes"))
            {
                kv.SetBool("motorroad_tag", true);
            }
        }
        else if (ferry || rail)
        {
            string defaultVal = "true";

            if (kv.Eq("impassable", "yes") || access == "false" ||
                (kv.Eq("access", "private") && (kv.Eq("emergency", "yes") || kv.Eq("service", "emergency_access"))))
            {
                defaultVal = "false";
            }

            string pedVal = defaultVal;
            if (kv.Eq("smoothness", "impassable"))
            {
                defaultVal = "false";
            }

            kv.SetString("auto_tag", Or(LuaTagTables.AnyIn(LuaTagTables.MotorVehicle, kv.GetString("motorcar")), motorVehicleAccess));
            kv.SetString("auto_forward", Or(kv.GetString("auto_tag"), defaultVal));

            kv.SetString("truck_tag", Or(LuaTagTables.AnyIn(LuaTagTables.Truck, kv.GetString("hgv")), motorVehicleAccess));
            kv.SetString("truck_forward", Or(
                LuaTagTables.AnyIn(LuaTagTables.Truck, kv.GetString("hgv")),
                Or(kv.GetString("truck_forward"), Or(motorVehicleAccess, defaultVal))));

            kv.SetString("bus_tag", Or(
                LuaTagTables.AnyIn(LuaTagTables.Bus, kv.GetString("bus")),
                LuaTagTables.AnyIn(LuaTagTables.Psv, kv.GetString("psv")),
                Or(PsvLookup(kv.GetString("lanes:psv:forward")), motorVehicleAccess)));
            kv.SetString("bus_forward", Or(kv.GetString("bus_tag"), defaultVal));

            kv.SetString("taxi_tag", Or(
                LuaTagTables.AnyIn(LuaTagTables.Taxi, kv.GetString("taxi")),
                LuaTagTables.AnyIn(LuaTagTables.Psv, kv.GetString("psv")),
                Or(PsvLookup(kv.GetString("lanes:psv:forward")), motorVehicleAccess)));
            kv.SetString("taxi_forward", Or(kv.GetString("taxi_tag"), defaultVal));

            kv.SetString("foot_tag", Or(LuaTagTables.AnyIn(LuaTagTables.Foot, kv.GetString("foot")), FootLookup(kv.GetString("pedestrian"))));
            kv.SetString("pedestrian_forward", Or(kv.GetString("foot_tag"), pedVal));

            kv.SetString("bike_tag", Or(
                LuaTagTables.AnyIn(LuaTagTables.Bicycle, kv.GetString("bicycle")),
                LuaTagTables.AnyIn(LuaTagTables.Cycleway, kv.GetString("cycleway")),
                Or(LuaTagTables.AnyIn(LuaTagTables.Bicycle, kv.GetString("bicycle_road")),
                   Or(BicycleLookup(kv.GetString("cyclestreet")), vehicleAccess))));
            kv.SetString("bike_forward", Or(kv.GetString("bike_tag"), defaultVal));

            kv.SetString("moped_tag", Or(
                LuaTagTables.AnyIn(LuaTagTables.Moped, kv.GetString("moped")),
                Or(LuaTagTables.AnyIn(LuaTagTables.Moped, kv.GetString("mofa")), motorVehicleAccess)));
            kv.SetString("moped_forward", Or(kv.GetString("moped_tag"), defaultVal));

            kv.SetString("motorcycle_tag", Or(LuaTagTables.AnyIn(LuaTagTables.MotorVehicle, kv.GetString("motorcycle")), motorVehicleAccess));
            kv.SetString("motorcycle_forward", Or(kv.GetString("motorcycle_tag"), defaultVal));

            if (kv.GetString("bike_tag") == null)
            {
                if (kv.Eq("sac_scale", "hiking"))
                {
                    kv.SetBool("bike_forward", true);
                    kv.SetBool("bike_tag", true);
                }
                else if (kv.Has("sac_scale"))
                {
                    kv.SetBool("bike_forward", false);
                }
            }

            if (kv.Eq("access", "psv"))
            {
                kv.SetBool("taxi_forward", true);
                kv.SetBool("taxi_tag", true);
                kv.SetBool("bus_forward", true);
                kv.SetBool("bus_tag", true);
            }

            if (kv.Eq("motorroad", "yes"))
            {
                kv.SetBool("motorroad_tag", true);
            }
        }
        else
        {
            // something we have no idea about.
            kv.SetBool("auto_forward", false);
            kv.SetBool("truck_forward", false);
            kv.SetBool("bus_forward", false);
            kv.SetBool("taxi_forward", false);
            kv.SetBool("moped_forward", false);
            kv.SetBool("motorcycle_forward", false);
            kv.SetBool("pedestrian_forward", false);
            kv.SetBool("bike_forward", false);

            kv.SetBool("auto_backward", false);
            kv.SetBool("truck_backward", false);
            kv.SetBool("bus_backward", false);
            kv.SetBool("taxi_backward", false);
            kv.SetBool("moped_backward", false);
            kv.SetBool("motorcycle_backward", false);
            kv.SetBool("pedestrian_backward", false);
            kv.SetBool("bike_backward", false);
        }

        // permissive/hov/taxi + oneway=reversible.
        if ((kv.Eq("access", "permissive") || kv.Eq("access", "hov") || kv.Eq("access", "taxi")) &&
            kv.Eq("oneway", "reversible"))
        {
            if (kv.Eq("bus_forward", "true"))
            {
                kv.SetBool("auto_forward", false);
                kv.SetBool("truck_forward", false);
                kv.SetBool("pedestrian_forward", false);
                kv.SetBool("bike_forward", false);
                kv.SetBool("moped_forward", false);
                kv.SetBool("motorcycle_forward", false);
            }
            else
            {
                return 1;
            }
        }

        // service=driveway means all are routable.
        if (kv.Eq("service", "driveway") && !kv.Has("access"))
        {
            kv.SetBool("auto_forward", true);
            kv.SetBool("truck_forward", true);
            kv.SetBool("bus_forward", true);
            kv.SetBool("taxi_forward", true);
            kv.SetBool("pedestrian_forward", true);
            kv.SetBool("bike_forward", true);
            kv.SetBool("moped_forward", true);
            kv.SetBool("motorcycle_forward", true);
        }

        // oneway-ness / backward traversability per mode.
        string? onewayBike = null, onewayBus = null, onewayTaxi = null, onewayMoped = null,
            onewayMotorcycle = null, onewayFoot = null;

        if ((kv.Eq("oneway", "yes") && kv.Eq("oneway:bicycle", "no")) ||
            kv.Eq("bicycle:backward", "yes") || kv.Eq("bicycle:backward", "no"))
        {
            kv.SetBool("bike_backward", true);
        }

        if (kv.GetString("bike_backward") == null || kv.Eq("bike_backward", "false"))
        {
            kv.SetString("bike_backward", Or(
                LuaTagTables.BikeReverse.GetValueOrDefault(kv.GetString("cycleway") ?? "\0"),
                Or(LuaTagTables.BikeReverse.GetValueOrDefault(kv.GetString("cycleway:left") ?? "\0"),
                   Or(LuaTagTables.BikeReverse.GetValueOrDefault(kv.GetString("cycleway:right") ?? "\0"), "false"))));
        }

        if (kv.Eq("bike_backward", "true"))
        {
            onewayBike = OnewayLookup(kv.GetString("oneway:bicycle"));
        }

        if (kv.GetString("oneway:bus") == null && kv.GetString("oneway:psv") != null)
        {
            kv.SetString("oneway:bus", kv.GetString("oneway:psv"));
        }

        if ((kv.Eq("oneway", "yes") && kv.Eq("oneway:bus", "no")) ||
            kv.Eq("bus:backward", "yes") || kv.Eq("bus:backward", "designated"))
        {
            kv.SetBool("bus_backward", true);
        }

        if (kv.GetString("bus_backward") == null || kv.Eq("bus_backward", "false"))
        {
            kv.SetString("bus_backward", Or(
                LuaTagTables.BusReverse.GetValueOrDefault(kv.GetString("busway") ?? "\0"),
                Or(LuaTagTables.BusReverse.GetValueOrDefault(kv.GetString("busway:left") ?? "\0"),
                   Or(LuaTagTables.BusReverse.GetValueOrDefault(kv.GetString("busway:right") ?? "\0"),
                      Or(PsvLookup(kv.GetString("lanes:psv:backward")), "false")))));
        }

        if (kv.Eq("bus_backward", "true"))
        {
            onewayBus = OnewayLookup(kv.GetString("oneway:bus"));
            if (onewayBus == "false" && kv.Eq("bus:backward", "yes"))
            {
                onewayBus = "true";
            }
        }

        if (kv.GetString("oneway:taxi") == null && kv.GetString("oneway:psv") != null)
        {
            kv.SetString("oneway:taxi", kv.GetString("oneway:psv"));
        }

        if ((kv.Eq("oneway", "yes") && kv.Eq("oneway:taxi", "no")) ||
            kv.Eq("taxi:backward", "yes") || kv.Eq("taxi:backward", "designated"))
        {
            kv.SetBool("taxi_backward", true);
        }

        if (kv.GetString("taxi_backward") == null || kv.Eq("taxi_backward", "false"))
        {
            kv.SetString("taxi_backward", Or(PsvLookup(kv.GetString("lanes:psv:backward")), "false"));
        }

        if (kv.Eq("taxi_backward", "true"))
        {
            onewayTaxi = OnewayLookup(kv.GetString("oneway:taxi"));
            if (onewayTaxi == "false" && kv.Eq("taxi:backward", "yes"))
            {
                onewayTaxi = "true";
            }
        }

        if (kv.GetString("moped_backward") == null)
        {
            kv.SetBool("moped_backward", false);
        }

        if ((kv.Eq("oneway", "yes") && (kv.Eq("oneway:moped", "no") || kv.Eq("oneway:mofa", "no"))) ||
            kv.Eq("moped:backward", "yes") || kv.Eq("mofa:backward", "yes"))
        {
            kv.SetBool("moped_backward", true);
        }

        if (kv.Eq("moped_backward", "true"))
        {
            onewayMoped = Or(OnewayLookup(kv.GetString("oneway:moped")), OnewayLookup(kv.GetString("oneway:mofa")));
        }

        if (kv.GetString("motorcycle_backward") == null)
        {
            kv.SetBool("motorcycle_backward", false);
        }

        if ((kv.Eq("oneway", "yes") && kv.Eq("oneway:motorcycle", "no")) || kv.Eq("motorcycle:backward", "yes"))
        {
            kv.SetBool("motorcycle_backward", true);
        }

        if (kv.Eq("motorcycle_backward", "true"))
        {
            onewayMotorcycle = OnewayLookup(kv.GetString("oneway:motorcycle"));
        }

        if (kv.GetString("pedestrian_backward") == null)
        {
            kv.SetBool("pedestrian_backward", false);
        }

        if ((kv.Eq("oneway", "yes") && kv.Eq("oneway:foot", "no")) || kv.Eq("foot:backward", "yes"))
        {
            kv.SetBool("pedestrian_backward", true);
        }

        if (kv.Eq("pedestrian_backward", "true"))
        {
            onewayFoot = OnewayLookup(kv.GetString("oneway:foot"));
        }

        string? onewayReverse = kv.GetString("oneway");
        string? onewayNorm = OnewayLookup(kv.GetString("oneway"));
        if (kv.Eq("junction", "roundabout") || kv.Eq("junction", "circular"))
        {
            onewayNorm = "true";
            kv.SetBool("roundabout", true);
        }
        else
        {
            kv.SetBool("roundabout", false);
        }

        if (kv.Eq("junction", "intersection"))
        {
            kv.SetBool("tagged_internal_intersection", true);
        }

        kv.SetString("oneway", onewayNorm);

        if (onewayNorm == "true")
        {
            kv.SetBool("auto_backward", false);
            kv.SetBool("truck_backward", false);
            kv.SetBool("emergency_backward", false);

            if (kv.Eq("bike_backward", "true"))
            {
                if (onewayBike == "true")
                {
                    kv.SetBool("bike_forward", false);
                }
                else if (onewayBike == "false")
                {
                    kv.SetBool("bike_forward", true);
                }
            }

            if (kv.Eq("bus_backward", "true"))
            {
                if (onewayBus == "true")
                {
                    kv.SetBool("bus_forward", false);
                }
                else if (onewayBus == "false")
                {
                    kv.SetBool("bus_forward", true);
                }
            }

            if (kv.Eq("taxi_backward", "true"))
            {
                if (onewayTaxi == "true")
                {
                    kv.SetBool("taxi_forward", false);
                }
                else if (onewayTaxi == "false")
                {
                    kv.SetBool("taxi_forward", true);
                }
            }

            if (kv.Eq("moped_backward", "true"))
            {
                if (onewayMoped == "true")
                {
                    kv.SetBool("moped_forward", false);
                }
                else if (onewayMoped == "false")
                {
                    kv.SetBool("moped_forward", true);
                }
            }

            if (kv.Eq("motorcycle_backward", "true"))
            {
                if (onewayMotorcycle == "true")
                {
                    kv.SetBool("motorcycle_forward", false);
                }
                else if (onewayMotorcycle == "false")
                {
                    kv.SetBool("motorcycle_forward", true);
                }
            }

            if (kv.Eq("highway", "footway") || kv.Eq("highway", "pedestrian") || kv.Eq("highway", "steps") ||
                kv.Eq("highway", "path") || kv.Has("oneway:foot"))
            {
                if (kv.Eq("pedestrian_backward", "true"))
                {
                    if (onewayFoot == "true")
                    {
                        kv.SetBool("pedestrian_forward", false);
                    }
                    else if (onewayFoot == "false")
                    {
                        kv.SetBool("pedestrian_forward", true);
                    }
                }
            }
            else
            {
                kv.SetString("pedestrian_backward", kv.GetString("pedestrian_forward"));
            }
        }
        else if (onewayNorm == null || onewayNorm == "false")
        {
            kv.SetString("auto_backward", kv.GetString("auto_forward"));
            kv.SetString("truck_backward", kv.GetString("truck_forward"));
            kv.SetString("emergency_backward", kv.GetString("emergency_forward"));

            if (kv.Eq("bike_backward", "false") && !kv.Eq("oneway:bicycle", "-1") &&
                (kv.GetString("oneway:bicycle") == null || OnewayLookup(kv.GetString("oneway:bicycle")) == null ||
                 OnewayLookup(kv.GetString("oneway:bicycle")) == "false" || kv.Eq("oneway:bicycle", "no")))
            {
                kv.SetString("bike_backward", kv.GetString("bike_forward"));
            }

            if (kv.Eq("bus_backward", "false") && !kv.Eq("oneway:bus", "-1") &&
                (kv.GetString("oneway:bus") == null || OnewayLookup(kv.GetString("oneway:bus")) == null ||
                 OnewayLookup(kv.GetString("oneway:bus")) == "false"))
            {
                kv.SetString("bus_backward", kv.GetString("bus_forward"));
            }

            if (kv.Eq("taxi_backward", "false") && !kv.Eq("oneway:taxi", "-1") &&
                (kv.GetString("oneway:taxi") == null || OnewayLookup(kv.GetString("oneway:taxi")) == null ||
                 OnewayLookup(kv.GetString("oneway:taxi")) == "false"))
            {
                kv.SetString("taxi_backward", kv.GetString("taxi_forward"));
            }

            if (kv.Eq("moped_backward", "false") &&
                (kv.GetString("oneway:moped") == null || OnewayLookup(kv.GetString("oneway:moped")) == null ||
                 OnewayLookup(kv.GetString("oneway:moped")) == "false" || kv.Eq("oneway:moped", "no")) &&
                (kv.GetString("oneway:mofa") == null || OnewayLookup(kv.GetString("oneway:mofa")) == null ||
                 OnewayLookup(kv.GetString("oneway:mofa")) == "false" || kv.Eq("oneway:mofa", "no")))
            {
                kv.SetString("moped_backward", kv.GetString("moped_forward"));
            }

            if (kv.Eq("motorcycle_backward", "false") && !kv.Eq("oneway:motorcycle", "-1") &&
                (kv.GetString("oneway:motorcycle") == null || OnewayLookup(kv.GetString("oneway:motorcycle")) == null ||
                 OnewayLookup(kv.GetString("oneway:motorcycle")) == "false" || kv.Eq("oneway:motorcycle", "no")))
            {
                kv.SetString("motorcycle_backward", kv.GetString("motorcycle_forward"));
            }

            if (kv.Eq("pedestrian_backward", "false") &&
                (kv.GetString("oneway:foot") == null || OnewayLookup(kv.GetString("oneway:foot")) == null ||
                 OnewayLookup(kv.GetString("oneway:foot")) == "false" || kv.Eq("oneway:foot", "no")))
            {
                kv.SetString("pedestrian_backward", kv.GetString("pedestrian_forward"));
            }
        }

        // Bike forward/backward overrides.
        if (CycleLaneAny(kv.GetString("cycleway:both")) ||
            (CycleLaneAny(kv.GetString("cycleway:right")) && CycleLaneAny(kv.GetString("cycleway:left"))))
        {
            kv.SetBool("bike_forward", true);
            kv.SetBool("bike_backward", true);
        }

        if (kv.Eq("busway", "lane") || (kv.Eq("busway:left", "lane") && kv.Eq("busway:right", "lane")))
        {
            kv.SetBool("bus_forward", true);
            kv.SetBool("bus_backward", true);
        }

        // :forward overrides.
        string? mvForward = Or(kv.GetString("motor_vehicle:forward"), kv.GetString("vehicle:forward"));
        if (mvForward != null)
        {
            string? accessForward = LuaTagTables.AnyIn(LuaTagTables.MotorVehicle, mvForward);
            kv.SetString("auto_forward", accessForward);
            kv.SetString("truck_forward", accessForward);
            kv.SetString("bus_forward", accessForward);
            kv.SetString("taxi_forward", accessForward);
            kv.SetString("moped_forward", accessForward);
            kv.SetString("motorcycle_forward", accessForward);
        }

        if (kv.GetString("foot:forward") != null)
        {
            kv.SetString("pedestrian_forward", LuaTagTables.AnyIn(LuaTagTables.Foot, kv.GetString("foot:forward")));
        }

        string? bkForward = Or(kv.GetString("bicycle:forward"), kv.GetString("vehicle:forward"));
        if (bkForward != null)
        {
            kv.SetString("bike_forward", LuaTagTables.AnyIn(LuaTagTables.Bicycle, bkForward));
        }

        // :backward overrides.
        string? mvBackward = Or(kv.GetString("motor_vehicle:backward"), kv.GetString("vehicle:backward"));
        if (mvBackward != null)
        {
            string? accessBackward = LuaTagTables.AnyIn(LuaTagTables.MotorVehicle, mvBackward);
            kv.SetString("auto_backward", accessBackward);
            kv.SetString("truck_backward", accessBackward);
            kv.SetString("bus_backward", accessBackward);
            kv.SetString("taxi_backward", accessBackward);
            kv.SetString("moped_backward", accessBackward);
            kv.SetString("motorcycle_backward", accessBackward);
        }

        if (kv.GetString("foot:backward") != null)
        {
            kv.SetString("pedestrian_backward", LuaTagTables.AnyIn(LuaTagTables.Foot, kv.GetString("foot:backward")));
        }

        string? bkBackward = Or(kv.GetString("bicycle:backward"), kv.GetString("vehicle:backward"));
        if (bkBackward != null)
        {
            kv.SetString("bike_backward", LuaTagTables.AnyIn(LuaTagTables.Bicycle, bkBackward));
        }

        kv.SetBool("oneway_reverse", false);

        // flip the onewayness.
        if (onewayReverse == "-1")
        {
            kv.SetBool("oneway_reverse", true);
            SwapTags(kv, "auto_forward", "auto_backward");
            SwapTags(kv, "truck_forward", "truck_backward");
            SwapTags(kv, "emergency_forward", "emergency_backward");
            SwapTags(kv, "bus_forward", "bus_backward");
            SwapTags(kv, "taxi_forward", "taxi_backward");
            SwapTags(kv, "bike_forward", "bike_backward");
            SwapTags(kv, "moped_forward", "moped_backward");
            SwapTags(kv, "motorcycle_forward", "motorcycle_backward");
            SwapTags(kv, "pedestrian_forward", "pedestrian_backward");
        }

        if (kv.Eq("oneway:bicycle", "-1"))
        {
            SwapTags(kv, "bike_forward", "bike_backward");
        }

        if (kv.Eq("oneway:moped", "-1") || kv.Eq("oneway:mofa", "-1"))
        {
            SwapTags(kv, "moped_forward", "moped_backward");
        }

        if (kv.Eq("oneway:motorcycle", "-1"))
        {
            SwapTags(kv, "motorcycle_forward", "motorcycle_backward");
        }

        if (kv.Eq("oneway:foot", "-1"))
        {
            SwapTags(kv, "pedestrian_forward", "pedestrian_backward");
        }

        if (kv.Eq("oneway:bus", "-1"))
        {
            SwapTags(kv, "bus_forward", "bus_backward");
        }

        // bus only logic.
        if (kv.Eq("lanes:bus", "1"))
        {
            kv.SetBool("bus_forward", true);
            kv.SetBool("bus_backward", false);
        }
        else if (kv.Eq("lanes:bus", "2"))
        {
            kv.SetBool("bus_forward", true);
            kv.SetBool("bus_backward", true);
        }

        if (kv.Eq("oneway:taxi", "-1"))
        {
            SwapTags(kv, "taxi_forward", "taxi_backward");
        }

        if (kv.Eq("lanes:psv", "1"))
        {
            kv.SetBool("taxi_forward", true);
            kv.SetBool("taxi_backward", false);
        }
        else if (kv.Eq("lanes:psv", "2"))
        {
            kv.SetBool("taxi_forward", true);
            kv.SetBool("taxi_backward", true);
        }

        // if none of the modes were set we are done looking at this.
        if (kv.Eq("auto_forward", "false") && kv.Eq("truck_forward", "false") && kv.Eq("bus_forward", "false") &&
            kv.Eq("bike_forward", "false") && kv.Eq("emergency_forward", "false") && kv.Eq("moped_forward", "false") &&
            kv.Eq("motorcycle_forward", "false") && kv.Eq("pedestrian_forward", "false") &&
            kv.Eq("auto_backward", "false") && kv.Eq("truck_backward", "false") && kv.Eq("bus_backward", "false") &&
            kv.Eq("bike_backward", "false") && kv.Eq("emergency_backward", "false") && kv.Eq("moped_backward", "false") &&
            kv.Eq("motorcycle_backward", "false") && kv.Eq("pedestrian_backward", "false"))
        {
            if (!kv.Eq("highway", "bridleway"))
            {
                return 1;
            }
        }

        // delete some tags.
        kv.Remove("FIXME");
        kv.Remove("note");
        kv.Remove("source");

        // set a few flags - road class.
        int? rc = LuaTagTables.RoadClass.TryGetValue(kv.GetString("highway") ?? "\0", out int rcv) ? rcv : (int?)null;
        if (kv.Eq("highway", "construction"))
        {
            rc = LuaTagTables.RoadClass.TryGetValue(kv.GetString("construction") ?? "\0", out int crc) ? crc : (int?)null;
        }

        if (!kv.Has("highway") && ferry)
        {
            rc = 2;
        }
        else if (!kv.Has("highway") && (kv.Has("railway") || kv.Eq("route", "shuttle_train")))
        {
            rc = 2;
        }
        else if (rc == null)
        {
            rc = 7;
        }

        kv.Set("road_class", LuaValue.Number(rc.Value));

        kv.Set("default_speed", LuaValue.Number(LuaTagTables.DefaultSpeed[rc.Value]));

        if (kv.Eq("service", "driveway"))
        {
            double ds = LuaTagTables.TryParseLua(kv.GetString("default_speed"), out double d) ? d : 0;
            kv.Set("default_speed", LuaValue.Number(System.Math.Floor(ds * 0.5)));
        }

        kv.SetString("lit", LitLookup(kv.GetString("lit")));

        int? use = LuaTagTables.Use.TryGetValue(kv.GetString("service") ?? "\0", out int uv) ? uv : (int?)null;

        if (kv.Has("highway"))
        {
            if (kv.Eq("highway", "construction"))
            {
                use = 43;
            }
            else if (kv.Eq("highway", "track"))
            {
                use = 3;
            }
            else if (kv.Eq("highway", "living_street"))
            {
                use = 10;
            }
            else if (use == null && kv.Eq("highway", "service"))
            {
                use = 11;
            }
            else if (kv.Eq("highway", "cycleway"))
            {
                use = 20;
            }
            else if (kv.Eq("pedestrian_forward", "false") && kv.Eq("auto_forward", "false") &&
                     kv.Eq("auto_backward", "false") && (kv.Eq("bike_forward", "true") || kv.Eq("bike_backward", "true")))
            {
                use = 20;
            }
            else if (kv.Eq("highway", "footway") && kv.Eq("footway", "sidewalk"))
            {
                use = 24;
            }
            else if (kv.Eq("highway", "footway") && kv.Eq("footway", "crossing"))
            {
                use = 32;
            }
            else if (kv.Eq("highway", "footway"))
            {
                use = 25;
            }
            else if (kv.Eq("highway", "elevator"))
            {
                use = 33;
            }
            else if (kv.Eq("highway", "steps") && kv.Has("conveying"))
            {
                use = 34;
            }
            else if (kv.Eq("highway", "steps"))
            {
                use = 26;
            }
            else if (kv.Eq("highway", "path"))
            {
                use = 27;
            }
            else if (kv.Eq("highway", "pedestrian"))
            {
                use = 28;
            }
            else if (kv.Eq("highway", "platform"))
            {
                use = 35;
            }
            else if (kv.Eq("pedestrian_forward", "true") &&
                     kv.Eq("auto_forward", "false") && kv.Eq("auto_backward", "false") &&
                     kv.Eq("truck_forward", "false") && kv.Eq("truck_backward", "false") &&
                     kv.Eq("bus_forward", "false") && kv.Eq("bus_backward", "false") &&
                     kv.Eq("bike_forward", "false") && kv.Eq("bike_backward", "false") &&
                     kv.Eq("moped_forward", "false") && kv.Eq("moped_backward", "false") &&
                     kv.Eq("motorcycle_forward", "false") && kv.Eq("motorcycle_backward", "false"))
            {
                use = 28;
            }
            else if (kv.Eq("highway", "bridleway"))
            {
                use = 29;
            }
        }

        if (use == null && kv.Has("service"))
        {
            use = 40;
        }
        else if (use == null)
        {
            use = 0;
        }

        if (use != 43 && (kv.Eq("access", "emergency") || kv.Eq("emergency", "yes")) &&
            kv.Eq("auto_forward", "false") && kv.Eq("auto_backward", "false") &&
            kv.Eq("truck_forward", "false") && kv.Eq("truck_backward", "false") &&
            kv.Eq("bus_forward", "false") && kv.Eq("bus_backward", "false") &&
            kv.Eq("bike_forward", "false") && kv.Eq("bike_backward", "false") &&
            kv.Eq("moped_forward", "false") && kv.Eq("moped_backward", "false") &&
            kv.Eq("motorcycle_forward", "false") && kv.Eq("motorcycle_backward", "false"))
        {
            use = 7;
        }

        kv.Set("use", LuaValue.Number(use.Value));

        // shoulders.
        string? rShoulder = Or(ShoulderLookup(kv.GetString("shoulder")), ShoulderLookup(kv.GetString("shoulder:both")));
        string? lShoulder = rShoulder;

        if (rShoulder == null)
        {
            rShoulder = Or(ShoulderLookup(kv.GetString("shoulder:right")), Or(ShoulderRightLookup(kv.GetString("shoulder")), "false"));
            lShoulder = Or(ShoulderLookup(kv.GetString("shoulder:left")), Or(ShoulderLeftLookup(kv.GetString("shoulder")), "false"));

            if (onewayNorm == "true" && rShoulder == "true" && lShoulder == "false")
            {
                lShoulder = "true";
            }
            else if (onewayNorm == "true" && rShoulder == "false" && lShoulder == "true")
            {
                rShoulder = "true";
            }
        }

        kv.SetString("shoulder_right", rShoulder);
        kv.SetString("shoulder_left", lShoulder);

        // cycle lanes.
        string cycleLaneRightOpposite = "false";
        string cycleLaneLeftOpposite = "false";
        int cycleLaneRight = 0;
        int cycleLaneLeft = 0;

        if ((use == 20 || use == 25 || use == 27) && (kv.Eq("bike_forward", "true") || kv.Eq("bike_backward", "true")))
        {
            if (kv.Eq("pedestrian_forward", "false"))
            {
                cycleLaneRight = 3;
            }
            else if (kv.Eq("segregated", "yes"))
            {
                cycleLaneRight = 2;
            }
            else if (kv.Eq("segregated", "no"))
            {
                cycleLaneRight = 1;
            }
            else if (use == 20)
            {
                cycleLaneRight = 2;
            }
            else
            {
                cycleLaneRight = 1;
            }

            cycleLaneLeft = cycleLaneRight;
        }
        else
        {
            cycleLaneRightOpposite = Or(BikeReverseLookup(kv.GetString("cycleway")), "false")!;
            cycleLaneLeftOpposite = cycleLaneRightOpposite;

            if (cycleLaneRightOpposite == "false")
            {
                cycleLaneRightOpposite = Or(BikeReverseLookup(kv.GetString("cycleway:right")), "false")!;
                cycleLaneLeftOpposite = Or(BikeReverseLookup(kv.GetString("cycleway:left")), "false")!;
            }

            cycleLaneRight = CycleLaneVal(kv.GetString("cycleway")) ??
                             BufferLookup(kv.GetString("cycleway:both:buffer")) ?? 0;
            cycleLaneLeft = cycleLaneRight;

            if (cycleLaneRight == 0)
            {
                cycleLaneRight = CycleLaneVal(kv.GetString("cycleway:right")) ??
                                 BufferLookup(kv.GetString("cycleway:right:buffer")) ?? 0;
                cycleLaneLeft = CycleLaneVal(kv.GetString("cycleway:left")) ??
                                BufferLookup(kv.GetString("cycleway:left:buffer")) ?? 0;
            }

            if (kv.Eq("oneway:bicycle", "no") && cycleLaneRightOpposite == "false" && cycleLaneLeftOpposite == "false")
            {
                if (cycleLaneRight == 2 || cycleLaneRight == 3)
                {
                    if (onewayNorm == "true")
                    {
                        cycleLaneLeft = cycleLaneRight;
                        cycleLaneLeftOpposite = "true";
                    }
                    else if (cycleLaneLeft == 0)
                    {
                        cycleLaneLeft = cycleLaneRight;
                    }
                }
                else if (cycleLaneLeft == 2 || cycleLaneLeft == 3)
                {
                    if (onewayNorm == "true")
                    {
                        cycleLaneRight = cycleLaneLeft;
                        cycleLaneRightOpposite = "true";
                    }
                    else if (cycleLaneRight == 0)
                    {
                        cycleLaneRight = cycleLaneLeft;
                    }
                }
            }
        }

        kv.Set("cycle_lane_right", LuaValue.Number(cycleLaneRight));
        kv.Set("cycle_lane_left", LuaValue.Number(cycleLaneLeft));
        kv.SetString("cycle_lane_right_opposite", cycleLaneRightOpposite);
        kv.SetString("cycle_lane_left_opposite", cycleLaneLeftOpposite);

        // link.
        string? highwayType = kv.GetString("highway");
        if (kv.Eq("highway", "construction"))
        {
            highwayType = kv.GetString("construction");
        }

        if (highwayType != null && highwayType.Contains("_link"))
        {
            kv.SetBool("link", true);
            kv.SetString("link_type", kv.GetString("link_type"));
        }

        if (kv.Eq("highway", "via_ferrata") && !kv.Has("sac_scale"))
        {
            kv.SetString("sac_scale", "difficult_alpine_hiking");
        }

        kv.SetString("private", Or(
            LuaTagTables.AnyIn(LuaTagTables.Private, kv.GetString("access")),
            LuaTagTables.AnyIn(LuaTagTables.Private, kv.GetString("motor_vehicle")),
            Or(LuaTagTables.AnyIn(LuaTagTables.Private, kv.GetString("motorcar")),
               Or(LuaTagTables.AnyIn(LuaTagTables.Private, kv.GetString("vehicle")), "false"))));
        kv.SetString("private_hgv", Or(LuaTagTables.AnyIn(LuaTagTables.Private, kv.GetString("hgv")), Or(kv.GetString("private"), "false")));
        kv.SetString("no_thru_traffic", Or(LuaTagTables.AnyIn(LuaTagTables.NoThruTraffic, kv.GetString("access")), "false"));
        kv.SetString("ferry", ferry ? "true" : "false");
        kv.SetString("rail", (kv.Eq("auto_forward", "true") && (kv.Eq("railway", "rail") || kv.Eq("route", "shuttle_train"))) ? "true" : "false");

        // names pass through.
        kv.SetString("name", kv.GetString("name"));
        kv.SetString("name:en", kv.GetString("name:en"));
        kv.SetString("alt_name", kv.GetString("alt_name"));
        kv.SetString("official_name", kv.GetString("official_name"));

        if (kv.Eq("maxspeed", "none"))
        {
            kv.SetString("max_speed", "unlimited");
        }
        else
        {
            kv.SetString("max_speed", FmtNum(LuaTagTables.NormalizeSpeed(kv.GetString("maxspeed"))));
        }

        kv.SetString("advisory_speed", FmtNum(LuaTagTables.NormalizeSpeed(kv.GetString("maxspeed:advisory"))));
        kv.SetString("average_speed", FmtNum(LuaTagTables.NormalizeSpeed(kv.GetString("maxspeed:practical"))));
        kv.SetString("backward_speed", FmtNum(LuaTagTables.NormalizeSpeed(kv.GetString("maxspeed:backward"))));
        kv.SetString("forward_speed", FmtNum(LuaTagTables.NormalizeSpeed(kv.GetString("maxspeed:forward"))));
        kv.SetString("int", kv.GetString("int"));
        kv.SetString("int_ref", kv.GetString("int_ref"));
        kv.SetString("surface", kv.GetString("surface"));
        kv.SetString("wheelchair", LuaTagTables.AnyIn(LuaTagTables.Wheelchair, kv.GetString("wheelchair")));

        // lower the default speed for tracks.
        if (kv.Eq("highway", "track"))
        {
            kv.Set("default_speed", LuaValue.Number(5));
            if (kv.Has("tracktype"))
            {
                if (kv.Eq("tracktype", "grade1"))
                {
                    kv.Set("default_speed", LuaValue.Number(20));
                }
                else if (kv.Eq("tracktype", "grade2"))
                {
                    kv.Set("default_speed", LuaValue.Number(15));
                }
                else if (kv.Eq("tracktype", "grade3"))
                {
                    kv.Set("default_speed", LuaValue.Number(12));
                }
                else if (kv.Eq("tracktype", "grade4"))
                {
                    kv.Set("default_speed", LuaValue.Number(10));
                }
            }
        }

        // unsigned_ref.
        if (kv.GetString("name") == null && kv.GetString("name:en") == null && kv.GetString("alt_name") == null &&
            kv.GetString("official_name") == null && kv.GetString("ref") == null && kv.GetString("int_ref") == null &&
            (kv.Eq("highway", "motorway") || kv.Eq("highway", "trunk") || kv.Eq("highway", "primary")) &&
            kv.GetString("unsigned_ref") != null)
        {
            kv.SetString("ref", kv.GetString("unsigned_ref"));
        }

        // lanes.
        SetLaneCount(kv, "lanes", "lanes");
        SetLaneCount(kv, "forward_lanes", "lanes:forward");
        SetLaneCount(kv, "backward_lanes", "lanes:backward");

        kv.SetString("bridge", Or(BridgeLookup(kv.GetString("bridge")), "false"));

        // hov.
        kv.SetBool("hov_tag", true);
        if (kv.Has("hov") && kv.Eq("hov", "no"))
        {
            kv.SetBool("hov_forward", false);
            kv.SetBool("hov_backward", false);
        }
        else
        {
            kv.SetString("hov_forward", kv.GetString("auto_forward"));
            kv.SetString("hov_backward", kv.GetString("auto_backward"));
        }

        if ((kv.Has("hov") && !kv.Eq("hov", "no")) || kv.Has("hov:lanes") || kv.Has("hov:minimum"))
        {
            bool onlyHovAllowed = kv.Eq("hov", "designated");

            if (onlyHovAllowed && kv.Has("hov:lanes"))
            {
                string lanes = kv.GetString("hov:lanes") + "|";
                foreach (string lane in SplitPipe(lanes))
                {
                    if (lane.Length > 0 && lane != "designated")
                    {
                        onlyHovAllowed = false;
                    }
                }
            }

            if (onlyHovAllowed)
            {
                if (kv.Eq("hov:minimum", "2"))
                {
                    kv.SetString("hov_type", "HOV2");
                }
                else if (kv.Eq("hov:minimum", "3"))
                {
                    kv.SetString("hov_type", "HOV3");
                }
                else
                {
                    onlyHovAllowed = false;
                }
            }

            if (onlyHovAllowed)
            {
                bool avoid = kv.Eq("oneway", "alternating") || kv.Eq("oneway", "reversible") ||
                             kv.Eq("oneway", "false") || kv.Has("oneway:conditional") || kv.Has("access:conditional");
                onlyHovAllowed = !avoid;
            }

            if (onlyHovAllowed)
            {
                if (kv.GetString("auto_tag") == null)
                {
                    kv.SetBool("auto_forward", false);
                    kv.SetBool("auto_backward", false);
                }

                if (kv.GetString("truck_tag") == null)
                {
                    kv.SetBool("truck_forward", false);
                    kv.SetBool("truck_backward", false);
                }

                if (kv.GetString("foot_tag") == null)
                {
                    kv.SetBool("pedestrian_forward", false);
                    kv.SetBool("pedestrian_backward", false);
                }

                if (kv.GetString("bike_tag") == null)
                {
                    kv.SetBool("bike_forward", false);
                    kv.SetBool("bike_backward", false);
                }
            }
            else
            {
                kv.SetBool("hov_forward", false);
                kv.SetBool("hov_backward", false);
            }
        }

        kv.SetString("tunnel", Or(TunnelLookup(kv.GetString("tunnel")), "false"));
        kv.SetString("toll", Or(TollLookup(kv.GetString("toll")), "false"));
        kv.SetString("destination", kv.GetString("destination"));
        kv.SetString("destination:forward", kv.GetString("destination:forward"));
        kv.SetString("destination:backward", kv.GetString("destination:backward"));
        kv.SetString("destination:ref", kv.GetString("destination:ref"));
        kv.SetString("destination:ref:to", kv.GetString("destination:ref:to"));
        kv.SetString("destination:street", kv.GetString("destination:street"));
        kv.SetString("destination:street:to", kv.GetString("destination:street:to"));
        kv.SetString("junction:ref", kv.GetString("junction:ref"));
        kv.SetString("turn:lanes", kv.GetString("turn:lanes"));
        kv.SetString("turn:lanes:forward", kv.GetString("turn:lanes:forward"));
        kv.SetString("turn:lanes:backward", kv.GetString("turn:lanes:backward"));

        // truck goodies.
        kv.SetString("maxheight", FmtNum(Or2(LuaTagTables.NormalizeMeasurement(kv.GetString("maxheight")), LuaTagTables.NormalizeMeasurement(kv.GetString("maxheight:physical")))));
        kv.SetString("maxwidth", FmtNum(Or2(LuaTagTables.NormalizeMeasurement(kv.GetString("maxwidth")), LuaTagTables.NormalizeMeasurement(kv.GetString("maxwidth:physical")))));
        kv.SetString("maxlength", FmtNum(LuaTagTables.NormalizeMeasurement(kv.GetString("maxlength"))));
        kv.SetString("maxweight", FmtNum(LuaTagTables.NormalizeWeight(kv.GetString("maxweight"))));
        kv.SetString("maxaxleload", FmtNum(LuaTagTables.NormalizeWeight(kv.GetString("maxaxleload"))));
        kv.SetString("maxaxles", LuaTagTables.TryParseLua(kv.GetString("maxaxles"), out double maxAxles) ? FmtNum(maxAxles) : null);

        kv.SetString("maxheight_forward", FmtNum(LuaTagTables.NormalizeMeasurement(kv.GetString("maxheight:forward"))));
        kv.SetString("maxheight_backward", FmtNum(LuaTagTables.NormalizeMeasurement(kv.GetString("maxheight:backward"))));
        kv.SetString("maxlength_forward", FmtNum(LuaTagTables.NormalizeMeasurement(kv.GetString("maxlength:forward"))));
        kv.SetString("maxlength_backward", FmtNum(LuaTagTables.NormalizeMeasurement(kv.GetString("maxlength:backward"))));
        kv.SetString("maxweight_forward", FmtNum(LuaTagTables.NormalizeWeight(kv.GetString("maxweight:forward"))));
        kv.SetString("maxweight_backward", FmtNum(LuaTagTables.NormalizeWeight(kv.GetString("maxweight:backward"))));
        kv.SetString("maxwidth_forward", FmtNum(LuaTagTables.NormalizeMeasurement(kv.GetString("maxwidth:forward"))));
        kv.SetString("maxwidth_backward", FmtNum(LuaTagTables.NormalizeMeasurement(kv.GetString("maxwidth:backward"))));

        // hazmat.
        kv.SetString("hazmat", HazmatChain(kv, "hazmat", "hazmat:water", "hazmat:A", "hazmat:B", "hazmat:C", "hazmat:D", "hazmat:E"));
        kv.SetString("hazmat_forward", HazmatChain(kv, "hazmat:forward", "hazmat:water:forward", "hazmat:A:forward", "hazmat:B:forward", "hazmat:C:forward", "hazmat:D:forward", "hazmat:E:forward"));
        kv.SetString("hazmat_backward", HazmatChain(kv, "hazmat:backward", "hazmat:water:backward", "hazmat:A:backward", "hazmat:B:backward", "hazmat:C:backward", "hazmat:D:backward", "hazmat:E:backward"));

        kv.SetString("maxspeed:hgv", FmtNum(LuaTagTables.NormalizeSpeed(kv.GetString("maxspeed:hgv"))));
        kv.SetString("maxspeed:hgv:forward", FmtNum(LuaTagTables.NormalizeSpeed(kv.GetString("maxspeed:hgv:forward"))));
        kv.SetString("maxspeed:hgv:backward", FmtNum(LuaTagTables.NormalizeSpeed(kv.GetString("maxspeed:hgv:backward"))));

        // access restriction conditional exemptions (append "~").
        ApplyConditionalRestrictionExemption(kv, "maxweight", true);
        ApplyConditionalRestrictionExemption(kv, "maxheight", true);
        ApplyConditionalRestrictionExemption(kv, "maxlength", true);
        ApplyConditionalRestrictionExemption(kv, "maxwidth", true);
        ApplyConditionalRestrictionExemption(kv, "hazmat", true);
        ApplyConditionalRestrictionExemption(kv, "maxaxles", false);
        ApplyConditionalRestrictionExemption(kv, "maxaxleload", false);

        if (kv.Has("hgv:national_network") || kv.Has("hgv:state_network") ||
            LuaTagTables.AnyIn(LuaTagTables.TruckHgv, kv.GetString("hgv")) != null)
        {
            kv.SetBool("truck_route", true);
        }

        // bike network mask.
        string? nref = kv.GetString("ncn_ref");
        string? rref = kv.GetString("rcn_ref");
        string? lref = kv.GetString("lcn_ref");
        int bikeMask = 0;
        if (nref != null || kv.Eq("ncn", "yes"))
        {
            bikeMask = 1;
        }

        if (rref != null || kv.Eq("rcn", "yes"))
        {
            bikeMask |= 2;
        }

        if (lref != null || kv.Eq("lcn", "yes"))
        {
            bikeMask |= 4;
        }

        if (kv.Eq("mtb", "yes"))
        {
            bikeMask |= 8;
        }

        kv.SetString("bike_national_ref", nref);
        kv.SetString("bike_regional_ref", rref);
        kv.SetString("bike_local_ref", lref);
        kv.Set("bike_network_mask", LuaValue.Number(bikeMask));

        // construction shut-off (backward compat).
        if (kv.Eq("highway", "construction"))
        {
            kv.SetBool("auto_forward", false);
            kv.SetBool("auto_backward", false);
            kv.SetBool("truck_forward", false);
            kv.SetBool("truck_backward", false);
            kv.SetBool("bus_forward", false);
            kv.SetBool("bus_backward", false);
            kv.SetBool("taxi_forward", false);
            kv.SetBool("taxi_backward", false);
            kv.SetBool("hov_forward", false);
            kv.SetBool("hov_backward", false);
            kv.SetBool("pedestrian_forward", false);
            kv.SetBool("pedestrian_backward", false);
            kv.SetBool("bike_forward", false);
            kv.SetBool("bike_backward", false);
            kv.SetBool("moped_forward", false);
            kv.SetBool("moped_backward", false);
            kv.SetBool("motorcycle_forward", false);
            kv.SetBool("motorcycle_backward", false);
            kv.SetBool("emergency_forward", false);
            kv.SetBool("emergency_backward", false);
        }

        return 0;
    }

    // ---- helpers --------------------------------------------------------------

    private static void SwapTags(LuaKv kv, string a, string b)
    {
        string? va = kv.GetString(a);
        kv.SetString(a, kv.GetString(b));
        kv.SetString(b, va);
    }

    private static void SetLaneCount(LuaKv kv, string outKey, string inKey)
    {
        string? prefix = LuaTagTables.NumericPrefix(kv.GetString(inKey), false);
        double? laneCount = LuaTagTables.TryParseLua(prefix, out double v) ? v : (double?)null;
        if (laneCount.HasValue && laneCount.Value > 15)
        {
            laneCount = null;
        }

        kv.SetString(outKey, laneCount.HasValue ? FmtNum(laneCount.Value) : null);
    }

    private static void ApplyConditionalRestrictionExemption(LuaKv kv, string restrKey, bool directed)
    {
        string conditionalTag = restrKey + ":conditional";
        int exceptDestination = LuaTagTables.ConditionalAccessRestriction.GetValueOrDefault(kv.GetString(conditionalTag) ?? "\0", 0);
        if (exceptDestination == 1 && kv.GetString(restrKey) != null)
        {
            kv.SetString(restrKey, kv.GetString(restrKey) + "~");
        }

        if (directed)
        {
            foreach (string direction in new[] { "forward", "backward" })
            {
                string key = restrKey + "_" + direction;
                string tag = restrKey + ":" + direction;
                string condTag = tag + ":conditional";
                int exc = LuaTagTables.ConditionalAccessRestriction.GetValueOrDefault(kv.GetString(condTag) ?? "\0", 0);
                if (exc == 1 && kv.GetString(tag) != null)
                {
                    // Lua does tostring(kv[key]) .. "~" - kv[key] may be nil -> "nil~".
                    string current = kv.GetString(key) ?? "nil";
                    kv.SetString(key, current + "~");
                }
            }
        }
    }

    private static string? HazmatChain(LuaKv kv, params string[] keys)
    {
        string? result = null;
        foreach (string key in keys)
        {
            result = Or(result, HazmatLookup(kv.GetString(key)));
        }

        return result;
    }

    private static bool CycleLaneAny(string? v) =>
        v != null && (LuaTagTables.Shared.ContainsKey(v) || LuaTagTables.Separated.ContainsKey(v) || LuaTagTables.Dedicated.ContainsKey(v));

    private static int? CycleLaneVal(string? v)
    {
        if (v == null)
        {
            return null;
        }

        if (LuaTagTables.Shared.TryGetValue(v, out int s))
        {
            return s;
        }

        if (LuaTagTables.Separated.TryGetValue(v, out int sep))
        {
            return sep;
        }

        if (LuaTagTables.Dedicated.TryGetValue(v, out int d))
        {
            return d;
        }

        return null;
    }

    private static int? BufferLookup(string? v) =>
        v != null && LuaTagTables.Buffer.TryGetValue(v, out int b) ? b : (int?)null;

    private static string? PsvLookup(string? v) => v != null && LuaTagTables.Psv.TryGetValue(v, out string? r) ? r : null;

    private static string? FootLookup(string? v) => v != null && LuaTagTables.Foot.TryGetValue(v, out string? r) ? r : null;

    private static string? BicycleLookup(string? v) => v != null && LuaTagTables.Bicycle.TryGetValue(v, out string? r) ? r : null;

    private static string? OnewayLookup(string? v) => v != null && LuaTagTables.Oneway.TryGetValue(v, out string? r) ? r : null;

    private static string? BridgeLookup(string? v) => v != null && LuaTagTables.Bridge.TryGetValue(v, out string? r) ? r : null;

    private static string? TunnelLookup(string? v) => v != null && LuaTagTables.Tunnel.TryGetValue(v, out string? r) ? r : null;

    private static string? TollLookup(string? v) => v != null && LuaTagTables.Toll.TryGetValue(v, out string? r) ? r : null;

    private static string? LitLookup(string? v) => v != null && LuaTagTables.Lit.TryGetValue(v, out string? r) ? r : null;

    private static string? HazmatLookup(string? v) => v != null && LuaTagTables.Hazmat.TryGetValue(v, out string? r) ? r : null;

    private static string? ShoulderLookup(string? v) => v != null && LuaTagTables.Shoulder.TryGetValue(v, out string? r) ? r : null;

    private static string? ShoulderRightLookup(string? v) => v != null && LuaTagTables.ShoulderRight.TryGetValue(v, out string? r) ? r : null;

    private static string? ShoulderLeftLookup(string? v) => v != null && LuaTagTables.ShoulderLeft.TryGetValue(v, out string? r) ? r : null;

    private static string? BikeReverseLookup(string? v) => v != null && LuaTagTables.BikeReverse.TryGetValue(v, out string? r) ? r : null;

    // Lua `a or b` for measurement results (numbers): a unless a is nil.
    private static double? Or2(double? a, double? b) => a ?? b;

    private static string? FmtNum(double? v) =>
        v.HasValue ? LuaValue.Number(v.Value).AsLuaString() : null;

    private static IEnumerable<string> SplitPipe(string s)
    {
        // Lua gmatch("([^|]*)|") over (str .. '|').
        int start = 0;
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] == '|')
            {
                yield return s.Substring(start, i - start);
                start = i + 1;
            }
        }
    }
}
