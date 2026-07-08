// Tests for the faithful C# port of graph.lua's node tag normalization (nodes_proc).
// Source: lua/graph.lua @ 3.7.0, function nodes_proc (line ~2039) and the node access-mask
// tables it consults (motor_vehicle_node/foot_node/bicycle_node/... line ~441 onward).
//
// nodes_proc is the control-device logic that the unprotected-left rule depends on: it
// produces the forward/backward stop and yield (give_way) flags, traffic-signal direction
// (incl. all-way / named-junction handling), the barrier classification (gate / bollard /
// sump_buster / wall / border_control / toll_booth / toll_gantry / elevator), the per-mode
// access_mask, and the tagged_access flag. Each expectation below is derived directly from
// the Lua source (and mirrors behaviors exercised by upstream test/graphparser.cc), so the
// C# transform stays bit-for-bit identical to what Valhalla's Lua front-end emits.

using System.Collections.Generic;

using SharpNinja.Valhalla.Baldr;
using SharpNinja.Valhalla.Mjolnir;

namespace SharpNinja.Valhalla.Tests.Mjolnir;

public class NodeTagTransformTests
{
    // --- access-mask bit constants (match the node tables in graph.lua exactly) ----
    private const int Auto = 1;        // motor_vehicle_node
    private const int Foot = 2;        // foot_node
    private const int Bike = 4;        // bicycle_node
    private const int Truck = 8;       // truck_node
    private const int Emergency = 16;  // emergency
    private const int Taxi = 32;       // taxi_node
    private const int Bus = 64;        // bus_node
    private const int Hov = 128;       // hov
    private const int Wheelchair = 256;// wheelchair_node
    private const int Moped = 512;     // moped_node
    private const int Motorcycle = 1024; // motor_cycle_node

    // The "everything allowed" mask nodes_proc emits when no tag blocks access.
    private const int FullMask =
        Auto | Foot | Bike | Truck | Emergency | Taxi | Bus | Hov | Wheelchair | Moped | Motorcycle;

    private static Dictionary<string, string> Node(params (string k, string v)[] input)
    {
        var tags = new Dictionary<string, string>();
        foreach ((string k, string v) in input)
        {
            tags[k] = v;
        }

        int result = NodeTagTransform.Transform(tags);
        Assert.Equal(0, result); // nodes_proc always returns 0 (never filters a node).
        return tags;
    }

    private static int Mask(Dictionary<string, string> tags) => int.Parse(tags["access_mask"]);

    // GraphConstants must agree with the literal node masks in graph.lua, otherwise every
    // mask assertion below is meaningless.
    [Fact]
    public void GraphConstants_MatchLuaNodeMaskBits()
    {
        Assert.Equal(Auto, GraphConstants.AutoAccess);
        Assert.Equal(Foot, GraphConstants.PedestrianAccess);
        Assert.Equal(Bike, GraphConstants.BicycleAccess);
        Assert.Equal(Truck, GraphConstants.TruckAccess);
        Assert.Equal(Emergency, GraphConstants.EmergencyAccess);
        Assert.Equal(Taxi, GraphConstants.TaxiAccess);
        Assert.Equal(Bus, GraphConstants.BusAccess);
        Assert.Equal(Hov, GraphConstants.HovAccess);
        Assert.Equal(Wheelchair, GraphConstants.WheelchairAccess);
        Assert.Equal(Moped, GraphConstants.MopedAccess);
        Assert.Equal(Motorcycle, GraphConstants.MotorcycleAccess);
    }

    // ---- baseline / access ----------------------------------------------------

    [Fact]
    public void NoTags_FullAccess_NotTagged()
    {
        Dictionary<string, string> tags = Node();
        Assert.Equal(FullMask, Mask(tags));
        Assert.Equal("0", tags["tagged_access"]);
        // No barrier -> all three barrier flags stored as "false".
        Assert.Equal("false", tags["gate"]);
        Assert.Equal("false", tags["bollard"]);
        Assert.Equal("false", tags["sump_buster"]);
        // private defaults to "false".
        Assert.Equal("false", tags["private"]);
    }

