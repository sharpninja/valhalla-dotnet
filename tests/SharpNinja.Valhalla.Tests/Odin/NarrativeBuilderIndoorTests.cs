// Tests for the ported odin NarrativeBuilder INDOOR / level-change / pass WRITTEN maneuver
// instructions (en-US) - slice A3.
//
// These assert the exact maneuver.Instruction() strings produced by the written FormXInstruction
// path for the indoor / level-change / pass maneuver families (elevator, stairs, escalator, enter /
// exit building, level change, park vehicle, pass), derived from the en-US.json phrase templates and
// the upstream src/odin/narrativebuilder.cc oracle. A handful of cases also lock the Build() verbal
// wiring (elevator / steps set the verbal pre-transition instruction to the written instruction). A
// bike-share verification test confirms the FormBssManeuverType prefix (A1) rides on the start
// instruction. The TRANSIT maneuver families are out of scope for this port (see the DEFER PORT-NOTE
// in NarrativeBuilder.cs) and are not exercised here.

using System.Collections.Generic;

using SharpNinja.Valhalla.Baldr;
using SharpNinja.Valhalla.Odin;
using SharpNinja.Valhalla.Sif;

namespace SharpNinja.Valhalla.Tests.Odin;

public class NarrativeBuilderIndoorTests
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

    // ----- Elevator -----

    [Fact]
    public void Elevator_NoLevel()
    {
        Maneuver m = NewManeuver(DirectionsLegManeuverType.ElevatorEnter);

        Run(m);

        Assert.Equal("Take the elevator.", m.Instruction());
    }

    [Fact]
    public void Elevator_WithLevel()
    {
        Maneuver m = NewManeuver(DirectionsLegManeuverType.ElevatorEnter);
        m.SetEndLevelRef("Level 1");

        Run(m);

        Assert.Equal("Take the elevator to Level 1.", m.Instruction());
    }

    [Fact]
    public void Elevator_WithElevatorNodeType_SetsVerbalInstructions()
    {
        Maneuver m = NewManeuver(DirectionsLegManeuverType.ElevatorEnter);
        m.SetEndLevelRef("Level 1");
        m.SetNodeType(NodeType.Elevator);

        Run(m);

        Assert.Equal("Take the elevator to Level 1.", m.Instruction());

        // Build() sets the verbal transition-alert and pre-transition instructions to the written
        // instruction only when the node is an elevator.
        Assert.Equal("Take the elevator to Level 1.", m.VerbalTransitionAlertInstruction());
        Assert.Equal("Take the elevator to Level 1.", m.VerbalPreTransitionInstruction());
    }

    // ----- Steps -----

    [Fact]
    public void Steps_NoLevel()
    {
        Maneuver m = NewManeuver(DirectionsLegManeuverType.StepsEnter);

        Run(m);

        Assert.Equal("Take the stairs.", m.Instruction());
    }

    [Fact]
    public void Steps_WithLevel()
    {
        Maneuver m = NewManeuver(DirectionsLegManeuverType.StepsEnter);
        m.SetEndLevelRef("Level 2");

        Run(m);

        Assert.Equal("Take the stairs to Level 2.", m.Instruction());
    }

    [Fact]
    public void Steps_SetsVerbalInstructions()
    {
        Maneuver m = NewManeuver(DirectionsLegManeuverType.StepsEnter);
        m.SetEndLevelRef("Level 2");

        Run(m);

        // Build() always sets the verbal transition-alert and pre-transition instructions for steps.
        Assert.Equal("Take the stairs to Level 2.", m.VerbalTransitionAlertInstruction());
        Assert.Equal("Take the stairs to Level 2.", m.VerbalPreTransitionInstruction());
    }

    // ----- Escalator -----

    [Fact]
    public void Escalator_NoLevel()
    {
        Maneuver m = NewManeuver(DirectionsLegManeuverType.EscalatorEnter);

        Run(m);

        Assert.Equal("Take the escalator.", m.Instruction());
    }

    [Fact]
    public void Escalator_WithLevel()
    {
        Maneuver m = NewManeuver(DirectionsLegManeuverType.EscalatorEnter);
        m.SetEndLevelRef("Level 3");

        Run(m);

        Assert.Equal("Take the escalator to Level 3.", m.Instruction());
    }

    // ----- Enter building -----

    [Fact]
    public void EnterBuilding_NoStreet()
    {
        Maneuver m = NewManeuver(DirectionsLegManeuverType.BuildingEnter);

        Run(m);

        Assert.Equal("Enter the building.", m.Instruction());
    }

    [Fact]
    public void EnterBuilding_WithStreet()
    {
        Maneuver m = NewManeuver(DirectionsLegManeuverType.BuildingEnter, new[] { ("Main Street", false) });

        Run(m);

        Assert.Equal("Enter the building, and continue on Main Street.", m.Instruction());
    }

    // ----- Exit building -----

    [Fact]
    public void ExitBuilding_NoStreet()
    {
        Maneuver m = NewManeuver(DirectionsLegManeuverType.BuildingExit);

        Run(m);

        Assert.Equal("Exit the building.", m.Instruction());
    }

    [Fact]
    public void ExitBuilding_WithStreet()
    {
        Maneuver m = NewManeuver(DirectionsLegManeuverType.BuildingExit, new[] { ("Broadway", false) });

        Run(m);

        Assert.Equal("Exit the building, and continue on Broadway.", m.Instruction());
    }

    // ----- Generic level change -----

    [Fact]
    public void LevelChange_WithLevel()
    {
        Maneuver m = NewManeuver(DirectionsLegManeuverType.LevelChange);
        m.SetEndLevelRef("Level 2");

        Run(m);

        Assert.Equal("Continue to Level 2.", m.Instruction());
    }

    // ----- Park vehicle -----

    [Fact]
    public void ParkVehicle()
    {
        Maneuver m = NewManeuver(DirectionsLegManeuverType.ParkVehicle);

        Run(m);

        // The en-US.json park_vehicle phrase "0" is "Park your vehicle" (no trailing period).
        Assert.Equal("Park your vehicle", m.Instruction());
    }

    // ----- Pass (Continue maneuver carrying a node_type) -----

    [Fact]
    public void Pass_Gate()
    {
        Maneuver m = NewManeuver(DirectionsLegManeuverType.Continue);
        m.SetNodeType(NodeType.Gate);

        Run(m);

        Assert.Equal("Pass the gate.", m.Instruction());

        // The pass arm also sets the verbal pre-transition instruction to the written instruction.
        Assert.Equal("Pass the gate.", m.VerbalPreTransitionInstruction());
    }

    [Fact]
    public void Pass_Bollard()
    {
        Maneuver m = NewManeuver(DirectionsLegManeuverType.Continue);
        m.SetNodeType(NodeType.Bollard);

        Run(m);

        Assert.Equal("Pass the bollards.", m.Instruction());
    }

    [Fact]
    public void Pass_StreetIntersection_TrafficSignal()
    {
        Maneuver m = NewManeuver(DirectionsLegManeuverType.Continue);
        m.SetNodeType(NodeType.StreetIntersection);
        m.SetTrafficSignal(true);

        Run(m);

        Assert.Equal("Pass traffic signals on ways intersection.", m.Instruction());
    }

    [Fact]
    public void Pass_StreetIntersection_CrossStreetNames_NoSignal()
    {
        Maneuver m = NewManeuver(DirectionsLegManeuverType.Continue);
        m.SetNodeType(NodeType.StreetIntersection);
        m.SetCrossStreetNames(new[] { ("5th Avenue", false) });

        Run(m);

        Assert.Equal("Pass 5th Avenue.", m.Instruction());
    }

    // ----- Bike-share prefix verification (A1 FormBssManeuverType) -----

    [Fact]
    public void BikeShare_RentPrefix_OnStart()
    {
        Maneuver m = NewManeuver(DirectionsLegManeuverType.Start);
        m.SetTravelMode(TravelMode.Bicycle);
        m.SetBeginCardinalDirection(DirectionsLegManeuverCardinalDirection.East);
        m.SetBssManeuverType(DirectionsLegManeuverBssManeuverType.RentBikeAtBikeShare);

        Run(m);

        Assert.Equal("Then rent a bike at BSS. Bike east.", m.Instruction());
    }

    [Fact]
    public void BikeShare_ReturnPrefix_OnStart()
    {
        Maneuver m = NewManeuver(DirectionsLegManeuverType.Start);
        m.SetTravelMode(TravelMode.Bicycle);
        m.SetBeginCardinalDirection(DirectionsLegManeuverCardinalDirection.East);
        m.SetBssManeuverType(DirectionsLegManeuverBssManeuverType.ReturnBikeAtBikeShare);

        Run(m);

        Assert.Equal("Then return the bike to BSS. Bike east.", m.Instruction());
    }
}
