// Tests for the ported odin NarrativeBuilder WRITTEN maneuver instructions (driving, en-US).
//
// These assert exact maneuver.Instruction() strings produced by the written FormXInstruction path,
// derived from the en-US.json phrase templates and the upstream test/narrativebuilder.cc oracle. The
// verbal_* families, non-en-US locales, FormLength, transit/pedestrian/indoor maneuver prose, and the
// depart/arrive strings are OUT OF SCOPE for this slice and are not exercised here.

using System.Collections.Generic;

using SharpNinja.Valhalla.Odin;
using SharpNinja.Valhalla.Sif;

namespace SharpNinja.Valhalla.Tests.Odin;

public class NarrativeBuilderTests
{
    private static readonly NarrativeDictionary Dict = NarrativeDictionaryLoader.Get("en-US");

    // Runs the narrative builder over the supplied maneuvers (in order) so the dispatch, the written
    // FormXInstruction methods, and the bss-maneuver-type prefix all execute exactly as in Build().
    private static void Run(params Maneuver[] maneuvers)
    {
        var list = new LinkedList<Maneuver>();
        foreach (Maneuver m in maneuvers)
        {
            list.AddLast(m);
        }

        NarrativeBuilderFactory.Create(new Options(), null, Dict).Build(list);
    }

    private static Maneuver NewManeuver(
        DirectionsLegManeuverType type,
        IEnumerable<(string, bool)>? streetNames = null)
    {
        var m = new Maneuver();
        m.SetType(type);
        m.SetTravelMode(TravelMode.Drive);
        if (streetNames != null)
        {
            m.SetStreetNames(streetNames);
        }

        return m;
    }

    [Fact]
    public void Start_Drive_East_MainStreet()
    {
        Maneuver m = NewManeuver(DirectionsLegManeuverType.Start, new[] { ("Main Street", false) });
        m.SetBeginCardinalDirection(DirectionsLegManeuverCardinalDirection.East);

        Run(m);

        Assert.Equal("Drive east on Main Street.", m.Instruction());
    }

    [Fact]
    public void Start_Drive_East_NoStreets()
    {
        Maneuver m = NewManeuver(DirectionsLegManeuverType.Start);
        m.SetBeginCardinalDirection(DirectionsLegManeuverCardinalDirection.East);

        Run(m);

        Assert.Equal("Drive east.", m.Instruction());
    }

    [Fact]
    public void Continue_NoStreets()
    {
        Maneuver m = NewManeuver(DirectionsLegManeuverType.Continue);

        Run(m);

        Assert.Equal("Continue.", m.Instruction());
    }

    [Fact]
    public void Continue_WithStreet()
    {
        Maneuver m = NewManeuver(DirectionsLegManeuverType.Continue, new[] { ("10th Avenue", false) });

        Run(m);

        Assert.Equal("Continue on 10th Avenue.", m.Instruction());
    }

    [Fact]
    public void SharpLeft_NoStreet()
    {
        Maneuver m = NewManeuver(DirectionsLegManeuverType.SharpLeft);

        Run(m);

        Assert.Equal("Make a sharp left.", m.Instruction());
    }

    [Fact]
    public void TurnLeft_OntoStreet()
    {
        Maneuver m = NewManeuver(DirectionsLegManeuverType.Left, new[] { ("Flatbush Avenue", false) });

        Run(m);

        Assert.Equal("Turn left onto Flatbush Avenue.", m.Instruction());
    }

    [Fact]
    public void TurnRight_OntoStreet()
    {
        Maneuver m = NewManeuver(DirectionsLegManeuverType.Right, new[] { ("Flatbush Avenue", false) });

        Run(m);

        Assert.Equal("Turn right onto Flatbush Avenue.", m.Instruction());
    }

    [Fact]
    public void BearRight_NoStreet()
    {
        Maneuver m = NewManeuver(DirectionsLegManeuverType.SlightRight);

        Run(m);

        Assert.Equal("Bear right.", m.Instruction());
    }

    [Fact]
    public void UturnLeft_NoStreet()
    {
        Maneuver m = NewManeuver(DirectionsLegManeuverType.UturnLeft);

        Run(m);

        Assert.Equal("Make a left U-turn.", m.Instruction());
    }