    [Fact]
    public void AccessNo_ZeroMask_Tagged()
    {
        Dictionary<string, string> tags = Node(("access", "no"));
        Assert.Equal(0, Mask(tags));
        Assert.Equal("1", tags["tagged_access"]);
    }

    [Fact]
    public void AccessYes_FullAccess_Tagged()
    {
        Dictionary<string, string> tags = Node(("access", "yes"));
        Assert.Equal(FullMask, Mask(tags));
        // initial_access is non-nil -> tagged_access 1.
        Assert.Equal("1", tags["tagged_access"]);
    }

    [Fact]
    public void AccessPrivate_FullAccess_PrivateFlagSet()
    {
        // access=private resolves to "true" in the access table, so full access remains,
        // and private is set from any_in(private, "private").
        Dictionary<string, string> tags = Node(("access", "private"));
        Assert.Equal(FullMask, Mask(tags));
        Assert.Equal("true", tags["private"]);
        Assert.Equal("1", tags["tagged_access"]);
    }

    [Fact]
    public void Impassable_ZeroMask()
    {
        // impassable=yes forces access "false" -> all modes default to 0.
        Dictionary<string, string> tags = Node(("impassable", "yes"));
        Assert.Equal(0, Mask(tags));
    }

    [Fact]
    public void VehicleNo_ShutsVehicleModes_KeepsFoot()
    {
        // vehicle=no zeroes the vehicle modes but (per Lua) does NOT clear foot.
        Dictionary<string, string> tags = Node(("vehicle", "no"));
        int mask = Mask(tags);
        Assert.Equal(0, mask & Auto);
        Assert.Equal(0, mask & Truck);
        Assert.Equal(0, mask & Bus);
        Assert.Equal(0, mask & Taxi);
        Assert.Equal(0, mask & Bike);
        // foot is NOT cleared by vehicle=no.
        Assert.Equal(Foot, mask & Foot);
    }

    [Fact]
    public void HovDesignated_ShutsModesIncludingFoot()
    {
        // hov=designated triggers the access==false-style shutoff AND clears foot.
        Dictionary<string, string> tags = Node(("hov", "designated"));
        int mask = Mask(tags);
        Assert.Equal(0, mask & Auto);
        Assert.Equal(0, mask & Foot);
    }

    [Fact]
    public void MotorVehicleNo_ShutsOffDerivedMotorModes()
    {
        // motor_vehicle=no -> motor_vehicle_tag 0; the "must shut off if motor_vehicle = 0"
        // block forces hov/bus/taxi/truck/moped/motorcycle to 0 when not otherwise tagged.
        Dictionary<string, string> tags = Node(("motor_vehicle", "no"));
        int mask = Mask(tags);
        Assert.Equal(0, mask & Auto);
        Assert.Equal(0, mask & Truck);
        Assert.Equal(0, mask & Bus);
        Assert.Equal(0, mask & Taxi);
        Assert.Equal(0, mask & Hov);
        Assert.Equal(0, mask & Moped);
        Assert.Equal(0, mask & Motorcycle);
        // foot / bike / wheelchair / emergency are unaffected.
        Assert.Equal(Foot, mask & Foot);
        Assert.Equal(Bike, mask & Bike);
    }

    [Fact]
    public void AccessPsv_EnablesBusAndTaxi()
    {
        // access=psv -> bus_tag 64, taxi_tag 32 (but access itself is not in the access table
        // as "true", so initial_access is nil and other modes use their tag-or-default values).
        Dictionary<string, string> tags = Node(("access", "psv"));
        int mask = Mask(tags);
        Assert.Equal(Bus, mask & Bus);
        Assert.Equal(Taxi, mask & Taxi);
    }

    // ---- barriers -------------------------------------------------------------

