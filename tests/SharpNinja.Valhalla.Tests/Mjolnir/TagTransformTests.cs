// Tests for the faithful C# port of graph.lua's way + node tag transforms.
// Source: lua/graph.lua @ 3.7.0 (filter_tags_generic / ways_proc, nodes_proc).
//
// Each case derives its expected output directly from the Lua logic (and mirrors the
// behavioral expectations exercised by test/graphparser.cc: footway = pedestrian only,
// residential = all modes both directions, oneway blocks the backward auto direction,
// motorway excludes bike/pedestrian, construction shuts off all access, bus-only access,
// and the control-node tags - traffic-signal/stop/give_way direction, gate/bollard access
// masks - produced by nodes_proc).

using System.Collections.Generic;

using SharpNinja.Valhalla.Baldr;
using SharpNinja.Valhalla.Mjolnir;

namespace SharpNinja.Valhalla.Tests.Mjolnir;

public class TagTransformTests
{
    private static (int filter, Dictionary<string, string> tags) Way(params (string k, string v)[] input)
    {
        var tags = new Dictionary<string, string>();
        foreach ((string k, string v) in input)
        {
            tags[k] = v;
        }

        int filter = WayTagTransform.Transform(tags);
        return (filter, tags);
    }

    private static Dictionary<string, string> Node(params (string k, string v)[] input)
    {
        var tags = new Dictionary<string, string>();
        foreach ((string k, string v) in input)
        {
            tags[k] = v;
        }

        NodeTagTransform.Transform(tags);
        return tags;
    }

    [Fact]
    public void Way_EmptyTags_Filtered()
    {
        (int filter, _) = Way();
        Assert.Equal(1, filter);
    }

    [Fact]
    public void Way_Residential_AllModesBothDirections()
    {
        (int filter, Dictionary<string, string> tags) = Way(("highway", "residential"));
        Assert.Equal(0, filter);
        Assert.Equal("true", tags["auto_forward"]);
        Assert.Equal("true", tags["auto_backward"]);
        Assert.Equal("true", tags["bike_forward"]);
        Assert.Equal("true", tags["bike_backward"]);
        Assert.Equal("true", tags["pedestrian_forward"]);
        Assert.Equal("true", tags["pedestrian_backward"]);
        // road_class 6 (residential), default_speed 35.
        Assert.Equal("6", tags["road_class"]);
        Assert.Equal("35", tags["default_speed"]);
    }

    [Fact]
    public void Way_Footway_PedestrianOnly()
    {
        // Mirrors graphparser TestBaltimoreArea way 133689121 (footway -> pedestrian only).
        (int filter, Dictionary<string, string> tags) = Way(("highway", "footway"));
        Assert.Equal(0, filter);
        Assert.Equal("false", tags["auto_forward"]);
        Assert.Equal("false", tags["auto_backward"]);
        Assert.Equal("false", tags["bus_forward"]);
        Assert.Equal("false", tags["bike_forward"]);
        Assert.Equal("true", tags["pedestrian_forward"]);
        Assert.Equal("true", tags["pedestrian_backward"]);
        // footway use = 25.
        Assert.Equal("25", tags["use"]);
    }

    [Fact]
    public void Way_OnewayYes_BlocksAutoBackward()
    {
        // Mirrors graphparser TestBaltimoreArea way 49641455 (oneway: forward only for auto).
        (int filter, Dictionary<string, string> tags) = Way(("highway", "residential"), ("oneway", "yes"));
        Assert.Equal(0, filter);
        Assert.Equal("true", tags["auto_forward"]);
        Assert.Equal("false", tags["auto_backward"]);
        Assert.Equal("true", tags["oneway"]);
        // pedestrian remains both directions on a normal road.
        Assert.Equal("true", tags["pedestrian_forward"]);
        Assert.Equal("true", tags["pedestrian_backward"]);
    }

    [Fact]
    public void Way_OnewayNo_AutoBackwardStaysOn()
    {
        // Mirrors graphparser TestBaltimoreArea way 192573108 (oneway=no -> auto backward set).
        (int _, Dictionary<string, string> tags) = Way(("highway", "residential"), ("oneway", "no"));
        Assert.Equal("true", tags["auto_forward"]);
        Assert.Equal("true", tags["auto_backward"]);
        Assert.Equal("false", tags["oneway"]);
    }

