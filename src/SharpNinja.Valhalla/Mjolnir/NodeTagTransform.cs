// Faithful C# port of graph.lua's node tag normalization (nodes_proc).
// Source: lua/graph.lua @ 3.7.0, function nodes_proc (line ~2039).
//
// nodes_proc turns raw OSM node tags into Valhalla's node tag set: an access_mask, the
// gate/bollard/sump_buster/wall barrier classification, border_control/toll_booth/
// toll_gantry/building_entrance/elevator types, bicycle_rental, traffic-signal direction,
// stop/give_way (forward/backward) control tags, named junction, private, cash_only_toll,
// and tagged_access. It always returns 0 (never filters a node).

using System.Collections.Generic;

namespace SharpNinja.Valhalla.Mjolnir;

/// <summary>
/// Node tag transform. Faithful port of graph.lua <c>nodes_proc</c>. <see cref="Transform"/>
/// mutates <paramref name="tags"/> in place into the normalized node tag set and always
/// returns 0 (nodes are never filtered here).
/// </summary>
public static class NodeTagTransform
{
    /// <summary>
    /// Normalizes raw OSM node tags (faithful to <c>nodes_proc</c>). Always returns 0.
    /// </summary>
    public static int Transform(IDictionary<string, string> tags)
    {
        Dictionary<string, string>? mutableTags = tags as Dictionary<string, string>;
        var kv = mutableTags is null ? new LuaKv(tags) : new LuaKv(mutableTags);
        NodesProc(kv);
        if (mutableTags is null)
        {
            tags.Clear();
            foreach (KeyValuePair<string, string> p in kv.Raw)
            {
                tags[p.Key] = p.Value;
            }
        }

        return 0;
    }