    [Fact]
    public void Gate_FullAccess()
    {
        Dictionary<string, string> tags = Node(("barrier", "gate"));
        Assert.Equal("true", tags["gate"]);
        Assert.Equal("false", tags["bollard"]);
        Assert.Equal(FullMask, Mask(tags));
    }

    [Theory]
    [InlineData("lift_gate")]
    [InlineData("swing_gate")]
    [InlineData("sliding_beam")]
    [InlineData("yes")]
    public void GateVariants_TreatedAsGate(string barrier)
    {
        Dictionary<string, string> tags = Node(("barrier", barrier));
        Assert.Equal("true", tags["gate"]);
        Assert.Equal(FullMask, Mask(tags));
    }

    [Fact]
    public void Bollard_PedestrianWheelchairBicycleOnly()
    {
        // bollard with no explicit access -> only foot|wheelchair|bike.
        Dictionary<string, string> tags = Node(("barrier", "bollard"));
        Assert.Equal("true", tags["bollard"]);
        Assert.Equal(Foot | Wheelchair | Bike, Mask(tags));
    }

    [Theory]
    [InlineData("block")]
    [InlineData("kissing_gate")]
    [InlineData("motorcycle_barrier")]
    [InlineData("cycle_barrier")]
    [InlineData("chain")]
    [InlineData("bar")]
    public void BollardVariants_PedestrianWheelchairBicycleOnly(string barrier)
    {
        Dictionary<string, string> tags = Node(("barrier", barrier));
        Assert.Equal("true", tags["bollard"]);
        Assert.Equal(Foot | Wheelchair | Bike, Mask(tags));
    }

    [Fact]
    public void RemovableBollard_TreatedAsBollard()
    {
        // bollard=removable -> bollard true even with barrier unset.
        Dictionary<string, string> tags = Node(("bollard", "removable"));
        Assert.Equal("true", tags["bollard"]);
        Assert.Equal(Foot | Wheelchair | Bike, Mask(tags));
    }

    [Fact]
    public void RisingBollard_PromotedToGate()
    {
        // bollard=rising flips bollard back off and marks the node as a gate (full access).
        Dictionary<string, string> tags = Node(("barrier", "bollard"), ("bollard", "rising"));
        Assert.Equal("true", tags["gate"]);
        Assert.Equal("false", tags["bollard"]);
        Assert.Equal(FullMask, Mask(tags));
    }

    [Fact]
    public void BollardWithExplicitAccess_DoesNotShutOff()
    {
        // initial_access != nil -> bollard does NOT force the foot/bike-only mask.
        Dictionary<string, string> tags = Node(("barrier", "bollard"), ("access", "yes"));
        Assert.Equal("true", tags["bollard"]);
        Assert.Equal(FullMask, Mask(tags));
    }

    [Fact]
    public void SumpBuster_BlocksAutoTaxiHov_KeepsTruckBusFootBike()
    {
        // sump_buster: auto/taxi/hov off (no tag), truck/bus/foot/wheelchair/bike/moped/
        // motorcycle/emergency keep their defaults.
        Dictionary<string, string> tags = Node(("barrier", "sump_buster"));
        Assert.Equal("true", tags["sump_buster"]);
        int mask = Mask(tags);
        Assert.Equal(0, mask & Auto);
        Assert.Equal(0, mask & Taxi);
        Assert.Equal(0, mask & Hov);
        Assert.Equal(Truck, mask & Truck);
        Assert.Equal(Bus, mask & Bus);
        Assert.Equal(Foot, mask & Foot);
        Assert.Equal(Bike, mask & Bike);
        Assert.Equal(Moped, mask & Moped);
        Assert.Equal(Motorcycle, mask & Motorcycle);
        Assert.Equal(Emergency, mask & Emergency);
    }

    [Theory]
    [InlineData("fence")]
    [InlineData("barrier_board")]
    [InlineData("wall")]
    [InlineData("jersey_barrier")]
    [InlineData("debris")]
    public void Wall_BlocksEverything(string barrier)
    {
        Dictionary<string, string> tags = Node(("barrier", barrier));
        Assert.Equal(0, Mask(tags));
    }

