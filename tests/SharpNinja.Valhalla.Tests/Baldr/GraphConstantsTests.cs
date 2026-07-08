// Fidelity guard tests for the baldr graphconstants port (part of the "ids-constants" group).
// Source enums/constants: F:/github/valhalla/valhalla/baldr/graphconstants.h @ 3.7.0
//
// graphconstants.h has no dedicated gtest of its own; these [Fact]s lock the integer values
// of the bit-packed enums and the canonical enum<->string maps so the port cannot silently
// drift from the on-disk encoding the C++ engine produces.

using SharpNinja.Valhalla.Baldr;

namespace SharpNinja.Valhalla.Tests.Baldr;

public class GraphConstantsTests
{
    [Fact]
    public void AccessBitConstantsMatchCpp()
    {
        Assert.Equal(1, GraphConstants.AutoAccess);
        Assert.Equal(2, GraphConstants.PedestrianAccess);
        Assert.Equal(4, GraphConstants.BicycleAccess);
        Assert.Equal(8, GraphConstants.TruckAccess);
        Assert.Equal(16, GraphConstants.EmergencyAccess);
        Assert.Equal(32, GraphConstants.TaxiAccess);
        Assert.Equal(64, GraphConstants.BusAccess);
        Assert.Equal(128, GraphConstants.HovAccess);
        Assert.Equal(256, GraphConstants.WheelchairAccess);
        Assert.Equal(512, GraphConstants.MopedAccess);
        Assert.Equal(1024, GraphConstants.MotorcycleAccess);
        Assert.Equal(4095, GraphConstants.AllAccess);

        // kVehicularAccess = auto|truck|moped|motorcycle|taxi|bus|hov = 1|8|512|1024|32|64|128 = 1769
        Assert.Equal(1769, GraphConstants.VehicularAccess);
    }

    [Fact]
    public void FieldLimitsMatchCpp()
    {
        Assert.Equal(4194303u, GraphConstants.MaxGraphTileId);
        Assert.Equal(2097151u, GraphConstants.MaxGraphId);
        Assert.Equal(252u, GraphConstants.MaxSpeedKph); // std::max(252, 140)
        Assert.Equal((byte)252, GraphConstants.MaxTrafficSpeed);
        Assert.Equal((byte)140, GraphConstants.MaxAssumedSpeed);
        Assert.Equal(15u, GraphConstants.MaxDensity);
        Assert.Equal(16777215u, GraphConstants.MaxNameOffset);
        Assert.Equal(33554431u, GraphConstants.MaxEdgeInfoOffset);
        Assert.Equal(16777215u, GraphConstants.MaxEdgeLength);
    }

    [Fact]
    public void EnumValuesMatchCpp()
    {
        // Traversability
        Assert.Equal(0, (byte)Traversability.None);
        Assert.Equal(3, (byte)Traversability.Both);

        // RoadClass
        Assert.Equal(0, (byte)RoadClass.Motorway);
        Assert.Equal(7, (byte)RoadClass.ServiceOther);
        Assert.Equal(8, (byte)RoadClass.Invalid);

        // Use - spot-check the non-contiguous values
        Assert.Equal(0, (byte)Use.Road);
        Assert.Equal(20, (byte)Use.Cycleway);
        Assert.Equal(24, (byte)Use.Sidewalk);
        Assert.Equal(32, (byte)Use.PedestrianCrossing);
        Assert.Equal(40, (byte)Use.Other);
        Assert.Equal(54, (byte)Use.TransitConnection);
        Assert.Equal(64, (byte)Use.Size);

        // NodeType
        Assert.Equal(0, (byte)NodeType.StreetIntersection);
        Assert.Equal(14, (byte)NodeType.Elevator);

        // Surface
        Assert.Equal(0, (byte)Surface.PavedSmooth);
        Assert.Equal(7, (byte)Surface.Impassable);

        // Language: starts at 1, none == 255
        Assert.Equal(1, (byte)Language.Ab);
        Assert.Equal(17, (byte)Language.En);
        Assert.Equal(61, (byte)Language.SrLatn);
        Assert.Equal(255, (byte)Language.None);

        // TaggedValue ASCII-coded tunnel/bridge values
        Assert.Equal((byte)'1', (byte)TaggedValue.Tunnel);
        Assert.Equal((byte)'2', (byte)TaggedValue.Bridge);

        // PronunciationAlphabet None deliberately == 5 (0 deprecated)
        Assert.Equal(5, (byte)PronunciationAlphabet.None);
    }

