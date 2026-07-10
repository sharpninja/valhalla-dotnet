// Tests for the ported odin NarrativeBuilder VERBAL maneuver instructions (driving, en-US).
//
// These assert the exact verbal strings produced by Build() (verbal pre / alert / succinct / post),
// the verbal multi-cue post-pass, and the US verbal text formatter expansion. Expected strings are
// derived from the en-US.json *_verbal example_phrases and the upstream test/narrativebuilder.cc
// oracle (verbal delimiter ", ", US route-number expansion). Non-driving families and non-en-US
// locales remain OUT OF SCOPE for this slice.

using System.Collections.Generic;

using SharpNinja.Valhalla.Baldr;
using SharpNinja.Valhalla.Odin;
using SharpNinja.Valhalla.Sif;

namespace SharpNinja.Valhalla.Tests.Odin;

public class NarrativeBuilderVerbalTests
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

    private static Maneuver NewManeuver(
        DirectionsLegManeuverType type,
        IEnumerable<(string, bool)>? streetNames = null,
        float lengthKm = 0.0f)
    {
        var m = new Maneuver();
        m.SetType(type);
        m.SetTravelMode(TravelMode.Drive);
        m.SetLength(lengthKm);
        if (streetNames != null)
        {
            m.SetStreetNames(streetNames);
        }

        return m;
    }

    // -------------------------------------------------------------------------------------------
    // Verbal pre / alert / succinct / post per driving maneuver family
    // -------------------------------------------------------------------------------------------

    [Fact]
    public void Start_Drive_East_MainStreet()
    {
        Maneuver m = NewManeuver(DirectionsLegManeuverType.Start, new[] { ("Main Street", false) }, 0.5f);
        m.SetBeginCardinalDirection(DirectionsLegManeuverCardinalDirection.East);

        Run(m);

        Assert.Equal("Drive east on Main Street.", m.VerbalPreTransitionInstruction());
        Assert.Equal("Drive east.", m.VerbalSuccinctTransitionInstruction());
        Assert.Equal("Continue for 500 meters.", m.VerbalPostTransitionInstruction());
    }

    [Fact]
    public void Start_Drive_East_WithLength()
    {
        Maneuver m = NewManeuver(DirectionsLegManeuverType.Start, new[] { ("Main Street", false) }, 0.5f);
        m.SetBeginCardinalDirection(DirectionsLegManeuverCardinalDirection.East);
        m.SetIncludeVerbalPreTransitionLength(true);

        Run(m);

        Assert.Equal("Drive east on Main Street for 500 meters.", m.VerbalPreTransitionInstruction());
        Assert.Equal("Drive east for 500 meters.", m.VerbalSuccinctTransitionInstruction());
    }

    [Fact]
    public void Continue_WithStreet_AndLength()
    {
        Maneuver m = NewManeuver(DirectionsLegManeuverType.Continue, new[] { ("10th Avenue", false) }, 3.0f);
        m.SetIncludeVerbalPreTransitionLength(true);

        Run(m);

        Assert.Equal("Continue on 10th Avenue for 3 kilometers.", m.VerbalPreTransitionInstruction());
        Assert.Equal("Continue on 10th Avenue.", m.VerbalTransitionAlertInstruction());
        Assert.Equal("Continue for 3 kilometers.", m.VerbalPostTransitionInstruction());
    }

    [Fact]
    public void TurnLeft_OntoStreet()
    {
        Maneuver m = NewManeuver(DirectionsLegManeuverType.Left, new[] { ("Flatbush Avenue", false) }, 0.2f);

        Run(m);

        Assert.Equal("Turn left onto Flatbush Avenue.", m.VerbalPreTransitionInstruction());
        Assert.Equal("Turn left onto Flatbush Avenue.", m.VerbalTransitionAlertInstruction());
        Assert.Equal("Turn left.", m.VerbalSuccinctTransitionInstruction());
        Assert.Equal("Continue for 200 meters.", m.VerbalPostTransitionInstruction());
    }

    [Fact]
    public void UturnLeft_OntoStreet()
    {
        Maneuver m = NewManeuver(DirectionsLegManeuverType.UturnLeft, new[] { ("Bunker Hill Road", false) }, 0.2f);

        Run(m);

        Assert.Equal("Make a left U-turn onto Bunker Hill Road.", m.VerbalPreTransitionInstruction());
        Assert.Equal("Make a left U-turn onto Bunker Hill Road.", m.VerbalTransitionAlertInstruction());
        Assert.Equal("Make a left U-turn.", m.VerbalSuccinctTransitionInstruction());
    }

    [Fact]
    public void Merge_TwoStreetNames_RawNames()
    {
        Maneuver m = NewManeuver(
            DirectionsLegManeuverType.Merge,
            new[] { ("I 76 West", false), ("Pennsylvania Turnpike", false) },
            1.0f);

        Run(m);

        // Verbal delimiter is ", " (not the written "/") and max 2 elements.
        Assert.Equal("Merge onto I 76 West, Pennsylvania Turnpike.", m.VerbalPreTransitionInstruction());
        Assert.Equal("Merge.", m.VerbalSuccinctTransitionInstruction());
        Assert.Equal("Continue for 1 kilometer.", m.VerbalPostTransitionInstruction());
    }

    [Fact]
    public void Merge_TwoStreetNames_UsFormatterExpandsRouteNumbers()
    {
        Maneuver m = NewManeuver(
            DirectionsLegManeuverType.Merge,
            new[] { ("I 76 West", false), ("Pennsylvania Turnpike", false) },
            1.0f);
        m.SetVerbalFormatter(new VerbalTextFormatterUs("US", "PA"));

        Run(m);

        Assert.Equal("Merge onto Interstate 76 West, Pennsylvania Turnpike.", m.VerbalPreTransitionInstruction());
    }

    [Fact]
    public void EnterRoundabout_Exit2()
    {
        Maneuver m = NewManeuver(DirectionsLegManeuverType.RoundaboutEnter);
        m.SetRoundaboutExitCount(2);

        Run(m);

        Assert.Equal("Enter the roundabout and take the 2nd exit.", m.VerbalPreTransitionInstruction());
        Assert.Equal("Enter the roundabout and take the 2nd exit.", m.VerbalSuccinctTransitionInstruction());
    }

    [Fact]
    public void ExitRoundabout_OntoStreet()
    {
        Maneuver m = NewManeuver(
            DirectionsLegManeuverType.RoundaboutExit,
            new[] { ("Philadelphia Road", false), ("MD 7", false) },
            0.2f);

        Run(m);

        Assert.Equal("Exit the roundabout onto Philadelphia Road, MD 7.", m.VerbalPreTransitionInstruction());
        Assert.Equal("Exit the roundabout.", m.VerbalSuccinctTransitionInstruction());
    }

    [Fact]
    public void EnterFerry_Named()
    {
        Maneuver m = NewManeuver(DirectionsLegManeuverType.FerryEnter, new[] { ("Millersburg Ferry", false) }, 1.0f);

        Run(m);

        Assert.Equal("Take the Millersburg Ferry.", m.VerbalPreTransitionInstruction());
        Assert.Equal("Take the Millersburg Ferry.", m.VerbalTransitionAlertInstruction());
        Assert.Equal("Continue for 1 kilometer.", m.VerbalPostTransitionInstruction());
    }

    // -------------------------------------------------------------------------------------------
    // Verbal multi-cue post-pass
    // -------------------------------------------------------------------------------------------

    [Fact]
    public void VerbalMultiCue_Imminent_CombinesTwoManeuvers()
    {
        Maneuver prev = NewManeuver(DirectionsLegManeuverType.Left, new[] { ("North Plum Street", false) }, 0.16f);
        Maneuver curr = NewManeuver(DirectionsLegManeuverType.Right, new[] { ("East Fulton Street", false) });

        Run(prev, curr);

        Assert.Equal(
            "Turn left onto North Plum Street. Then Turn right onto East Fulton Street.",
            prev.VerbalPreTransitionInstruction());
    }

    [Fact]
    public void VerbalMultiCue_Distant_IncludesLength()
    {
        Maneuver prev = NewManeuver(DirectionsLegManeuverType.Left, new[] { ("North Plum Street", false) }, 0.16f);
        prev.SetHasRightTraversableOutboundIntersectingEdge(true);
        Maneuver curr = NewManeuver(DirectionsLegManeuverType.Right, new[] { ("East Fulton Street", false) });

        Run(prev, curr);

        Assert.Equal(
            "Turn left onto North Plum Street. Then, in 200 meters, Turn right onto East Fulton Street.",
            prev.VerbalPreTransitionInstruction());
    }

    // -------------------------------------------------------------------------------------------
    // US verbal text formatter (number / street expansion)
    // -------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("US 322", "U.S. 3 22")]
    [InlineData("I 95 South", "Interstate 95 South")]
    [InlineData("10th Avenue", "10th Avenue")]
    public void VerbalTextFormatterUs_Expands(string input, string expected)
        => Assert.Equal(expected, new VerbalTextFormatterUs("US", "PA").Format(input));
}