    [Fact]
    public void UturnLeft_OntoStreet()
    {
        Maneuver m = NewManeuver(DirectionsLegManeuverType.UturnLeft, new[] { ("Bunker Hill Road", false) });

        Run(m);

        Assert.Equal("Make a left U-turn onto Bunker Hill Road.", m.Instruction());
    }

    [Fact]
    public void KeepAtFork_StayRight()
    {
        Maneuver m = NewManeuver(DirectionsLegManeuverType.StayRight);

        Run(m);

        Assert.Equal("Keep right at the fork.", m.Instruction());
    }

    [Fact]
    public void KeepToStayOn_StayLeft()
    {
        Maneuver m = NewManeuver(DirectionsLegManeuverType.StayLeft, new[] { ("I 95 South", false) });
        m.SetToStayOn(true);

        Run(m);

        Assert.Equal("Keep left to stay on I 95 South.", m.Instruction());
    }

    [Fact]
    public void Exit_WithNumber()
    {
        Maneuver m = NewManeuver(DirectionsLegManeuverType.ExitRight);
        // drive_on_right defaults to true, which matches ExitRight -> phrase base 15.
        m.MutableSigns().MutableExitNumberList().Add(new OdinSign("67 B-A", false));

        Run(m);

        Assert.Equal("Take exit 67 B-A.", m.Instruction());
    }

    [Fact]
    public void Merge_TwoStreetNames()
    {
        Maneuver m = NewManeuver(
            DirectionsLegManeuverType.Merge,
            new[] { ("I 76 West", false), ("Pennsylvania Turnpike", false) });

        Run(m);

        Assert.Equal("Merge onto I 76 West/Pennsylvania Turnpike.", m.Instruction());
    }

    [Fact]
    public void MergeRight_OntoStreet()
    {
        Maneuver m = NewManeuver(DirectionsLegManeuverType.MergeRight, new[] { ("I 83 South", false) });

        Run(m);

        Assert.Equal("Merge right onto I 83 South.", m.Instruction());
    }

    [Fact]
    public void EnterRoundabout_Exit2_NoStreets()
    {
        Maneuver m = NewManeuver(DirectionsLegManeuverType.RoundaboutEnter);
        m.SetRoundaboutExitCount(2);

        Run(m);

        Assert.Equal("Enter the roundabout and take the 2nd exit.", m.Instruction());
    }

    [Fact]
    public void EnterRoundabout_NoExitCount()
    {
        Maneuver m = NewManeuver(DirectionsLegManeuverType.RoundaboutEnter);

        Run(m);

        Assert.Equal("Enter the roundabout.", m.Instruction());
    }

    [Fact]
    public void ExitRoundabout_OntoStreet()
    {
        Maneuver m = NewManeuver(
            DirectionsLegManeuverType.RoundaboutExit,
            new[] { ("Philadelphia Road", false), ("MD 7", false) });

        Run(m);

        Assert.Equal("Exit the roundabout onto Philadelphia Road/MD 7.", m.Instruction());
    }

    [Fact]
    public void EnterFerry_Named()
    {
        Maneuver m = NewManeuver(DirectionsLegManeuverType.FerryEnter, new[] { ("Millersburg Ferry", false) });

        Run(m);

        Assert.Equal("Take the Millersburg Ferry.", m.Instruction());
    }

    [Fact]
    public void Destination_NoNameOrStreet()
    {
        Maneuver m = NewManeuver(DirectionsLegManeuverType.Destination);

        Run(m);

        Assert.Equal("You have arrived at your destination.", m.Instruction());
    }

    [Fact]
    public void Becomes_UsesPreviousManeuverStreetNames()
    {
        Maneuver prev = NewManeuver(DirectionsLegManeuverType.Continue, new[] { ("Vine Street", false) });
        Maneuver m = NewManeuver(DirectionsLegManeuverType.Becomes, new[] { ("Middletown Road", false) });

        Run(prev, m);

        Assert.Equal("Vine Street becomes Middletown Road.", m.Instruction());
    }
}