    [Fact]
    public void Way_OnewayMinusOne_FlipsForwardBackward()
    {
        // oneway=-1 reverses directionality; oneway normalizes to "true".
        (int _, Dictionary<string, string> tags) = Way(("highway", "residential"), ("oneway", "-1"));
        Assert.Equal("true", tags["oneway"]);
        Assert.Equal("true", tags["oneway_reverse"]);
        // oneway_norm=="true" sets auto_backward=false; then the oneway=-1 flip swaps
        // forward<->backward, so the travelable direction ends up on auto_backward.
        Assert.Equal("false", tags["auto_forward"]);
        Assert.Equal("true", tags["auto_backward"]);
    }

    [Fact]
    public void Way_Motorway_NoBikeOrPedestrian()
    {
        (int _, Dictionary<string, string> tags) = Way(("highway", "motorway"));
        Assert.Equal("true", tags["auto_forward"]);
        Assert.Equal("true", tags["truck_forward"]);
        Assert.Equal("false", tags["bike_forward"]);
        Assert.Equal("false", tags["pedestrian_forward"]);
        // road_class 0, default_speed 105.
        Assert.Equal("0", tags["road_class"]);
        Assert.Equal("105", tags["default_speed"]);
        // No oneway tag: oneway[nil] = nil, so kv["oneway"] is cleared (Lua nil removal).
        Assert.False(tags.ContainsKey("oneway"));
    }

    [Fact]
    public void Way_Construction_AllAccessOff()
    {
        (int filter, Dictionary<string, string> tags) = Way(("highway", "construction"), ("construction", "residential"));
        Assert.Equal(0, filter);
        Assert.Equal("false", tags["auto_forward"]);
        Assert.Equal("false", tags["auto_backward"]);
        Assert.Equal("false", tags["truck_forward"]);
        Assert.Equal("false", tags["bike_forward"]);
        Assert.Equal("false", tags["pedestrian_forward"]);
        // use = 43 (construction).
        Assert.Equal("43", tags["use"]);
    }

    [Fact]
    public void Way_ConstructionWithoutConstructionTag_Filtered()
    {
        (int filter, _) = Way(("highway", "construction"));
        Assert.Equal(1, filter);
    }

    [Fact]
    public void Way_Area_Filtered()
    {
        (int filter, _) = Way(("highway", "residential"), ("area", "yes"));
        Assert.Equal(1, filter);
    }

    [Fact]
    public void Way_BusOnly_AccessForBusBikePedNotAuto()
    {
        // access=no shuts everything; bus=yes re-enables bus. Mirrors the bus access cases in
        // graphparser TestBus (e.g. bus_forward true, auto_forward false).
        (int filter, Dictionary<string, string> tags) = Way(
            ("highway", "service"),
            ("access", "no"),
            ("bus", "yes"));
        Assert.Equal(0, filter);
        Assert.Equal("false", tags["auto_forward"]);
        Assert.Equal("true", tags["bus_forward"]);
    }

    [Fact]
    public void Way_Roundabout_IsOnewayTrue()
    {
        (int _, Dictionary<string, string> tags) = Way(("highway", "primary"), ("junction", "roundabout"));
        Assert.Equal("true", tags["oneway"]);
        Assert.Equal("true", tags["roundabout"]);
        Assert.Equal("false", tags["auto_backward"]);
    }

    [Fact]
    public void Way_TruckRoute_FromHgvNationalNetwork()
    {
        (int _, Dictionary<string, string> tags) = Way(("highway", "primary"), ("hgv:national_network", "yes"));
        Assert.Equal("true", tags["truck_route"]);
    }

    [Fact]
    public void Way_MaxspeedNone_Unlimited()
    {
        (int _, Dictionary<string, string> tags) = Way(("highway", "motorway"), ("maxspeed", "none"));
        Assert.Equal("unlimited", tags["max_speed"]);
    }

    [Fact]
    public void Way_MaxspeedMph_NormalizedToKph()
    {
        // 60 mph -> round(60 * 1.609344) = 97.
        (int _, Dictionary<string, string> tags) = Way(("highway", "primary"), ("maxspeed", "60 mph"));
        Assert.Equal("97", tags["max_speed"]);
    }

    [Fact]
    public void Way_MaxweightTonnes_Normalized()
    {
        (int _, Dictionary<string, string> tags) = Way(("highway", "primary"), ("maxweight", "7.5t"));
        Assert.Equal("7.5", tags["maxweight"]);
    }