    [Fact]
    public void BorderControl_FlaggedWithFullAccess()
    {
        Dictionary<string, string> tags = Node(("barrier", "border_control"));
        Assert.Equal("true", tags["border_control"]);
        Assert.Equal(FullMask, Mask(tags));
    }

    [Fact]
    public void TollBooth_Flagged()
    {
        Dictionary<string, string> tags = Node(("barrier", "toll_booth"));
        Assert.Equal("true", tags["toll_booth"]);
        Assert.False(tags.ContainsKey("cash_only_toll"));
    }

    [Fact]
    public void TollBooth_CashOnly()
    {
        Dictionary<string, string> tags = Node(
            ("barrier", "toll_booth"),
            ("payment:cash", "yes"),
            ("payment:credit_cards", "no"));
        Assert.Equal("true", tags["toll_booth"]);
        Assert.Equal("true", tags["cash_only_toll"]);
    }

    [Fact]
    public void TollBooth_NotCashOnly_WhenCardAccepted()
    {
        Dictionary<string, string> tags = Node(
            ("barrier", "toll_booth"),
            ("payment:cash", "yes"),
            ("payment:credit_cards", "yes"));
        Assert.Equal("true", tags["toll_booth"]);
        Assert.False(tags.ContainsKey("cash_only_toll"));
    }

    [Fact]
    public void TollGantry_Flagged()
    {
        Dictionary<string, string> tags = Node(("highway", "toll_gantry"));
        Assert.Equal("true", tags["toll_gantry"]);
    }

    [Fact]
    public void BuildingEntrance_Flagged()
    {
        Dictionary<string, string> tags = Node(("entrance", "yes"), ("indoor", "yes"));
        Assert.Equal("true", tags["building_entrance"]);
    }

    [Fact]
    public void Elevator_Flagged()
    {
        Dictionary<string, string> tags = Node(("highway", "elevator"));
        Assert.Equal("true", tags["elevator"]);
    }

    [Fact]
    public void BicycleRental_FromAmenity()
    {
        Dictionary<string, string> tags = Node(("amenity", "bicycle_rental"));
        Assert.Equal("true", tags["bicycle_rental"]);
    }

    [Fact]
    public void BicycleRental_FromShopService()
    {
        Dictionary<string, string> tags = Node(
            ("shop", "bicycle"),
            ("service:bicycle:rental", "yes"));
        Assert.Equal("true", tags["bicycle_rental"]);
    }

    // ---- traffic signals ------------------------------------------------------

    [Fact]
    public void TrafficSignalDirection_Forward()
    {
        Dictionary<string, string> tags = Node(
            ("highway", "traffic_signals"),
            ("traffic_signals:direction", "forward"));
        Assert.Equal("true", tags["forward_signal"]);
        Assert.False(tags.ContainsKey("backward_signal"));
    }

    [Fact]
    public void TrafficSignalDirection_Backward()
    {
        Dictionary<string, string> tags = Node(
            ("highway", "traffic_signals"),
            ("traffic_signals:direction", "backward"));
        Assert.Equal("true", tags["backward_signal"]);
        Assert.False(tags.ContainsKey("forward_signal"));
    }

    [Fact]
    public void TrafficSignalDirection_Forward_WithName_NamesJunction()
    {
        // traffic_signals:direction=forward + a name (no public_transport) -> junction "named".
        Dictionary<string, string> tags = Node(
            ("highway", "traffic_signals"),
            ("traffic_signals:direction", "forward"),
            ("name", "Main & 1st"));
        Assert.Equal("true", tags["forward_signal"]);
        Assert.Equal("named", tags["junction"]);
    }

    [Fact]
    public void TrafficSignalDirection_PublicTransport_DoesNotName()
    {
        // public_transport present suppresses the named-junction assignment.
        Dictionary<string, string> tags = Node(
            ("highway", "traffic_signals"),
            ("traffic_signals:direction", "forward"),
            ("name", "Stop A"),
            ("public_transport", "stop_position"));
        Assert.Equal("true", tags["forward_signal"]);
        Assert.False(tags.ContainsKey("junction"));
    }