    [Theory]
    [InlineData(RoadClass.Motorway, "motorway")]
    [InlineData(RoadClass.ServiceOther, "service_other")]
    [InlineData(RoadClass.Residential, "residential")]
    public void RoadClassToStringMatchesCpp(RoadClass rc, string expected)
        => Assert.Equal(expected, GraphConstants.ToStringValue(rc));

    [Theory]
    [InlineData("Motorway", RoadClass.Motorway)]
    [InlineData("ServiceOther", RoadClass.ServiceOther)]
    public void StringToRoadClassMatchesCpp(string s, RoadClass expected)
        => Assert.Equal(expected, GraphConstants.StringToRoadClass(s));

    [Theory]
    [InlineData(Use.DriveThru, "drive_through")]
    [InlineData(Use.RailFerry, "rail-ferry")]
    [InlineData(Use.TransitConnection, "transit_connection")]
    public void UseToStringMatchesCpp(Use u, string expected)
        => Assert.Equal(expected, GraphConstants.ToStringValue(u));

    [Theory]
    [InlineData("en", Language.En)]
    [InlineData("sr-Latn", Language.SrLatn)]
    [InlineData("none", Language.None)]
    [InlineData("zzz-unknown", Language.None)] // unknown -> kNone, matching C++
    public void StringLanguageMatchesCpp(string s, Language expected)
        => Assert.Equal(expected, GraphConstants.StringLanguage(s));

    [Fact]
    public void AccessRestrictionMasksMatchCpp()
    {
        var masks = GraphConstants.AccessRestrictionMasks;
        Assert.Equal(32, masks.Length);
        Assert.Equal((byte)GraphConstants.HazmatMask, masks[(int)AccessType.Hazmat]);
        Assert.Equal((byte)GraphConstants.MaxHeightMask, masks[(int)AccessType.MaxHeight]);
        Assert.Equal((byte)GraphConstants.MaxWidthMask, masks[(int)AccessType.MaxWidth]);
        Assert.Equal((byte)GraphConstants.MaxLengthMask, masks[(int)AccessType.MaxLength]);
        Assert.Equal((byte)GraphConstants.MaxWeightMask, masks[(int)AccessType.MaxWeight]);
        Assert.Equal((byte)GraphConstants.MaxAxleLoadMask, masks[(int)AccessType.MaxAxleLoad]);
        Assert.Equal((byte)GraphConstants.MaxAxlesMask, masks[(int)AccessType.MaxAxles]);
        // Slots without a mask remain zero (e.g. kTimedAllowed = 6).
        Assert.Equal(0, masks[(int)AccessType.TimedAllowed]);
    }

    [Theory]
    // rc < 2 -> *1.6 ; rc 2..4 -> *1.4 ; else *1.0 ; pedestrian-ish use -> *0.5
    [InlineData(RoadClass.Motorway, Use.Road, 15.0f * 1.6f)]
    [InlineData(RoadClass.Primary, Use.Road, 15.0f * 1.4f)]
    [InlineData(RoadClass.Residential, Use.Road, 15.0f)]
    [InlineData(RoadClass.Motorway, Use.Footway, 15.0f * 1.6f * 0.5f)]
    public void GetOffsetForHeadingMatchesCpp(RoadClass rc, Use use, float expected)
        => Assert.Equal(expected, GraphConstants.GetOffsetForHeading(rc, use));
}