    [Fact]
    public void Way_BikeNetworkMask_FromNcnRcn()
    {
        (int _, Dictionary<string, string> tags) = Way(("highway", "residential"), ("ncn", "yes"), ("rcn", "yes"));
        // ncn=1, rcn=2 -> mask 3.
        Assert.Equal("3", tags["bike_network_mask"]);
    }

    [Fact]
    public void Way_Ferry_RouteFerry_Bidirectional()
    {
        // route=ferry, no highway: ferry=true, road_class 2 (default_speed 75),
        // all modes default true, no oneway -> backward mirrors forward.
        (int filter, Dictionary<string, string> tags) = Way(("route", "ferry"));
        Assert.Equal(0, filter);
        Assert.Equal("true", tags["ferry"]);
        Assert.Equal("true", tags["auto_forward"]);
        Assert.Equal("true", tags["auto_backward"]);
        Assert.Equal("2", tags["road_class"]);
        Assert.Equal("75", tags["default_speed"]);
    }

    [Fact]
    public void Way_Tunnel_NormalizedTrue()
    {
        (int _, Dictionary<string, string> tags) = Way(("highway", "residential"), ("tunnel", "yes"));
        Assert.Equal("true", tags["tunnel"]);
    }

    [Fact]
    public void Way_Tunnel_BuildingPassage_NormalizedTrue()
    {
        // tunnel=building_passage maps to "true" in the tunnel table.
        (int _, Dictionary<string, string> tags) = Way(("highway", "footway"), ("tunnel", "building_passage"));
        Assert.Equal("true", tags["tunnel"]);
    }

    [Fact]
    public void Way_Bridge_NormalizedTrue()
    {
        (int _, Dictionary<string, string> tags) = Way(("highway", "primary"), ("bridge", "yes"));
        Assert.Equal("true", tags["bridge"]);
    }

    [Fact]
    public void Way_NoBridgeTag_DefaultsFalse()
    {
        (int _, Dictionary<string, string> tags) = Way(("highway", "primary"));
        Assert.Equal("false", tags["bridge"]);
    }

    [Fact]
    public void Way_Toll_NormalizedTrue()
    {
        (int _, Dictionary<string, string> tags) = Way(("highway", "motorway"), ("toll", "yes"));
        Assert.Equal("true", tags["toll"]);
    }

    [Fact]
    public void Way_Surface_PassesThrough()
    {
        // surface is copied through unchanged (kv["surface"] = kv["surface"]).
        (int _, Dictionary<string, string> tags) = Way(("highway", "residential"), ("surface", "gravel"));
        Assert.Equal("gravel", tags["surface"]);
    }

    [Fact]
    public void Way_Name_And_Ref_PassThrough()
    {
        (int _, Dictionary<string, string> tags) = Way(
            ("highway", "primary"),
            ("name", "Main Street"),
            ("ref", "US 1"));
        Assert.Equal("Main Street", tags["name"]);
        Assert.Equal("US 1", tags["ref"]);
    }

    [Fact]
    public void Way_UnsignedRef_UsedWhenNoNameOrRef()
    {
        // motorway/trunk/primary with no name/ref/etc but an unsigned_ref -> ref = unsigned_ref.
        (int _, Dictionary<string, string> tags) = Way(("highway", "motorway"), ("unsigned_ref", "A40"));
        Assert.Equal("A40", tags["ref"]);
    }

    [Fact]
    public void Way_Lanes_Parsed()
    {
        (int _, Dictionary<string, string> tags) = Way(("highway", "primary"), ("lanes", "3"));
        Assert.Equal("3", tags["lanes"]);
    }

    [Fact]
    public void Way_Lanes_OverFifteen_Dropped()
    {
        // lane_count > 15 -> nil (key removed).
        (int _, Dictionary<string, string> tags) = Way(("highway", "primary"), ("lanes", "20"));
        Assert.False(tags.ContainsKey("lanes"));
    }

    [Fact]
    public void Way_Hov_DesignatedWithMinimum2_HovOnly()
    {
        // hov=designated + hov:minimum=2: true HOV-only lane. auto_tag is unset so
        // auto access is shut off, hov_type=HOV2, hov_forward stays true.
        (int _, Dictionary<string, string> tags) = Way(
            ("highway", "motorway"),
            ("hov", "designated"),
            ("hov:minimum", "2"));
        Assert.Equal("HOV2", tags["hov_type"]);
        Assert.Equal("false", tags["auto_forward"]);
        Assert.Equal("false", tags["auto_backward"]);
        Assert.Equal("true", tags["hov_forward"]);
    }