    [Fact]
    public void TrafficSignalWithName_NamesJunction()
    {
        Dictionary<string, string> tags = Node(
            ("highway", "traffic_signals"),
            ("name", "Broadway & 7th"));
        Assert.Equal("named", tags["junction"]);
    }

    [Fact]
    public void TrafficSignalWithName_AndJunctionYes_NotRenamed()
    {
        // junction=yes is preserved (not overwritten with "named") for traffic_signals.
        Dictionary<string, string> tags = Node(
            ("highway", "traffic_signals"),
            ("name", "Broadway & 7th"),
            ("junction", "yes"));
        Assert.Equal("yes", tags["junction"]);
    }

    [Fact]
    public void JunctionYesWithName_BecomesNamed()
    {
        Dictionary<string, string> tags = Node(
            ("junction", "yes"),
            ("name", "Five Points"));
        Assert.Equal("named", tags["junction"]);
    }

    [Fact]
    public void ReferencePointWithName_BecomesNamedJunction()
    {
        Dictionary<string, string> tags = Node(
            ("reference_point", "yes"),
            ("name", "Mile 42"));
        Assert.Equal("named", tags["junction"]);
    }

    // ---- stop signs (highway=stop) -------------------------------------------

    [Fact]
    public void Stop_NoDirection_NoForwardOrBackwardFlags()
    {
        // highway=stop with no direction: neither forward_stop nor backward_stop set,
        // and highway stays "stop".
        Dictionary<string, string> tags = Node(("highway", "stop"));
        Assert.False(tags.ContainsKey("forward_stop"));
        Assert.False(tags.ContainsKey("backward_stop"));
        Assert.Equal("stop", tags["highway"]);
    }

    [Fact]
    public void Stop_DirectionBoth_AllWayStop()
    {
        Dictionary<string, string> tags = Node(("highway", "stop"), ("direction", "both"));
        Assert.Equal("true", tags["forward_stop"]);
        Assert.Equal("true", tags["backward_stop"]);
        Assert.Equal("stop", tags["highway"]);
    }

    [Fact]
    public void Stop_DirectionForward()
    {
        Dictionary<string, string> tags = Node(("highway", "stop"), ("direction", "forward"));
        Assert.Equal("true", tags["forward_stop"]);
        Assert.False(tags.ContainsKey("backward_stop"));
    }

    [Theory]
    [InlineData("backward")]
    [InlineData("reverse")]
    public void Stop_DirectionBackwardOrReverse(string direction)
    {
        Dictionary<string, string> tags = Node(("highway", "stop"), ("direction", direction));
        Assert.Equal("true", tags["backward_stop"]);
        Assert.False(tags.ContainsKey("forward_stop"));
    }

    [Fact]
    public void Stop_UnknownDirection_NoStopTag_ClearsHighway()
    {
        // direction set to something other than both/forward/backward/reverse, and there's no
        // stop=* tag -> the highway=stop tag is dropped (it was a compass heading, not a sign).
        Dictionary<string, string> tags = Node(("highway", "stop"), ("direction", "north"));
        Assert.False(tags.ContainsKey("highway"));
        Assert.False(tags.ContainsKey("forward_stop"));
        Assert.False(tags.ContainsKey("backward_stop"));
    }

    [Fact]
    public void Stop_UnknownDirection_WithStopTag_KeepsHighway()
    {
        // a stop=* tag means it really is a stop sign; the odd direction does not clear it.
        Dictionary<string, string> tags = Node(
            ("highway", "stop"),
            ("direction", "north"),
            ("stop", "all"));
        Assert.Equal("stop", tags["highway"]);
    }

    // ---- give_way / yield (highway=give_way) ----------------------------------