    private static void NodesProc(LuaKv kv)
    {
        // initial_access = any_in(access, kv["access"]); access = initial_access or "true".
        string? initialAccess = LuaTagTables.AnyIn(LuaTagTables.Access, kv.GetString("access"));
        string access = initialAccess ?? "true";

        if (kv.Eq("impassable", "yes") ||
            (kv.Eq("access", "private") && (kv.Eq("emergency", "yes") || kv.Eq("service", "emergency_access"))))
        {
            access = "false";
        }

        int? hovTag = null;
        if ((kv.Has("hov") && !kv.Eq("hov", "no")) || kv.Has("hov:lanes") || kv.Has("hov:minimum"))
        {
            hovTag = 128;
        }

        int? footTag = LuaTagTables.AnyInNum(LuaTagTables.FootNode, kv.GetString("foot"));
        int? wheelchairTag = LuaTagTables.AnyInNum(LuaTagTables.WheelchairNode, kv.GetString("wheelchair"));
        int? bikeTag = LuaTagTables.AnyInNum(LuaTagTables.BicycleNode, kv.GetString("bicycle"));
        int? truckTag = LuaTagTables.AnyInNum(LuaTagTables.TruckNode, kv.GetString("hgv"));
        int? autoTag = LuaTagTables.AnyInNum(LuaTagTables.MotorVehicleNode, kv.GetString("motorcar"));
        int? motorVehicleTag = LuaTagTables.AnyInNum(LuaTagTables.MotorVehicleNode, kv.GetString("motor_vehicle"));
        int? mopedTag = LuaTagTables.AnyInNum(LuaTagTables.MopedNode, kv.GetString("moped")) ??
                        LuaTagTables.AnyInNum(LuaTagTables.MopedNode, kv.GetString("mofa"));
        int? motorcycleTag = LuaTagTables.AnyInNum(LuaTagTables.MotorCycleNode, kv.GetString("motorcycle"));

        if (autoTag == null)
        {
            autoTag = motorVehicleTag;
        }

        int? busTag;
        int? taxiTag;
        if (kv.Eq("access", "psv"))
        {
            busTag = 64;
            taxiTag = 32;
        }
        else
        {
            busTag = LuaTagTables.AnyInNum(LuaTagTables.BusNode, kv.GetString("bus"));
            taxiTag = LuaTagTables.AnyInNum(LuaTagTables.TaxiNode, kv.GetString("taxi"));
        }

        if (busTag == null)
        {
            busTag = LuaTagTables.AnyInNum(LuaTagTables.PsvBusNode, kv.GetString("psv"));
        }

        if (busTag == null && autoTag == 1)
        {
            busTag = 64;
        }

        if (wheelchairTag == null && footTag == 2)
        {
            wheelchairTag = 256;
        }

        if (hovTag == null && autoTag == 1)
        {
            hovTag = 128;
        }

        if (taxiTag == null)
        {
            taxiTag = LuaTagTables.AnyInNum(LuaTagTables.PsvTaxiNode, kv.GetString("psv"));
        }

        if (taxiTag == null && autoTag == 1)
        {
            taxiTag = 32;
        }

        if (truckTag == null && autoTag == 1)
        {
            truckTag = 8;
        }

        // must shut these off if motor_vehicle = 0.
        if (motorVehicleTag == 0)
        {
            hovTag ??= 0;
            busTag ??= 0;
            taxiTag ??= 0;
            truckTag ??= 0;
            mopedTag ??= 0;
            motorcycleTag ??= 0;
        }

        int? emergencyTag = null;
        if (kv.Eq("access", "emergency") || kv.Eq("emergency", "yes") || kv.Eq("service", "emergency_access"))
        {
            emergencyTag = 16;
        }

        // don't shut off bike access at a highway crossing.
        if (bikeTag == 0 && kv.Eq("highway", "crossing"))
        {
            bikeTag = 4;
        }

        int auto = autoTag ?? 1;
        int truck = truckTag ?? 8;
        int bus = busTag ?? 64;
        int taxi = taxiTag ?? autoTag ?? 32;
        int foot = footTag ?? 2;
        int wheelchair = wheelchairTag ?? 256;
        int bike = bikeTag ?? 4;
        int emergency = emergencyTag ?? 16;
        int hov = hovTag ?? autoTag ?? 128;
        int moped = mopedTag ?? 512;
        int motorcycle = motorcycleTag ?? 1024;

        if (access == "false" || kv.Eq("vehicle", "no") || kv.Eq("smoothness", "impassable") || kv.Eq("hov", "designated"))
        {
            auto = autoTag ?? 0;
            truck = truckTag ?? 0;
            bus = busTag ?? 0;
            taxi = taxiTag ?? 0;

            if (access == "false" || kv.Eq("hov", "designated"))
            {
                foot = footTag ?? 0;
            }

            wheelchair = wheelchairTag ?? 0;
            bike = bikeTag ?? 0;
            moped = mopedTag ?? 0;
            motorcycle = motorcycleTag ?? 0;
            emergency = emergencyTag ?? 0;
            hov = hovTag ?? 0;
        }

        // gates, bollards, walls, sump_busters.
        bool gate = kv.Eq("barrier", "gate") || kv.Eq("barrier", "yes") || kv.Eq("barrier", "lift_gate") ||
                    kv.Eq("barrier", "swing_gate") || kv.Eq("barrier", "sliding_beam");
        bool bollard = false;
        bool sumpBuster = false;
        bool wall = false;

        if (!gate)
        {
            bollard = kv.Eq("barrier", "bollard") || kv.Eq("barrier", "block") || kv.Eq("bollard", "removable") ||
                      kv.Eq("barrier", "kissing_gate") || kv.Eq("barrier", "motorcycle_barrier") ||
                      kv.Eq("barrier", "cycle_barrier") || kv.Eq("barrier", "chain") || kv.Eq("barrier", "bar");

            sumpBuster = kv.Eq("barrier", "sump_buster");

            wall = kv.Eq("barrier", "fence") || kv.Eq("barrier", "barrier_board") || kv.Eq("barrier", "wall") ||
                   kv.Eq("barrier", "jersey_barrier") || kv.Eq("barrier", "debris");

            if (bollard && kv.Eq("bollard", "rising"))
            {
                gate = true;
                bollard = false;
            }

            if (bollard && initialAccess == null)
            {
                auto = autoTag ?? 0;
                truck = truckTag ?? 0;
                bus = busTag ?? 0;
                taxi = taxiTag ?? 0;
                foot = footTag ?? 2;
                wheelchair = wheelchairTag ?? 256;
                bike = bikeTag ?? 4;
                moped = mopedTag ?? 0;
                motorcycle = motorcycleTag ?? 0;
                emergency = emergencyTag ?? 0;
                hov = hovTag ?? 0;
            }
            else if (sumpBuster)
            {
                auto = autoTag ?? 0;
                truck = truckTag ?? 8;
                bus = busTag ?? 64;
                taxi = taxiTag ?? 0;
                foot = footTag ?? 2;
                wheelchair = wheelchairTag ?? 256;
                bike = bikeTag ?? 4;
                moped = mopedTag ?? 512;
                motorcycle = motorcycleTag ?? 1024;
                emergency = emergencyTag ?? 16;
                hov = hovTag ?? 0;
            }
            else if (wall)
            {
                auto = autoTag ?? 0;
                truck = truckTag ?? 0;
                bus = busTag ?? 0;
                taxi = taxiTag ?? 0;
                foot = footTag ?? 0;
                wheelchair = wheelchairTag ?? 0;
                bike = bikeTag ?? 0;
                moped = mopedTag ?? 0;
                motorcycle = motorcycleTag ?? 0;
                emergency = emergencyTag ?? 0;
                hov = hovTag ?? 0;
            }
        }

        // if nothing blocks access at this node assume access is allowed (for crossings).
        if (!gate && !bollard && !sumpBuster && !wall && access == "true")
        {
            if (kv.Eq("highway", "crossing") || kv.Eq("railway", "crossing") || kv.Eq("footway", "crossing") ||
                kv.Eq("cycleway", "crossing") || kv.Eq("foot", "crossing") || kv.Eq("bicycle", "crossing") ||
                kv.Eq("pedestrian", "crossing") || kv.Has("crossing"))
            {
                auto = autoTag ?? 1;
                truck = truckTag ?? 8;
                bus = busTag ?? 64;
                taxi = taxiTag ?? 32;
                foot = footTag ?? 2;
                wheelchair = wheelchairTag ?? 256;
                bike = bikeTag ?? 4;
                moped = mopedTag ?? 512;
                motorcycle = motorcycleTag ?? 1024;
                emergency = emergencyTag ?? 16;
                hov = hovTag ?? 128;
            }
        }

        kv.SetString("gate", gate ? "true" : "false");
        kv.SetString("bollard", bollard ? "true" : "false");
        kv.SetString("sump_buster", sumpBuster ? "true" : "false");

        if (kv.Eq("barrier", "border_control"))
        {
            kv.SetBool("border_control", true);
        }
        else if (kv.Eq("barrier", "toll_booth"))
        {
            kv.SetBool("toll_booth", true);
            if (LuaTagTables.IsCashOnlyPayment(kv.Raw))
            {
                kv.SetBool("cash_only_toll", true);
            }
        }
        else if (kv.Eq("highway", "toll_gantry"))
        {
            kv.SetBool("toll_gantry", true);
        }
        else if (kv.Eq("entrance", "yes") && kv.Eq("indoor", "yes"))
        {
            kv.SetBool("building_entrance", true);
        }
        else if (kv.Eq("highway", "elevator"))
        {
            kv.SetBool("elevator", true);
        }

        if (kv.Eq("amenity", "bicycle_rental") ||
            (kv.Eq("shop", "bicycle") && kv.Eq("service:bicycle:rental", "yes")))
        {
            kv.SetBool("bicycle_rental", true);
        }

        if (kv.Eq("traffic_signals:direction", "forward"))
        {
            kv.SetBool("forward_signal", true);
            if (!kv.Has("public_transport") && kv.Has("name"))
            {
                kv.SetString("junction", "named");
            }
        }

        if (kv.Eq("traffic_signals:direction", "backward"))
        {
            kv.SetBool("backward_signal", true);
            if (!kv.Has("public_transport") && kv.Has("name"))
            {
                kv.SetString("junction", "named");
            }
        }

        if (kv.Eq("highway", "stop"))
        {
            if (kv.Eq("direction", "both"))
            {
                kv.SetBool("forward_stop", true);
                kv.SetBool("backward_stop", true);
            }
            else if (kv.Eq("direction", "forward"))
            {
                kv.SetBool("forward_stop", true);
            }
            else if (kv.Eq("direction", "backward") || kv.Eq("direction", "reverse"))
            {
                kv.SetBool("backward_stop", true);
            }
            else if (kv.Has("direction") && !kv.Has("stop"))
            {
                kv.Remove("highway");
            }
        }

        if (kv.Eq("highway", "give_way"))
        {
            if (kv.Eq("direction", "both"))
            {
                kv.SetBool("forward_yield", true);
                kv.SetBool("backward_yield", true);
            }
            else if (kv.Eq("direction", "forward"))
            {
                kv.SetBool("forward_yield", true);
            }
            else if (kv.Eq("direction", "backward") || kv.Eq("direction", "reverse"))
            {
                kv.SetBool("backward_yield", true);
            }
            else if (kv.Has("direction") && !kv.Has("give_way"))
            {
                kv.Remove("highway");
            }
        }

        if (!kv.Has("public_transport") && kv.Has("name"))
        {
            if (kv.Eq("highway", "traffic_signals"))
            {
                if (!kv.Eq("junction", "yes"))
                {
                    kv.SetString("junction", "named");
                }
            }
            else if (kv.Eq("junction", "yes") || kv.Eq("reference_point", "yes"))
            {
                kv.SetString("junction", "named");
            }
        }

        kv.SetString("private", Or(
            LuaTagTables.AnyIn(LuaTagTables.Private, kv.GetString("access")),
            Or(LuaTagTables.AnyIn(LuaTagTables.Private, kv.GetString("motor_vehicle")), "false")));

        // store a mask denoting access (bit-or of all modes).
        int accessMask = auto | emergency | truck | bike | foot | wheelchair | bus | hov | moped | motorcycle | taxi;
        kv.Set("access_mask", LuaValue.Number(accessMask));

        // tagged_access flag.
        bool anyTagged = initialAccess != null || autoTag != null || truckTag != null || busTag != null ||
                         taxiTag != null || footTag != null || wheelchairTag != null || bikeTag != null ||
                         mopedTag != null || motorcycleTag != null || emergencyTag != null || hovTag != null;
        kv.Set("tagged_access", LuaValue.Number(anyTagged ? 1 : 0));
    }

    private static string? Or(string? a, string? b) => a ?? b;
}