    [Fact]
    public void Way_HovNo_HovAccessOff()
    {
        (int _, Dictionary<string, string> tags) = Way(("highway", "primary"), ("hov", "no"));
        Assert.Equal("false", tags["hov_forward"]);
        Assert.Equal("false", tags["hov_backward"]);
    }

    [Fact]
    public void Way_TruckMaxHeight_NormalizedMeters()
    {
        // maxheight=4.5 (plain number) -> round(4.5, 2) -> 4.5.
        (int _, Dictionary<string, string> tags) = Way(("highway", "primary"), ("maxheight", "4.5"));
        Assert.Equal("4.5", tags["maxheight"]);
    }

    [Fact]
    public void Way_TruckMaxHeight_FeetInches_NormalizedMeters()
    {
        // 3ft6in -> 3*0.3048 + 6*0.0254 = 0.9144 + 0.1524 = 1.0668 -> round 1.07.
        (int _, Dictionary<string, string> tags) = Way(("highway", "primary"), ("maxheight", "3ft6in"));
        Assert.Equal("1.07", tags["maxheight"]);
    }

    [Fact]
    public void Way_TruckMaxWeight_Lbs_ConvertedToTons()
    {
        // 4000lbs -> 4000/2000 = 2 tons.
        (int _, Dictionary<string, string> tags) = Way(("highway", "primary"), ("maxweight", "4000lbs"));
        Assert.Equal("2", tags["maxweight"]);
    }

    [Fact]
    public void Way_Hazmat_NormalizedFalse()
    {
        (int _, Dictionary<string, string> tags) = Way(("highway", "primary"), ("hazmat", "no"));
        Assert.Equal("false", tags["hazmat"]);
    }

    [Fact]
    public void Way_Hazmat_DesignatedTrue()
    {
        (int _, Dictionary<string, string> tags) = Way(("highway", "primary"), ("hazmat", "designated"));
        Assert.Equal("true", tags["hazmat"]);
    }

    [Fact]
    public void Way_MaxspeedHgv_Normalized()
    {
        (int _, Dictionary<string, string> tags) = Way(("highway", "motorway"), ("maxspeed:hgv", "80"));
        Assert.Equal("80", tags["maxspeed:hgv"]);
    }

    [Fact]
    public void Way_LivingStreet_Use10()
    {
        (int _, Dictionary<string, string> tags) = Way(("highway", "living_street"));
        Assert.Equal("10", tags["use"]);
    }

    [Fact]
    public void Way_Track_DefaultSpeed5_Use3()
    {
        // track: use=3, default_speed lowered to 5.
        (int _, Dictionary<string, string> tags) = Way(("highway", "track"));
        Assert.Equal("3", tags["use"]);
        Assert.Equal("5", tags["default_speed"]);
    }

    [Fact]
    public void Way_TrackGrade1_DefaultSpeed20()
    {
        (int _, Dictionary<string, string> tags) = Way(("highway", "track"), ("tracktype", "grade1"));
        Assert.Equal("20", tags["default_speed"]);
    }

    [Fact]
    public void Way_PrivateAccess_FlagSet()
    {
        // access=private -> private="true" (and not combined with emergency, so still routable).
        (int _, Dictionary<string, string> tags) = Way(("highway", "service"), ("access", "private"));
        Assert.Equal("true", tags["private"]);
    }

    [Fact]
    public void Way_LinkType_SetForMotorwayLink()
    {
        (int _, Dictionary<string, string> tags) = Way(("highway", "motorway_link"));
        Assert.Equal("true", tags["link"]);
        Assert.Equal("0", tags["road_class"]);
    }

    [Fact]
    public void Way_CyclewayLane_BikeNetwork_Use20()
    {
        // highway=cycleway -> use=20, bike_forward true.
        (int _, Dictionary<string, string> tags) = Way(("highway", "cycleway"));
        Assert.Equal("20", tags["use"]);
        Assert.Equal("true", tags["bike_forward"]);
        Assert.Equal("false", tags["auto_forward"]);
    }

    // ---- Node transform -------------------------------------------------------