    [Fact]
    public void GiveWay_NoDirection_NoYieldFlags()
    {
        Dictionary<string, string> tags = Node(("highway", "give_way"));
        Assert.False(tags.ContainsKey("forward_yield"));
        Assert.False(tags.ContainsKey("backward_yield"));
        Assert.Equal("give_way", tags["highway"]);
    }

    [Fact]
    public void GiveWay_DirectionBoth()
    {
        Dictionary<string, string> tags = Node(("highway", "give_way"), ("direction", "both"));
        Assert.Equal("true", tags["forward_yield"]);
        Assert.Equal("true", tags["backward_yield"]);
    }

    [Fact]
    public void GiveWay_DirectionForward()
    {
        Dictionary<string, string> tags = Node(("highway", "give_way"), ("direction", "forward"));
        Assert.Equal("true", tags["forward_yield"]);
        Assert.False(tags.ContainsKey("backward_yield"));
    }

    [Theory]
    [InlineData("backward")]
    [InlineData("reverse")]
    public void GiveWay_DirectionBackwardOrReverse(string direction)
    {
        Dictionary<string, string> tags = Node(("highway", "give_way"), ("direction", direction));
        Assert.Equal("true", tags["backward_yield"]);
        Assert.False(tags.ContainsKey("forward_yield"));
    }

    [Fact]
    public void GiveWay_UnknownDirection_NoGiveWayTag_ClearsHighway()
    {
        Dictionary<string, string> tags = Node(("highway", "give_way"), ("direction", "east"));
        Assert.False(tags.ContainsKey("highway"));
        Assert.False(tags.ContainsKey("forward_yield"));
        Assert.False(tags.ContainsKey("backward_yield"));
    }

    [Fact]
    public void GiveWay_UnknownDirection_WithGiveWayTag_KeepsHighway()
    {
        Dictionary<string, string> tags = Node(
            ("highway", "give_way"),
            ("direction", "east"),
            ("give_way", "yes"));
        Assert.Equal("give_way", tags["highway"]);
    }

    // ---- crossings ------------------------------------------------------------

    [Fact]
    public void HighwayCrossing_FullAccess()
    {
        // a crossing with nothing blocking restores full access.
        Dictionary<string, string> tags = Node(("highway", "crossing"));
        Assert.Equal(FullMask, Mask(tags));
    }

    [Fact]
    public void Crossing_DoesNotShutOffBike()
    {
        // bicycle=no would zero bike, but at a highway=crossing bike is kept (set back to 4).
        Dictionary<string, string> tags = Node(("highway", "crossing"), ("bicycle", "no"));
        int mask = Mask(tags);
        Assert.Equal(Bike, mask & Bike);
    }

    // ---- private flag ---------------------------------------------------------

    [Fact]
    public void Private_FromMotorVehicle()
    {
        Dictionary<string, string> tags = Node(("motor_vehicle", "private"));
        Assert.Equal("true", tags["private"]);
    }

    [Fact]
    public void Private_DefaultFalse()
    {
        Dictionary<string, string> tags = Node(("barrier", "gate"));
        Assert.Equal("false", tags["private"]);
    }

    // ---- tagged_access --------------------------------------------------------

    [Fact]
    public void TaggedAccess_SetWhenAnyModeTagged()
    {
        Dictionary<string, string> tags = Node(("bicycle", "yes"));
        Assert.Equal("1", tags["tagged_access"]);
    }

    [Fact]
    public void TaggedAccess_NotSetForBarrierOnly()
    {
        // a bare gate has no access tag -> tagged_access 0.
        Dictionary<string, string> tags = Node(("barrier", "gate"));
        Assert.Equal("0", tags["tagged_access"]);
    }

    // ---- semicolon list handling (any_in_num) ---------------------------------

    [Fact]
    public void FootList_FirstNonZeroWins()
    {
        // foot="no;yes": any_in_num returns >0 for "yes", so foot stays enabled.
        Dictionary<string, string> tags = Node(("foot", "no;yes"));
        Assert.Equal(Foot, Mask(tags) & Foot);
    }
}
