// Street-name handling tests for the ported odin NarrativeBuilder WRITTEN instruction path.
//
// Covers the shared FormStreetNames helper: multiple names join with "/", begin + street names drive
// the "Continue on" phrase variants, and empty street names are enhanced to the empty_street_name
// labels when (and only when) the phrase enhances them. Start uses the cardinal direction only (no
// street-name enhancement), which is covered by NarrativeBuilderTests.Start_Drive_East_NoStreets.

using System.Collections.Generic;

using SharpNinja.Valhalla.Odin;
using SharpNinja.Valhalla.Sif;

namespace SharpNinja.Valhalla.Tests.Odin;

public class NarrativeBuilderStreetNameTests
{
    private static readonly NarrativeDictionary Dict = NarrativeDictionaryLoader.Get("en-US");

    private static void Run(params Maneuver[] maneuvers)
    {
        var list = new LinkedList<Maneuver>();
        foreach (Maneuver m in maneuvers)
        {
            list.AddLast(m);
        }

        NarrativeBuilderFactory.Create(new Options(), null, Dict).Build(list);
    }

    [Fact]
    public void MultipleStreetNames_JoinWithSlash()
    {
        var m = new Maneuver();
        m.SetType(DirectionsLegManeuverType.Left);
        m.SetTravelMode(TravelMode.Drive);
        m.SetStreetNames(new[] { ("A Street", false), ("B Road", false) });

        Run(m);

        Assert.Equal("Turn left onto A Street/B Road.", m.Instruction());
    }

    [Fact]
    public void Start_BeginAndStreetNames_UseContinuePhrase()
    {
        var m = new Maneuver();
        m.SetType(DirectionsLegManeuverType.Start);
        m.SetTravelMode(TravelMode.Drive);
        m.SetBeginCardinalDirection(DirectionsLegManeuverCardinalDirection.East);
        m.SetBeginStreetNames(new[] { ("First Avenue", false) });
        m.SetStreetNames(new[] { ("Second Avenue", false) });

        Run(m);

        Assert.Equal("Drive east on First Avenue. Continue on Second Avenue.", m.Instruction());
    }

    [Fact]
    public void ExitRoundabout_BeginAndStreetNames_UseContinuePhrase()
    {
        var m = new Maneuver();
        m.SetType(DirectionsLegManeuverType.RoundaboutExit);
        m.SetTravelMode(TravelMode.Drive);
        m.SetBeginStreetNames(new[] { ("Catoctin Mountain Highway", false), ("US 15", false) });
        m.SetStreetNames(new[] { ("US 15", false) });

        Run(m);

        Assert.Equal("Exit the roundabout onto Catoctin Mountain Highway/US 15. Continue on US 15.", m.Instruction());
    }

    [Fact]
    public void EnterRoundabout_Exit2_WithExitStreetNames()
    {
        var m = new Maneuver();
        m.SetType(DirectionsLegManeuverType.RoundaboutEnter);
        m.SetTravelMode(TravelMode.Drive);
        m.SetRoundaboutExitCount(2);
        m.SetRoundaboutExitStreetNames(new[] { ("Philadelphia Road", false), ("MD 7", false) });

        Run(m);

        Assert.Equal("Enter the roundabout and take the 2nd exit onto Philadelphia Road/MD 7.", m.Instruction());
    }

    [Fact]
    public void EmptyStreetNames_EnhancedToWalkwayLabel()
    {
        // The shared FormStreetNames enhancement fills empty street names with the walkway label for
        // a pedestrian maneuver on an unnamed footway. Exercised through Continue, whose phrase id is
        // unaffected by travel mode, so the enhanced label alone drives the "Continue on ..." phrase.
        var m = new Maneuver();
        m.SetType(DirectionsLegManeuverType.Continue);
        m.SetTravelMode(TravelMode.Pedestrian);
        m.SetTrailType(TrailType.UnnamedWalkway);

        Run(m);

        Assert.Equal("Continue on the walkway.", m.Instruction());
    }
}