    [Fact]
    public void Node_NoTags_AllAccessAndNotTagged()
    {
        Dictionary<string, string> tags = Node();
        // No access tags -> tagged_access 0, full access mask.
        Assert.Equal("0", tags["tagged_access"]);
        Assert.Equal(FullMaskString(), tags["access_mask"]);
    }

    [Fact]
    public void Node_Gate_FullAccess()
    {
        // Mirrors graphparser gate node 2949666866 (full access on a plain gate).
        Dictionary<string, string> tags = Node(("barrier", "gate"));
        Assert.Equal("true", tags["gate"]);
        Assert.Equal(FullMaskString(), tags["access_mask"]);
    }

    [Fact]
    public void Node_Bollard_PedestrianWheelchairBicycleOnly()
    {
        // Mirrors graphparser bollard node 569645326 (foot|wheelchair|bicycle).
        Dictionary<string, string> tags = Node(("barrier", "bollard"));
        Assert.Equal("true", tags["bollard"]);
        int expected = GraphConstants.PedestrianAccess | GraphConstants.WheelchairAccess | GraphConstants.BicycleAccess;
        Assert.Equal(expected.ToString(), tags["access_mask"]);
    }

    [Fact]
    public void Node_BorderControl_FullAccess()
    {
        Dictionary<string, string> tags = Node(("barrier", "border_control"));
        Assert.Equal("true", tags["border_control"]);
        Assert.Equal(FullMaskString(), tags["access_mask"]);
    }

    [Fact]
    public void Node_RisingBollard_SavedAsGateWithFullAccess()
    {
        // Mirrors graphparser TestRemovableBollards node 2425784125 (bollard=rising -> gate).
        Dictionary<string, string> tags = Node(("barrier", "bollard"), ("bollard", "rising"));
        Assert.Equal("true", tags["gate"]);
        Assert.Equal("false", tags["bollard"]);
        Assert.Equal(FullMaskString(), tags["access_mask"]);
    }

    [Fact]
    public void Node_TollBooth_Tagged()
    {
        Dictionary<string, string> tags = Node(("barrier", "toll_booth"));
        Assert.Equal("true", tags["toll_booth"]);
    }

    [Fact]
    public void Node_CashOnlyTollBooth()
    {
        Dictionary<string, string> tags = Node(
            ("barrier", "toll_booth"),
            ("payment:cash", "yes"),
            ("payment:credit_cards", "no"));
        Assert.Equal("true", tags["toll_booth"]);
        Assert.Equal("true", tags["cash_only_toll"]);
    }

    [Fact]
    public void Node_TrafficSignalDirection_Forward()
    {
        Dictionary<string, string> tags = Node(("highway", "traffic_signals"), ("traffic_signals:direction", "forward"));
        Assert.Equal("true", tags["forward_signal"]);
        Assert.False(tags.ContainsKey("backward_signal"));
    }

    [Fact]
    public void Node_StopSignDirection_Both()
    {
        Dictionary<string, string> tags = Node(("highway", "stop"), ("direction", "both"));
        Assert.Equal("true", tags["forward_stop"]);
        Assert.Equal("true", tags["backward_stop"]);
    }

    [Fact]
    public void Node_GiveWayDirection_Backward()
    {
        Dictionary<string, string> tags = Node(("highway", "give_way"), ("direction", "backward"));
        Assert.Equal("true", tags["backward_yield"]);
        Assert.False(tags.ContainsKey("forward_yield"));
    }

    [Fact]
    public void Node_NamedJunctionFromTrafficSignalWithName()
    {
        Dictionary<string, string> tags = Node(("highway", "traffic_signals"), ("name", "Main & 1st"));
        Assert.Equal("named", tags["junction"]);
    }

    [Fact]
    public void Node_AccessNo_NoAccessMask()
    {
        Dictionary<string, string> tags = Node(("access", "no"));
        Assert.Equal("0", tags["access_mask"]);
        Assert.Equal("1", tags["tagged_access"]);
    }

    private static string FullMaskString()
    {
        int full = GraphConstants.AutoAccess | GraphConstants.HovAccess | GraphConstants.TaxiAccess |
                   GraphConstants.TruckAccess | GraphConstants.BusAccess | GraphConstants.EmergencyAccess |
                   GraphConstants.PedestrianAccess | GraphConstants.WheelchairAccess |
                   GraphConstants.BicycleAccess | GraphConstants.MopedAccess | GraphConstants.MotorcycleAccess;
        return full.ToString();
    }
}
