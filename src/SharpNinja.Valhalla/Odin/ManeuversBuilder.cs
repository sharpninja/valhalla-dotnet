// Faithful C# port of Valhalla odin ManeuversBuilder
// (valhalla/odin/maneuversbuilder.h + src/odin/maneuversbuilder.cc) @ 3.7.0.
// Source: valhalla/odin/maneuversbuilder.h, src/odin/maneuversbuilder.cc
//
// Turns an EnhancedTripLeg (wrapping the ported Thor TripLeg) into an ordered list of Maneuvers.
// The maneuver Produce / Combine passes, relative-direction + turn-type logic, roundabout / fork /
// tee / internal-intersection / turn-channel handling, and sign assignment are ported verbatim.
// Public members are PascalCase; every threshold, comparison, and control-flow branch mirrors the
// C++ exactly.
//
// PORT-NOTE (DEFER): narrativebuilder.cc + narrative_dictionary.cc (localized prose text) are NOT
// ported - only maneuver STRUCTURE is produced. The prose string fields on Maneuver stay empty.
//
// PORT-NOTE (DEFER): transit support (TransitRouteInfo / TransitPlatformInfo, the transit_info /
// transit_connection_platform_info objects, InsertTransitStop, and the transit block_id/trip_id
// comparisons) belongs to the EXCLUDED transit module. The ported Thor TripEdge does not carry
// transit route info, and the foundation Maneuver omits the transit info objects. Branches that
// would read transit_info() are therefore reduced to the structural transit-mode predicates that the
// foundation does carry (travel mode == PublicTransit, IsTransit(), transit_connection()). The
// transit-remain-on vs. transit-transfer distinction (which needs block_id/trip_id) collapses to
// kTransitTransfer, and CanManeuverIncludePrevEdge's transit block/trip combine test - which needs
// the same ids - is treated as "not combinable" (the conservative branch). These transit code paths
// are not reached by the ported gtest cases (all drive-mode).
//
// PORT-NOTE (DEFER): the bike-share (bss_info) objects, RouteLandmark landmarks, guidance-view
// junction/signboard image matching that reads osm_changeset + per-edge guidance_view_junctions /
// guidance_view_signboards (a sign sub-list the ported Thor TripSign does not carry), the
// per-maneuver verbal-succinct long-street-name pass (prose-adjacent, harmless to keep), and the
// LOGGING_LEVEL_TRACE / LOGGING_LEVEL_DEBUG emitters are omitted. The structural Build() pipeline
// (Produce, Combine, CountAndSortSigns, ConfirmManeuverTypeAssignment,
// SetTraversableOutboundIntersectingEdgeFlags, ProcessRoundabouts, SetToStayOnAttribute,
// EnhanceSignlessInterchnages, UpdateManeuverPlacementForInternalIntersectionTurns,
// CollapseSmallEndRampFork, CollapseMergeManeuvers, ProcessVerbalSuccinctTransitionInstruction) is
// ported in full. ProcessGuidanceViews / ProcessTurnLanes are ported using the data the ported Thor
// types carry (turn-lane masks); guidance-view image matching is a no-op when the sign sub-lists are
// absent.

using System;
using System.Collections.Generic;
using System.Linq;

using SharpNinja.Valhalla.Baldr;
using SharpNinja.Valhalla.Midgard;
using SharpNinja.Valhalla.Sif;
using SharpNinja.Valhalla.Thor;

namespace SharpNinja.Valhalla.Odin;

/// <summary>
/// Builds the maneuver list based on the specified directions options and enhanced trip path.
/// Faithful port of <c>valhalla::odin::ManeuversBuilder</c>.
/// </summary>
public class ManeuversBuilder
{
    // Anonymous-namespace constants from maneuversbuilder.cc.
    private const uint RelativeStraightTurnDegreeLowerBound = 330;
    private const uint RelativeStraightTurnDegreeUpperBound = 30;

    private const uint TurnChannelTurnDegreeLowerBound = 290;
    private const uint TurnChannelTurnDegreeUpperBound = 70;

    private const float ShortTurnChannelThreshold = 0.036f; // Kilometers

    // private const float ShortForkThreshold = 0.05f; // Kilometers - used only in ProcessTurnLanes

    // Kilometers - picked since the next rounded maneuver announcement will happen
    // in a quarter mile or 400 meters
    private const float ShortContinueThreshold = 0.6f;

    // Maximum number of edges to look for matching overlay (guidance views - deferred).
    // private const uint OverlaySignBoardEdgeMax = 5;

    private const float UpcomingLanesThreshold = 3.0f; // Kilometers

    // Small end ramp fork threshold in kilometers
    private const float SmallEndRampForkThreshold = 0.125f;

    private const float ShortForkThreshold = 0.05f; // Kilometers

    // Thresholds for succinct phrase usage
    private const uint MaxWordCount = 5;
    private const uint MaxStreetNameLength = 25;

    private readonly Options _options;
    private readonly EnhancedTripLeg? _tripPath;

    /// <summary>
    /// Constructor that assigns the specified directions options and trip path. Faithful port of
    /// <c>ManeuversBuilder(const Options&amp;, EnhancedTripLeg*)</c>.
    /// </summary>
    /// <param name="options">The directions options such as units and language.</param>
    /// <param name="tripPath">The trip path - list of nodes, edges, attributes and shape.</param>
    public ManeuversBuilder(Options options, EnhancedTripLeg? tripPath)
    {
        _options = options;
        _tripPath = tripPath;
    }

    private EnhancedTripLeg TripPath => _tripPath!;

    /// <summary>Builds the maneuver list. Faithful port of <c>Build()</c>.</summary>
    public LinkedList<Maneuver> Build()
    {
        // Create the maneuvers
        LinkedList<Maneuver> maneuvers = Produce();

        // Combine maneuvers
        Combine(maneuvers);

        // Calculate the consecutive exit sign count and then sort
        CountAndSortSigns(maneuvers);

        // Confirm maneuver type assignment
        ConfirmManeuverTypeAssignment(maneuvers);

        // Mark the maneuvers that have traversable outbound intersecting edges
        SetTraversableOutboundIntersectingEdgeFlags(maneuvers);

        // Process roundabouts
        ProcessRoundabouts(maneuvers);

        // Process the 'to stay on' attribute
        SetToStayOnAttribute(maneuvers);

        // Enhance signless interchanges
        EnhanceSignlessInterchnages(maneuvers);

        // Process the guidance view junctions and signboards
        ProcessGuidanceViews(maneuvers);

        // Update the maneuver placement for internal intersection turns
        UpdateManeuverPlacementForInternalIntersectionTurns(maneuvers);

        // Collapse small end ramp fork maneuvers to reduce verbose instructions
        // Must happen after updating maneuver placement for internal edges
        CollapseSmallEndRampFork(maneuvers);

        // Collapse merge maneuvers to reduce obvious instructions
        CollapseMergeManeuvers(maneuvers);

        // Process the turn lanes. Must happen after updating maneuver placement for internal edges so
        // we activate the correct lanes.
        ProcessTurnLanes(maneuvers);

        // Add landmarks to maneuvers as direction guidance support
        // PORT-NOTE (DEFER): AddLandmarksFromTripLegToManeuvers needs RouteLandmark data the ported
        // Thor TripEdge does not carry; omitted.

        ProcessVerbalSuccinctTransitionInstruction(maneuvers);

        return maneuvers;
    }

    /// <summary>Produces the initial maneuver list (reverse pass). Faithful port of <c>Produce()</c>.</summary>
    protected LinkedList<Maneuver> Produce()
    {
        var maneuvers = new LinkedList<Maneuver>();

        // Validate trip path node list
        if (TripPath.NodeSize() < 1)
        {
            throw new ValhallaException(210);
        }

        // Check for a single node
        if (TripPath.NodeSize() == 1)
        {
            // TODO - handle origin and destination are the same
            throw new ValhallaException(211);
        }

        // PORT-NOTE (DEFER): the C++ validates location_size() < 2 (proto locations not carried).

        // Process the Destination maneuver
        maneuvers.AddFirst(new Maneuver());
        CreateDestinationManeuver(maneuvers.First!.Value);

        // Initialize maneuver prior to loop
        maneuvers.AddFirst(new Maneuver());
        InitializeManeuver(maneuvers.First!.Value, TripPath.GetLastNodeIndex());

        // Step through nodes in reverse order to produce maneuvers
        // excluding the last and first nodes
        for (int i = TripPath.GetLastNodeIndex() - 1; i > 0; --i)
        {
            EnhancedTripLeg_Node node = TripPath.GetEnhancedNode(i);

            // PORT-NOTE (DEFER): the C++ gates this block on curr_edge->pedestrian_type() == kBlind.
            // The ported TripEdge does not carry a per-edge pedestrian type (blind-routing cross-street
            // annotation is outside the maneuver-structure foundation), so the block never triggers.
            if (EdgeIsBlind())
            {
                switch (node.GetNodeType())
                {
                    case NodeType.StreetIntersection:
                    {
                        var nameList = new List<(string Name, bool IsRouteNumber)>();
                        for (int z = 0; z < node.IntersectingEdgeSize(); ++z)
                        {
                            EnhancedTripLeg_IntersectingEdge intersectingEdge = node.GetIntersectingEdge(z);
                            foreach (string name in intersectingEdge.Name())
                            {
                                // PORT-NOTE: intersecting-edge names carry no is-route-number flag in
                                // the ported Thor type; treated as false.
                                (string, bool) curStreet = (name, false);
                                if (!nameList.Contains(curStreet))
                                {
                                    nameList.Add(curStreet);
                                }
                            }
                        }

                        if (nameList.Count != 0)
                        {
                            maneuvers.First!.Value.SetCrossStreetNames(nameList);
                            maneuvers.First!.Value.SetNodeType(node.GetNodeType());
                            if (node.TrafficSignal())
                            {
                                maneuvers.First!.Value.SetTrafficSignal(true);
                            }
                        }

                        break;
                    }

                    case NodeType.Gate:
                    case NodeType.Bollard:
                        maneuvers.First!.Value.SetNodeType(node.GetNodeType());
                        break;
                    default:
                        break;
                }
            }

            if (CanManeuverIncludePrevEdge(maneuvers.First!.Value, i))
            {
                UpdateManeuver(maneuvers.First!.Value, i);
            }
            else
            {
                // Finalize current maneuver
                FinalizeManeuver(maneuvers.First!.Value, i);

                // Initialize new maneuver
                maneuvers.AddFirst(new Maneuver());
                InitializeManeuver(maneuvers.First!.Value, i);
            }
        }

        // Process the Start maneuver
        CreateStartManeuver(maneuvers.First!.Value);

        return maneuvers;
    }

    /// <summary>Combines maneuvers until no further combination occurs. Faithful port of <c>Combine()</c>.</summary>
    protected void Combine(LinkedList<Maneuver> maneuvers)
    {
        bool maneuversHaveBeenCombined = true;

        // Continue trying to combine maneuvers until no maneuvers have been combined
        while (maneuversHaveBeenCombined)
        {
            maneuversHaveBeenCombined = false;

            LinkedListNode<Maneuver>? prevMan = maneuvers.First;
            LinkedListNode<Maneuver>? currMan = maneuvers.First;
            LinkedListNode<Maneuver>? nextMan = maneuvers.First;

            if (nextMan != null)
            {
                nextMan = nextMan.Next;
            }

            while (nextMan != null)
            {
                // Process common base names
                StreetNames commonBaseNames =
                    currMan!.Value.StreetNames().FindCommonBaseNames(nextMan.Value.StreetNames());

                // Get the begin edge of the next maneuver
                EnhancedTripLeg_Edge? nextManBeginEdge = TripPath.GetCurrEdge((int)nextMan.Value.BeginNodeIndex());

                bool isFirstMan = currMan == maneuvers.First;

                // PORT-NOTE (DEFER): the transit-connection-collapse branches (kTransitConnectionStart /
                // kTransitConnectionDestination) read transit_connection_platform_info().type(); the
                // transit platform info is not carried. They are omitted (no transit input reaches here).

                // Do not combine
                // if current or next maneuver is an elevator
                if (currMan.Value.Elevator() || nextMan.Value.Elevator())
                {
                    prevMan = currMan;
                    currMan = nextMan;
                    nextMan = nextMan.Next;
                }
                // Do not combine
                // if current or next maneuver is indoor steps
                else if (currMan.Value.IndoorSteps() || nextMan.Value.IndoorSteps())
                {
                    prevMan = currMan;
                    currMan = nextMan;
                    nextMan = nextMan.Next;
                }
                // Do not combine
                // if current or next maneuver is an escalator
                else if (currMan.Value.Escalator() || nextMan.Value.Escalator())
                {
                    prevMan = currMan;
                    currMan = nextMan;
                    nextMan = nextMan.Next;
                }
                else if (currMan.Value.HasLevelChanges() != nextMan.Value.HasLevelChanges())
                {
                    prevMan = currMan;
                    currMan = nextMan;
                    nextMan = nextMan.Next;
                }
                // Do not combine
                // if current or next maneuver is a building entrance
                else if (currMan.Value.BuildingEnter() || nextMan.Value.BuildingEnter()
                         || currMan.Value.BuildingExit() || nextMan.Value.BuildingExit())
                {
                    prevMan = currMan;
                    currMan = nextMan;
                    nextMan = nextMan.Next;
                }
                // Do not combine
                // if any transit connection maneuvers
                else if (currMan.Value.TransitConnection() || nextMan.Value.TransitConnection())
                {
                    prevMan = currMan;
                    currMan = nextMan;
                    nextMan = nextMan.Next;
                }
                // Do not combine
                // if driving side is different
                else if (currMan.Value.DriveOnRight() != nextMan.Value.DriveOnRight())
                {
                    prevMan = currMan;
                    currMan = nextMan;
                    nextMan = nextMan.Next;
                }
                // Do not combine
                // if travel mode is different
                // OR next maneuver is destination
                else if ((currMan.Value.GetTravelMode() != nextMan.Value.GetTravelMode())
                         || (nextMan.Value.Type() == DirectionsLegManeuverType.Destination))
                {
                    prevMan = currMan;
                    currMan = nextMan;
                    nextMan = nextMan.Next;
                }
                // Combine current left unspecified internal maneuver with next left maneuver
                else if (PossibleUnspecifiedInternalManeuver(prevMan!, currMan, nextMan)
                         && prevMan!.Value.HasSimilarNames(nextMan.Value, true)
                         && (DetermineRelativeDirection(currMan.Value.TurnDegree()) == Maneuver.RelativeDirection.Left)
                         && (DetermineRelativeDirection(nextMan.Value.TurnDegree()) == Maneuver.RelativeDirection.Left)
                         && (DetermineRelativeDirection(
                                 Util.GetTurnDegree(prevMan.Value.EndHeading(), nextMan.Value.BeginHeading()))
                             == Maneuver.RelativeDirection.Reverse))
                {
                    currMan = CombineUnspecifiedInternalManeuver(maneuvers, prevMan, currMan, nextMan,
                        DirectionsLegManeuverType.UturnLeft);
                    maneuversHaveBeenCombined = true;
                    nextMan = currMan.Next;
                }
                // Combine current right unspecified internal maneuver with next right maneuver
                else if (PossibleUnspecifiedInternalManeuver(prevMan!, currMan, nextMan)
                         && prevMan!.Value.HasSimilarNames(nextMan.Value, true)
                         && (DetermineRelativeDirection(currMan.Value.TurnDegree()) == Maneuver.RelativeDirection.Right)
                         && (DetermineRelativeDirection(nextMan.Value.TurnDegree()) == Maneuver.RelativeDirection.Right)
                         && (DetermineRelativeDirection(
                                 Util.GetTurnDegree(prevMan.Value.EndHeading(), nextMan.Value.BeginHeading()))
                             == Maneuver.RelativeDirection.Reverse))
                {
                    currMan = CombineUnspecifiedInternalManeuver(maneuvers, prevMan, currMan, nextMan,
                        DirectionsLegManeuverType.UturnRight);
                    maneuversHaveBeenCombined = true;
                    nextMan = currMan.Next;
                }
                // Do not combine
                // if next maneuver is a fork or a tee
                else if (nextMan.Value.Fork() || nextMan.Value.Tee())
                {
                    prevMan = currMan;
                    currMan = nextMan;
                    nextMan = nextMan.Next;
                }
                // Do not combine
                // if current or next maneuver is a ferry
                else if (currMan.Value.Ferry() || nextMan.Value.Ferry())
                {
                    prevMan = currMan;
                    currMan = nextMan;
                    nextMan = nextMan.Next;
                }
                // Combine current internal maneuver with next maneuver
                else if (currMan.Value.InternalIntersection() && (currMan != nextMan)
                         && !nextMan.Value.IsDestinationType())
                {
                    currMan = CombineInternalManeuver(maneuvers, prevMan!, currMan, nextMan, isFirstMan);
                    if (isFirstMan)
                    {
                        prevMan = currMan;
                    }

                    maneuversHaveBeenCombined = true;
                    nextMan = currMan.Next;
                }
                // Combine current turn channel maneuver with next maneuver
                else if (IsTurnChannelManeuverCombinable(prevMan!, currMan, nextMan, isFirstMan))
                {
                    currMan = CombineTurnChannelManeuver(maneuvers, prevMan!, currMan, nextMan, isFirstMan);
                    if (isFirstMan)
                    {
                        prevMan = currMan;
                    }

                    maneuversHaveBeenCombined = true;
                    nextMan = currMan.Next;
                }
                // Do not combine
                // if next maneuver has an intersecting forward link
                else if (nextMan.Value.IntersectingForwardEdge())
                {
                    prevMan = currMan;
                    currMan = nextMan;
                    nextMan = nextMan.Next;
                }
                // Do not combine
                // if has node_type
                else if ((currMan.Value.GetPedestrianType() == PedestrianType.Blind
                          && nextMan.Value.GetPedestrianType() == PedestrianType.Blind)
                         && (currMan.Value.HasNodeType() || nextMan.Value.HasNodeType()
                             || currMan.Value.IsSteps() || nextMan.Value.IsSteps()
                             || currMan.Value.IsBridge() || nextMan.Value.IsBridge()
                             || currMan.Value.IsTunnel() || nextMan.Value.IsTunnel()))
                {
                    prevMan = currMan;
                    currMan = nextMan;
                    nextMan = nextMan.Next;
                }
                // Do not combine
                // if trail type is different (unnamed/named pedestrian/bike/mtb)
                else if (currMan.Value.GetTrailType() != nextMan.Value.GetTrailType())
                {
                    prevMan = currMan;
                    currMan = nextMan;
                    nextMan = nextMan.Next;
                }
                // Combine the 'same name straight' next maneuver with the current maneuver
                else if ((nextMan.Value.BeginRelativeDirection() == Maneuver.RelativeDirection.KeepStraight)
                         && (nextManBeginEdge != null && !nextManBeginEdge.IsTurnChannelUse())
                         && !nextMan.Value.InternalIntersection() && !currMan.Value.Ramp() && !nextMan.Value.Ramp()
                         && !currMan.Value.Roundabout() && !nextMan.Value.Roundabout() && commonBaseNames.Count != 0)
                {
                    // If needed, set the begin street names
                    if (!currMan.Value.HasBeginStreetNames() && !currMan.Value.PortionsHighway()
                        && (currMan.Value.StreetNames().Count > commonBaseNames.Count))
                    {
                        currMan.Value.SetBeginStreetNames(currMan.Value.StreetNames().Clone());
                    }

                    // Update current maneuver street names
                    currMan.Value.SetStreetNames(commonBaseNames);

                    nextMan = CombineManeuvers(maneuvers, currMan, nextMan);
                    maneuversHaveBeenCombined = true;
                }
                // Combine unnamed straight maneuvers
                else if ((nextMan.Value.BeginRelativeDirection() == Maneuver.RelativeDirection.KeepStraight)
                         && !currMan.Value.HasStreetNames() && !nextMan.Value.HasStreetNames()
                         && !currMan.Value.IsTransit() && !nextMan.Value.IsTransit()
                         && (nextManBeginEdge != null && !nextManBeginEdge.IsTurnChannelUse())
                         && !nextMan.Value.InternalIntersection() && !currMan.Value.Ramp() && !nextMan.Value.Ramp()
                         && !currMan.Value.Roundabout() && !nextMan.Value.Roundabout())
                {
                    nextMan = CombineManeuvers(maneuvers, currMan, nextMan);
                    maneuversHaveBeenCombined = true;
                }
                // Combine ramp maneuvers
                else if (AreRampManeuversCombinable(currMan, nextMan))
                {
                    nextMan = CombineManeuvers(maneuvers, currMan, nextMan);
                    maneuversHaveBeenCombined = true;
                }
                // Combine obvious maneuver
                else if (IsNextManeuverObvious(maneuvers, currMan, nextMan))
                {
                    // If current maneuver does not have street names then use the next maneuver street names
                    if (!currMan.Value.HasStreetNames() && nextMan.Value.HasStreetNames())
                    {
                        currMan.Value.SetStreetNames(nextMan.Value.StreetNames().Clone());
                    }

                    // Mark that the current maneuver contains an obvious maneuver
                    currMan.Value.SetContainsObviousManeuver(true);

                    // Disable turn channel
                    currMan.Value.SetTurnChannel(false);

                    nextMan = CombineManeuvers(maneuvers, currMan, nextMan);
                    maneuversHaveBeenCombined = true;
                }
                // Combine current short length non-internal edges (left or right) with next maneuver
                // that is a kRampStraight
                else if (PossibleUnspecifiedInternalManeuver(prevMan!, currMan, nextMan)
                         && ((DetermineRelativeDirection(currMan.Value.TurnDegree()) == Maneuver.RelativeDirection.Left)
                             || (DetermineRelativeDirection(currMan.Value.TurnDegree()) == Maneuver.RelativeDirection.Right))
                         && nextMan.Value.Type() == DirectionsLegManeuverType.RampStraight)
                {
                    currMan = CombineUnspecifiedInternalManeuver(maneuvers, prevMan!, currMan, nextMan,
                        DirectionsLegManeuverType.None);
                    maneuversHaveBeenCombined = true;
                    nextMan = currMan.Next;
                }
                else
                {
                    // Update with no combine
                    prevMan = currMan;
                    currMan = nextMan;
                    nextMan = nextMan.Next;
                }
            }
        }
    }

    /// <summary>
    /// Returns true if the current maneuver may be an unspecified internal maneuver. Faithful port of
    /// <c>PossibleUnspecifiedInternalManeuver()</c>.
    /// </summary>
    protected bool PossibleUnspecifiedInternalManeuver(
        LinkedListNode<Maneuver> prevMan,
        LinkedListNode<Maneuver> currMan,
        LinkedListNode<Maneuver> nextMan)
    {
        if (!currMan.Value.InternalIntersection() && currMan.Value.GetTravelMode() == TravelMode.Drive
            && !prevMan.Value.Roundabout() && !currMan.Value.Roundabout() && !nextMan.Value.Roundabout()
            && (currMan.Value.Length() <= (GraphConstants.MaxInternalLength * Constants.KmPerMeter))
            && currMan != nextMan && !currMan.Value.IsStartType() && !nextMan.Value.IsDestinationType())
        {
            return true;
        }

        return false;
    }

    /// <summary>Collapses unspecified internal edge maneuvers. Faithful port of <c>CombineUnspecifiedInternalManeuver()</c>.</summary>
    protected LinkedListNode<Maneuver> CombineUnspecifiedInternalManeuver(
        LinkedList<Maneuver> maneuvers,
        LinkedListNode<Maneuver> prevMan,
        LinkedListNode<Maneuver> currMan,
        LinkedListNode<Maneuver> nextMan,
        DirectionsLegManeuverType maneuverType)
    {
        // Determine turn degree based on previous maneuver and next maneuver
        nextMan.Value.SetTurnDegree(Util.GetTurnDegree(prevMan.Value.EndHeading(), nextMan.Value.BeginHeading()));

        // Set the cross street names
        if (currMan.Value.HasStreetNames())
        {
            nextMan.Value.SetCrossStreetNames(currMan.Value.StreetNames().Clone());
        }

        // Set relative direction
        nextMan.Value.SetBeginRelativeDirection(DetermineRelativeDirection(nextMan.Value.TurnDegree()));

        // Add distance
        nextMan.Value.SetLength(nextMan.Value.Length() + currMan.Value.Length());

        // Add time
        nextMan.Value.SetTime(nextMan.Value.Time() + currMan.Value.Time());

        // Add basic time
        nextMan.Value.SetBasicTime(nextMan.Value.BasicTime() + currMan.Value.BasicTime());

        // Set begin node index
        nextMan.Value.SetBeginNodeIndex(currMan.Value.BeginNodeIndex());

        // Set begin shape index
        nextMan.Value.SetBeginShapeIndex(currMan.Value.BeginShapeIndex());

        // Set maneuver type to specified argument
        nextMan.Value.SetType(maneuverType);

        return Erase(maneuvers, currMan);
    }

    /// <summary>Combines an internal-intersection maneuver. Faithful port of <c>CombineInternalManeuver()</c>.</summary>
    protected LinkedListNode<Maneuver> CombineInternalManeuver(
        LinkedList<Maneuver> maneuvers,
        LinkedListNode<Maneuver> prevMan,
        LinkedListNode<Maneuver> currMan,
        LinkedListNode<Maneuver> nextMan,
        bool startMan)
    {
        if (startMan)
        {
            // Determine turn degree current maneuver and next maneuver
            nextMan.Value.SetTurnDegree(Util.GetTurnDegree(currMan.Value.EndHeading(), nextMan.Value.BeginHeading()));
        }
        else
        {
            // Determine turn degree based on previous maneuver and next maneuver
            nextMan.Value.SetTurnDegree(Util.GetTurnDegree(prevMan.Value.EndHeading(), nextMan.Value.BeginHeading()));
        }

        // Set the cross street names
        if (currMan.Value.HasUsableInternalIntersectionName())
        {
            nextMan.Value.SetCrossStreetNames(currMan.Value.StreetNames().Clone());
        }

        // Set the right and left internal turn counts
        nextMan.Value.SetInternalRightTurnCount(currMan.Value.InternalRightTurnCount());
        nextMan.Value.SetInternalLeftTurnCount(currMan.Value.InternalLeftTurnCount());

        // Set relative direction
        nextMan.Value.SetBeginRelativeDirection(DetermineRelativeDirection(nextMan.Value.TurnDegree()));

        // If the relative direction is straight
        // and both internal left and right turns exist
        // then update the relative direction
        if ((nextMan.Value.BeginRelativeDirection() == Maneuver.RelativeDirection.KeepStraight)
            && (currMan.Value.InternalLeftTurnCount() > 0) && (currMan.Value.InternalRightTurnCount() > 0))
        {
            nextMan.Value.SetBeginRelativeDirection(DetermineRelativeDirection(
                Util.GetTurnDegree(prevMan.Value.EndHeading(), currMan.Value.EndHeading())));
        }

        // Add distance
        nextMan.Value.SetLength(nextMan.Value.Length() + currMan.Value.Length());

        // Add time
        nextMan.Value.SetTime(nextMan.Value.Time() + currMan.Value.Time());

        // Add basic time
        nextMan.Value.SetBasicTime(nextMan.Value.BasicTime() + currMan.Value.BasicTime());

        // Set begin node index
        nextMan.Value.SetBeginNodeIndex(currMan.Value.BeginNodeIndex());

        // Set begin shape index
        nextMan.Value.SetBeginShapeIndex(currMan.Value.BeginShapeIndex());

        // NOTE: Do not copy signs from internal maneuver
        if (startMan)
        {
            nextMan.Value.SetType(DirectionsLegManeuverType.Start);
        }
        else
        {
            // Set maneuver type to 'none' so the type will be processed again
            nextMan.Value.SetType(DirectionsLegManeuverType.None);
            SetManeuverType(nextMan.Value);
        }

        return Erase(maneuvers, currMan);
    }

    /// <summary>Combines a turn-channel maneuver. Faithful port of <c>CombineTurnChannelManeuver()</c>.</summary>
    protected LinkedListNode<Maneuver> CombineTurnChannelManeuver(
        LinkedList<Maneuver> maneuvers,
        LinkedListNode<Maneuver> prevMan,
        LinkedListNode<Maneuver> currMan,
        LinkedListNode<Maneuver> nextMan,
        bool startMan)
    {
        if (startMan)
        {
            nextMan.Value.SetTurnDegree(Util.GetTurnDegree(currMan.Value.EndHeading(), nextMan.Value.BeginHeading()));
        }
        else
        {
            nextMan.Value.SetTurnDegree(Util.GetTurnDegree(prevMan.Value.EndHeading(), nextMan.Value.BeginHeading()));
        }

        // Set relative direction
        nextMan.Value.SetBeginRelativeDirection(currMan.Value.BeginRelativeDirection());

        // Add distance
        nextMan.Value.SetLength(nextMan.Value.Length() + currMan.Value.Length());

        // Add time
        nextMan.Value.SetTime(nextMan.Value.Time() + currMan.Value.Time());

        // Add basic time
        nextMan.Value.SetBasicTime(nextMan.Value.BasicTime() + currMan.Value.BasicTime());

        // Set begin node index
        nextMan.Value.SetBeginNodeIndex(currMan.Value.BeginNodeIndex());

        // Set begin shape index
        nextMan.Value.SetBeginShapeIndex(currMan.Value.BeginShapeIndex());

        // Set signs, if needed
        if (currMan.Value.HasSigns() && !nextMan.Value.HasSigns())
        {
            nextMan.Value.MutableSigns().CopyFrom(currMan.Value.GetSigns());
        }

        if (startMan)
        {
            nextMan.Value.SetType(DirectionsLegManeuverType.Start);
        }
        else
        {
            // Set maneuver type to 'none' so the type will be processed again
            nextMan.Value.SetType(DirectionsLegManeuverType.None);
            SetManeuverType(nextMan.Value);
        }

        return Erase(maneuvers, currMan);
    }

    /// <summary>Combines the next maneuver into the current one. Faithful port of <c>CombineManeuvers()</c>.</summary>
    protected LinkedListNode<Maneuver> CombineManeuvers(
        LinkedList<Maneuver> maneuvers,
        LinkedListNode<Maneuver> currMan,
        LinkedListNode<Maneuver> nextMan)
    {
        Maneuver curr = currMan.Value;
        Maneuver next = nextMan.Value;

        // Add distance
        curr.SetLength(curr.Length() + next.Length());

        // Add time
        curr.SetTime(curr.Time() + next.Time());

        // Add basic time
        curr.SetBasicTime(curr.BasicTime() + next.BasicTime());

        // Update end heading
        curr.SetEndHeading(next.EndHeading());

        // Update end node index
        curr.SetEndNodeIndex(next.EndNodeIndex());

        // Update end shape index
        curr.SetEndShapeIndex(next.EndShapeIndex());

        // Update end level
        curr.SetEndLevelRef(next.EndLevelRef());

        if (next.Elevator())
        {
            curr.SetElevator(true);
        }

        if (next.IndoorSteps())
        {
            curr.SetIndoorSteps(true);
        }

        if (next.IsSteps())
        {
            curr.SetSteps(true);
        }

        if (next.Escalator())
        {
            curr.SetEscalator(true);
        }

        if (next.HasLevelChanges())
        {
            curr.SetHasLevelChanges(true);
        }

        if (next.Ramp())
        {
            curr.SetRamp(true);
        }

        if (next.Ferry())
        {
            curr.SetFerry(true);
        }

        if (next.RailFerry())
        {
            curr.SetRailFerry(true);
        }

        if (next.Roundabout())
        {
            curr.SetRoundabout(true);
        }

        if (next.PortionsToll())
        {
            curr.SetPortionsToll(true);
        }

        if (next.HasTimeRestrictions())
        {
            curr.SetHasTimeRestrictions(true);
        }

        if (next.PortionsUnpaved())
        {
            curr.SetPortionsUnpaved(true);
        }

        if (next.PortionsHighway())
        {
            curr.SetPortionsHighway(true);
        }

        if (next.ContainsObviousManeuver())
        {
            curr.SetContainsObviousManeuver(true);
        }

        return Erase(maneuvers, nextMan);
    }

    /// <summary>Counts and sorts the consecutive exit signs. Faithful port of <c>CountAndSortSigns()</c>.</summary>
    protected void CountAndSortSigns(LinkedList<Maneuver> maneuvers)
    {
        // Reverse iteration: prev_man = rbegin + 1, curr_man = rbegin
        LinkedListNode<Maneuver>? prevMan = maneuvers.Last;
        LinkedListNode<Maneuver>? currMan = maneuvers.Last;

        if (prevMan != null)
        {
            prevMan = prevMan.Previous;
        }

        // Rank the exit signs
        while (prevMan != null)
        {
            Maneuver prev = prevMan.Value;
            Maneuver curr = currMan!.Value;

            // Increase the branch exit sign consecutive count
            // if it matches the succeeding named maneuver
            if (prev.HasExitBranchSign() && !curr.HasExitSign() && curr.HasStreetNames())
            {
                foreach (OdinSign sign in prev.MutableSigns().MutableExitBranchList())
                {
                    foreach (StreetName streetName in curr.StreetNames())
                    {
                        if (sign.Text() == streetName.Value)
                        {
                            sign.SetConsecutiveCount(sign.ConsecutiveCount() + 1);
                        }
                    }
                }

                Signs.Sort(prev.MutableSigns().MutableExitBranchList());
            }
            // Increase the branch guide sign consecutive count
            // if it matches the succeeding named maneuver
            else if (prev.HasGuideBranchSign() && !curr.HasGuideSign() && curr.HasStreetNames())
            {
                foreach (OdinSign sign in prev.MutableSigns().MutableGuideBranchList())
                {
                    foreach (StreetName streetName in curr.StreetNames())
                    {
                        if (sign.Text() == streetName.Value)
                        {
                            sign.SetConsecutiveCount(sign.ConsecutiveCount() + 1);
                        }
                    }
                }

                Signs.Sort(prev.MutableSigns().MutableGuideBranchList());
            }
            // Increase the consecutive count of signs that match their neighbor
            else if (prev.HasSigns() && curr.HasSigns())
            {
                Signs.CountAndSort(prev.MutableSigns().MutableExitNumberList(), curr.MutableSigns().MutableExitNumberList());
                Signs.CountAndSort(prev.MutableSigns().MutableExitBranchList(), curr.MutableSigns().MutableExitBranchList());
                Signs.CountAndSort(prev.MutableSigns().MutableExitTowardList(), curr.MutableSigns().MutableExitTowardList());
                Signs.CountAndSort(prev.MutableSigns().MutableExitNameList(), curr.MutableSigns().MutableExitNameList());
                Signs.CountAndSort(prev.MutableSigns().MutableGuideBranchList(), curr.MutableSigns().MutableGuideBranchList());
                Signs.CountAndSort(prev.MutableSigns().MutableGuideTowardList(), curr.MutableSigns().MutableGuideTowardList());
                Signs.CountAndSort(prev.MutableSigns().MutableJunctionNameList(), curr.MutableSigns().MutableJunctionNameList());
            }

            // Update iterators
            currMan = prevMan;
            prevMan = prevMan.Previous;
        }
    }

    /// <summary>Marks maneuvers with a long street name. Faithful port of <c>ProcessVerbalSuccinctTransitionInstruction()</c>.</summary>
    protected void ProcessVerbalSuccinctTransitionInstruction(LinkedList<Maneuver> maneuvers)
    {
        foreach (Maneuver maneuver in maneuvers)
        {
            uint streetNameCount = 0;
            foreach (StreetName streetName in maneuver.StreetNames())
            {
                if (streetNameCount == OdinUtil.VerbalPreElementMaxCount)
                {
                    break;
                }

                if (OdinUtil.GetWordCount(streetName.Value) > MaxWordCount
                    || OdinUtil.StrlenUtf8(streetName.Value) > MaxStreetNameLength)
                {
                    maneuver.SetLongStreetName(true);
                    break;
                }

                ++streetNameCount;
            }

            if ((maneuver.Type() == DirectionsLegManeuverType.RoundaboutEnter) && !maneuver.HasLongStreetName())
            {
                uint roundaboutExitStreetNameCount = 0;
                foreach (StreetName roundaboutExitStreetName in maneuver.RoundaboutExitStreetNames())
                {
                    if (roundaboutExitStreetNameCount == OdinUtil.VerbalPreElementMaxCount)
                    {
                        break;
                    }

                    if (OdinUtil.GetWordCount(roundaboutExitStreetName.Value) > MaxWordCount
                        || OdinUtil.StrlenUtf8(roundaboutExitStreetName.Value) > MaxStreetNameLength)
                    {
                        maneuver.SetLongStreetName(true);
                        break;
                    }

                    ++roundaboutExitStreetNameCount;
                }
            }
        }
    }

    /// <summary>Re-runs the maneuver type assignment with none_type_allowed = false. Faithful port of <c>ConfirmManeuverTypeAssignment()</c>.</summary>
    protected void ConfirmManeuverTypeAssignment(LinkedList<Maneuver> maneuvers)
    {
        foreach (Maneuver maneuver in maneuvers)
        {
            SetManeuverType(maneuver, false);
        }
    }

    /// <summary>Creates the destination maneuver. Faithful port of <c>CreateDestinationManeuver()</c>.</summary>
    protected void CreateDestinationManeuver(Maneuver maneuver)
    {
        int nodeIndex = TripPath.GetLastNodeIndex();

        // PORT-NOTE (DEFER): the C++ reads GetDestination().side_of_street() (proto Location) to pick
        // DESTINATION_LEFT / DESTINATION_RIGHT. The ported TripLeg carries no Location; default to
        // kDestination (the side-of-street info is a snapping/location feature outside this foundation).
        maneuver.SetType(DirectionsLegManeuverType.Destination);

        // Set the begin and end node index
        maneuver.SetBeginNodeIndex((uint)nodeIndex);
        maneuver.SetEndNodeIndex((uint)nodeIndex);

        // Set the begin and end shape index
        EnhancedTripLeg_Edge prevEdge = TripPath.GetPrevEdge(nodeIndex)!;
        maneuver.SetBeginShapeIndex(prevEdge.EndShapeIndex());
        maneuver.SetEndShapeIndex(prevEdge.EndShapeIndex());

        // Travel mode
        maneuver.SetTravelMode(prevEdge.GetTravelMode());

        // Vehicle type
        if (prevEdge.HasVehicleType())
        {
            // PORT-NOTE: the ported TripEdge does not carry per-edge vehicle/pedestrian/bicycle/transit
            // type; the maneuver retains its default travel-type for the mode.
        }
    }

    /// <summary>Creates the start maneuver. Faithful port of <c>CreateStartManeuver()</c>.</summary>
    protected void CreateStartManeuver(Maneuver maneuver)
    {
        int nodeIndex = 0;

        // PORT-NOTE (DEFER): the C++ reads GetOrigin().side_of_street() (proto Location) to pick
        // START_LEFT / START_RIGHT; default to kStart (see CreateDestinationManeuver).
        maneuver.SetType(DirectionsLegManeuverType.Start);

        EnhancedTripLeg_Edge currEdge = TripPath.GetCurrEdge(nodeIndex)!;

        // exception: start maneuvers are not helpful for routes starting on stairs or escalators
        if (currEdge.IsStepsUse() || currEdge.IsEscalatorUse() || HasLevelChanges(currEdge))
        {
            maneuver.SetType(DirectionsLegManeuverType.None);
        }

        FinalizeManeuver(maneuver, nodeIndex);
    }

    /// <summary>Initializes a new maneuver at the specified node. Faithful port of <c>InitializeManeuver()</c>.</summary>
    protected void InitializeManeuver(Maneuver maneuver, int nodeIndex)
    {
        EnhancedTripLeg_Edge prevEdge = TripPath.GetPrevEdge(nodeIndex)!;
        EnhancedTripLeg_Edge? currEdge = TripPath.GetCurrEdge(nodeIndex);

        // Set the end heading
        maneuver.SetEndHeading(prevEdge.EndHeading());

        // Set the end node index
        maneuver.SetEndNodeIndex((uint)nodeIndex);

        // Set the end shape index
        maneuver.SetEndShapeIndex(prevEdge.EndShapeIndex());

        // Set the end level ref
        if (currEdge != null && currEdge.GetLevelRef().Count != 0)
        {
            if (currEdge.GetLevelRef().Count > 1)
            {
                maneuver.SetEndLevelRef(string.Empty);
            }
            else
            {
                maneuver.SetEndLevelRef(currEdge.GetLevelRef()[0]);
            }
        }

        // Elevator
        if (prevEdge.IsElevatorUse())
        {
            maneuver.SetElevator(true);
        }

        // Indoor Steps
        // PORT-NOTE: the ported TripEdge does not carry an indoor() flag; treated as not indoor.
        // Escalator
        if (prevEdge.IsEscalatorUse())
        {
            maneuver.SetEscalator(true);
        }

        if (HasLevelChanges(prevEdge))
        {
            maneuver.SetHasLevelChanges(true);
        }

        // Ramp
        if (prevEdge.IsRampUse())
        {
            maneuver.SetRamp(true);
        }

        // Turn Channel
        if (prevEdge.IsTurnChannelUse())
        {
            maneuver.SetTurnChannel(true);
        }

        // Ferry
        if (prevEdge.IsFerryUse())
        {
            maneuver.SetFerry(true);
        }

        // Rail Ferry
        if (prevEdge.IsRailFerryUse())
        {
            maneuver.SetRailFerry(true);
        }

        // Roundabout
        if (AreRoundaboutsProcessable(prevEdge.GetTravelMode()) && prevEdge.Roundabout())
        {
            maneuver.SetRoundabout(true);
            maneuver.SetRoundaboutExitCount(1);
        }

        // Internal Intersection - excluding the first and last edges
        if (prevEdge.InternalIntersection() && !TripPath.IsLastNodeIndex(nodeIndex)
            && !TripPath.IsFirstNodeIndex(nodeIndex - 1))
        {
            maneuver.SetInternalIntersection(true);
        }

        // Travel mode
        maneuver.SetTravelMode(prevEdge.GetTravelMode());

        // Driving side
        maneuver.SetDriveOnRight(prevEdge.DriveOnRight());

        // PORT-NOTE: per-edge vehicle/pedestrian/bicycle/transit types not carried by ported TripEdge.

        // Set trail type
        if (prevEdge.IsFootwayUse())
        {
            maneuver.SetTrailType(prevEdge.IsUnnamed() ? TrailType.UnnamedWalkway : TrailType.NamedWalkway);
        }
        else if (prevEdge.IsMountainBikeUse())
        {
            maneuver.SetTrailType(prevEdge.IsUnnamed() ? TrailType.UnnamedMtbTrail : TrailType.NamedMtbTrail);
        }
        else if (prevEdge.IsCyclewayUse())
        {
            maneuver.SetTrailType(prevEdge.IsUnnamed() ? TrailType.UnnamedCycleway : TrailType.NamedCycleway);
        }
        else
        {
            maneuver.SetTrailType(TrailType.None);
        }

        // PORT-NOTE (DEFER): transit info population (travel mode == PublicTransit) and transit-stop
        // insertion need transit route info the ported TripEdge does not carry; omitted.

        // Transit connection
        if (prevEdge.IsTransitConnection())
        {
            maneuver.SetTransitConnection(true);

            // If previous edge is transit connection platform
            // and current edge is transit then mark maneuver as transit connection start
            if (prevEdge.IsPlatformConnectionUse() && currEdge != null
                && (currEdge.GetTravelMode() == TravelMode.PublicTransit))
            {
                maneuver.SetType(DirectionsLegManeuverType.TransitConnectionStart);
            }
            else
            {
                maneuver.SetType(DirectionsLegManeuverType.TransitConnectionDestination);
            }
        }

        // only set steps to true if it involves a level change or the steps are long enough to
        // not be considered trivial
        // PORT-NOTE: the ported TripEdge does not carry traverses_levels(); use length threshold only.
        maneuver.SetSteps(prevEdge.GetUse() == Use.Steps && prevEdge.LengthKm() >= 0.003f);

        if (maneuver.GetPedestrianType() == PedestrianType.Blind)
        {
            if (prevEdge.GetUse() == Use.Steps)
            {
                maneuver.SetSteps(true);
            }

            if (prevEdge.Bridge())
            {
                maneuver.SetBridge(true);
            }

            if (prevEdge.Tunnel())
            {
                maneuver.SetTunnel(true);
            }
        }

        UpdateManeuver(maneuver, nodeIndex);
    }

    /// <summary>Adds the previous edge's attributes to the maneuver. Faithful port of <c>UpdateManeuver()</c>.</summary>
    protected void UpdateManeuver(Maneuver maneuver, int nodeIndex)
    {
        EnhancedTripLeg_Edge prevEdge = TripPath.GetPrevEdge(nodeIndex)!;

        // Street names
        // Set if street names are empty and maneuver is not internal intersection
        // or usable internal intersection name exists
        if ((maneuver.StreetNames().Count == 0 && !maneuver.InternalIntersection())
            || UsableInternalIntersectionName(maneuver, nodeIndex))
        {
            maneuver.SetStreetNames(
                StreetNamesFactory.Create(TripPath.GetCountryCode(nodeIndex), prevEdge.GetNameList()));
        }

        // Update the internal turn count
        UpdateInternalTurnCount(maneuver, nodeIndex);

        // Distance in kilometers
        maneuver.SetLength(maneuver.Length() + prevEdge.LengthKm());

        // Basic time (len/speed on each edge with no stop impact) in seconds
        maneuver.SetBasicTime(maneuver.BasicTime()
            + Util.GetTime(prevEdge.LengthKm(), GetSpeed(maneuver.GetTravelMode(), prevEdge.DefaultSpeed())));

        // Portions Toll
        if (prevEdge.Toll())
        {
            maneuver.SetPortionsToll(true);
        }

        if (prevEdge.HasTimeRestrictions())
        {
            maneuver.SetHasTimeRestrictions(true);
        }

        // Portions unpaved
        if (prevEdge.Unpaved())
        {
            maneuver.SetPortionsUnpaved(true);
        }

        // Portions highway
        if (prevEdge.IsHighway())
        {
            maneuver.SetPortionsHighway(true);
        }

        // Roundabouts
        if (AreRoundaboutsProcessable(prevEdge.GetTravelMode()) && prevEdge.Roundabout())
        {
            TravelMode mode = prevEdge.GetTravelMode();

            // Adjust bicycle travel mode if roundabout is a road
            if ((mode == TravelMode.Bicycle) && prevEdge.IsRoadUse())
            {
                mode = TravelMode.Drive;
            }

            var xedgeCounts = new IntersectingEdgeCounts();
            TripPath.GetEnhancedNode(nodeIndex)
                .CalculateRightLeftIntersectingEdgeCounts(prevEdge.EndHeading(), mode, ref xedgeCounts);
            if (prevEdge.DriveOnRight())
            {
                maneuver.SetRoundaboutExitCount(maneuver.RoundaboutExitCount() + xedgeCounts.RightTraversableOutbound);
            }
            else
            {
                maneuver.SetRoundaboutExitCount(maneuver.RoundaboutExitCount() + xedgeCounts.LeftTraversableOutbound);
            }
        }

        // Signs (exit signs come from the previous edge)
        if (prevEdge.HasSign())
        {
            TripSign sign = prevEdge.Sign()!;

            foreach (TripSignElement exitNumber in sign.ExitNumbers)
            {
                maneuver.MutableSigns().MutableExitNumberList().Add(new OdinSign(exitNumber.Text, exitNumber.IsRouteNumber));
            }

            foreach (TripSignElement exitOntoStreet in sign.ExitOntoStreets)
            {
                maneuver.MutableSigns().MutableExitBranchList().Add(new OdinSign(exitOntoStreet.Text, exitOntoStreet.IsRouteNumber));
            }

            foreach (TripSignElement exitTowardLocation in sign.ExitTowardLocations)
            {
                maneuver.MutableSigns().MutableExitTowardList().Add(new OdinSign(exitTowardLocation.Text, exitTowardLocation.IsRouteNumber));
            }

            foreach (TripSignElement exitName in sign.ExitNames)
            {
                maneuver.MutableSigns().MutableExitNameList().Add(new OdinSign(exitName.Text, exitName.IsRouteNumber));
            }
        }

        // PORT-NOTE (DEFER): transit-stop insertion (travel mode == PublicTransit) omitted.
    }

    /// <summary>Finalizes a maneuver, computing turn degree, relative direction, guide signs, type. Faithful port of <c>FinalizeManeuver()</c>.</summary>
    protected void FinalizeManeuver(Maneuver maneuver, int nodeIndex)
    {
        EnhancedTripLeg_Edge? prevEdge = TripPath.GetPrevEdge(nodeIndex);
        EnhancedTripLeg_Edge currEdge = TripPath.GetCurrEdge(nodeIndex)!;
        EnhancedTripLeg_Node node = TripPath.GetEnhancedNode(nodeIndex);

        // Set begin cardinal direction
        maneuver.SetBeginCardinalDirection(DetermineCardinalDirection(currEdge.BeginHeading()));

        // Set the begin heading
        maneuver.SetBeginHeading(currEdge.BeginHeading());

        // Set the begin node index
        maneuver.SetBeginNodeIndex((uint)nodeIndex);

        // Set the begin shape index
        maneuver.SetBeginShapeIndex(currEdge.BeginShapeIndex());

        // Set the time based on the delta of the elapsed time between the begin and end nodes
        maneuver.SetTime(TripPath.Node((int)maneuver.EndNodeIndex()).ElapsedCost.Secs
            - TripPath.Node((int)maneuver.BeginNodeIndex()).ElapsedCost.Secs);

        // Set elevator
        if (node.IsElevator())
        {
            maneuver.SetElevator(true);
            maneuver.SetNodeType(node.GetNodeType());

            // Set the end level ref
            if (currEdge.GetLevelRef().Count != 0)
            {
                if (currEdge.GetLevelRef().Count > 1)
                {
                    maneuver.SetEndLevelRef(string.Empty);
                }
                else
                {
                    maneuver.SetEndLevelRef(currEdge.GetLevelRef()[0]);
                }
            }
        }

        // Set enter/exit building
        // PORT-NOTE: the ported TripEdge does not carry an indoor() flag; building enter/exit not set.

        // if possible, set the turn degree and relative direction
        if (prevEdge != null)
        {
            maneuver.SetTurnDegree(Util.GetTurnDegree(prevEdge.EndHeading(), currEdge.BeginHeading()));

            // Calculate and set the relative direction for the specified maneuver
            DetermineRelativeDirection(maneuver);
        }

        // PORT-NOTE (DEFER): transit-connection-transfer / transit-connection-destination platform
        // info and transit-stop insertion need transit route info; omitted.

        // Set the begin intersecting edge name consistency
        maneuver.SetBeginIntersectingEdgeNameConsistency(node.HasIntersectingEdgeNameConsistency());

        // Set begin street names
        if (!currEdge.IsHighway() && !currEdge.InternalIntersection() && (currEdge.NameSize() > 1))
        {
            StreetNames currEdgeNames =
                StreetNamesFactory.Create(TripPath.GetCountryCode(nodeIndex), currEdge.GetNameList());
            StreetNames commonBaseNames = currEdgeNames.FindCommonBaseNames(maneuver.StreetNames());
            if (currEdgeNames.Count > commonBaseNames.Count)
            {
                maneuver.SetBeginStreetNames(currEdgeNames);
            }
        }

        // PORT-NOTE (DEFER): bike-share (bss_info) maneuver-type assignment needs node bss info; omitted.

        // Guide signs (come from the current edge)
        if (currEdge.HasSign())
        {
            TripSign sign = currEdge.Sign()!;

            foreach (TripSignElement guideOntoStreet in sign.GuideOntoStreets)
            {
                maneuver.MutableSigns().MutableGuideBranchList().Add(new OdinSign(guideOntoStreet.Text, guideOntoStreet.IsRouteNumber));
            }

            foreach (TripSignElement guideTowardLocation in sign.GuideTowardLocations)
            {
                maneuver.MutableSigns().MutableGuideTowardList().Add(new OdinSign(guideTowardLocation.Text, guideTowardLocation.IsRouteNumber));
            }

            foreach (TripSignElement junctionName in sign.JunctionNames)
            {
                maneuver.MutableSigns().MutableJunctionNameList().Add(new OdinSign(junctionName.Text, junctionName.IsRouteNumber));
            }
        }

        if (currEdge.IsPedestrianCrossingUse() && prevEdge != null && prevEdge.IsFootwayUse())
        {
            maneuver.SetPedestrianCrossing(true);
        }

        // Set the maneuver type
        SetManeuverType(maneuver);
    }

    /// <summary>Sets the maneuver type if currently None. Faithful port of <c>SetManeuverType()</c>.</summary>
    protected void SetManeuverType(Maneuver maneuver, bool noneTypeAllowed = true)
    {
        // If the type is already set then just return
        if (maneuver.Type() != DirectionsLegManeuverType.None)
        {
            return;
        }

        EnhancedTripLeg_Edge? prevEdge = TripPath.GetPrevEdge((int)maneuver.BeginNodeIndex());
        EnhancedTripLeg_Edge? currEdge = TripPath.GetCurrEdge((int)maneuver.BeginNodeIndex());

        // Process the different transit types
        if (maneuver.GetTravelMode() == TravelMode.PublicTransit)
        {
            if (prevEdge != null && prevEdge.GetTravelMode() == TravelMode.PublicTransit)
            {
                // PORT-NOTE (DEFER): transit-remain-on (needs block_id/trip_id) collapses to transfer.
                maneuver.SetType(DirectionsLegManeuverType.TransitTransfer);
            }
            else
            {
                maneuver.SetType(DirectionsLegManeuverType.Transit);
            }
        }
        // Process post transit connection destination
        else if (prevEdge != null && prevEdge.IsTransitConnectionUse()
                 && (maneuver.GetTravelMode() != TravelMode.PublicTransit))
        {
            maneuver.SetType(DirectionsLegManeuverType.PostTransitConnectionDestination);
        }
        // Process enter roundabout
        else if (maneuver.Roundabout())
        {
            maneuver.SetType(DirectionsLegManeuverType.RoundaboutEnter);
        }
        // Process exit roundabout
        else if (prevEdge != null && AreRoundaboutsProcessable(prevEdge.GetTravelMode()) && prevEdge.Roundabout())
        {
            maneuver.SetType(DirectionsLegManeuverType.RoundaboutExit);
        }
        // Process fork
        else if (maneuver.Fork())
        {
            switch (maneuver.BeginRelativeDirection())
            {
                case Maneuver.RelativeDirection.KeepRight:
                case Maneuver.RelativeDirection.Right:
                    maneuver.SetType(DirectionsLegManeuverType.StayRight);
                    break;
                case Maneuver.RelativeDirection.KeepLeft:
                case Maneuver.RelativeDirection.Left:
                    maneuver.SetType(DirectionsLegManeuverType.StayLeft);
                    break;
                default:
                    maneuver.SetType(DirectionsLegManeuverType.StayStraight);
                    break;
            }
        }
        // Process Internal Intersection
        else if (noneTypeAllowed && maneuver.InternalIntersection())
        {
            maneuver.SetType(DirectionsLegManeuverType.None);
        }
        // Process Turn Channel
        else if (noneTypeAllowed && maneuver.TurnChannel())
        {
            maneuver.SetType(DirectionsLegManeuverType.None);
        }
        // Process exit
        else if (maneuver.Ramp() && prevEdge != null
                 && (prevEdge.IsHighway() || maneuver.HasExitNumberSign()
                     || (!prevEdge.IsRampUse() && !RampLeadsToHighway(maneuver)
                         && ((maneuver.BeginRelativeDirection() == Maneuver.RelativeDirection.KeepRight)
                             || (maneuver.BeginRelativeDirection() == Maneuver.RelativeDirection.KeepLeft)))))
        {
            switch (maneuver.BeginRelativeDirection())
            {
                case Maneuver.RelativeDirection.KeepRight:
                case Maneuver.RelativeDirection.Right:
                    maneuver.SetType(DirectionsLegManeuverType.ExitRight);
                    break;
                case Maneuver.RelativeDirection.KeepLeft:
                case Maneuver.RelativeDirection.Left:
                    maneuver.SetType(DirectionsLegManeuverType.ExitLeft);
                    break;
                default:
                    if (maneuver.DriveOnRight())
                    {
                        maneuver.SetType(DirectionsLegManeuverType.ExitRight);
                    }
                    else
                    {
                        maneuver.SetType(DirectionsLegManeuverType.ExitLeft);
                    }

                    break;
            }
        }
        // Process on ramp
        else if (maneuver.Ramp() && prevEdge != null && !prevEdge.IsHighway())
        {
            switch (maneuver.BeginRelativeDirection())
            {
                case Maneuver.RelativeDirection.KeepRight:
                case Maneuver.RelativeDirection.Right:
                    maneuver.SetType(DirectionsLegManeuverType.RampRight);
                    break;
                case Maneuver.RelativeDirection.KeepLeft:
                case Maneuver.RelativeDirection.Left:
                    maneuver.SetType(DirectionsLegManeuverType.RampLeft);
                    break;
                case Maneuver.RelativeDirection.KeepStraight:
                    maneuver.SetType(DirectionsLegManeuverType.RampStraight);
                    break;
                case Maneuver.RelativeDirection.Reverse:
                    if (maneuver.DriveOnRight())
                    {
                        if (maneuver.TurnDegree() < 180)
                        {
                            maneuver.SetType(DirectionsLegManeuverType.RampRight);
                        }
                        else
                        {
                            maneuver.SetType(DirectionsLegManeuverType.RampLeft);
                        }
                    }
                    else
                    {
                        if (maneuver.TurnDegree() > 180)
                        {
                            maneuver.SetType(DirectionsLegManeuverType.RampLeft);
                        }
                        else
                        {
                            maneuver.SetType(DirectionsLegManeuverType.RampRight);
                        }
                    }

                    break;
                default:
                    maneuver.SetType(DirectionsLegManeuverType.RampRight);
                    break;
            }
        }
        // Process merge
        else if (IsMergeManeuverType(maneuver, prevEdge, currEdge))
        {
            switch (maneuver.MergeToRelativeDirection())
            {
                case Maneuver.RelativeDirection.KeepRight:
                    maneuver.SetType(DirectionsLegManeuverType.MergeRight);
                    break;
                case Maneuver.RelativeDirection.KeepLeft:
                    maneuver.SetType(DirectionsLegManeuverType.MergeLeft);
                    break;
                default:
                    maneuver.SetType(DirectionsLegManeuverType.Merge);
                    break;
            }
        }
        // Process enter ferry
        else if (maneuver.Ferry() || maneuver.RailFerry())
        {
            maneuver.SetType(DirectionsLegManeuverType.FerryEnter);
        }
        // Process exit ferry
        else if (prevEdge != null && (prevEdge.IsFerryUse() || prevEdge.IsRailFerryUse()))
        {
            maneuver.SetType(DirectionsLegManeuverType.FerryExit);
        }
        // Process elevator
        else if (maneuver.Elevator())
        {
            maneuver.SetType(DirectionsLegManeuverType.ElevatorEnter);
        }
        // Process steps
        else if (maneuver.IndoorSteps() || maneuver.IsSteps())
        {
            maneuver.SetType(DirectionsLegManeuverType.StepsEnter);
        }
        // Process escalator
        else if (maneuver.Escalator())
        {
            maneuver.SetType(DirectionsLegManeuverType.EscalatorEnter);
        }
        // Process enter building
        else if (maneuver.BuildingEnter())
        {
            maneuver.SetType(DirectionsLegManeuverType.BuildingEnter);
        }
        // Process exit building
        else if (maneuver.BuildingExit())
        {
            maneuver.SetType(DirectionsLegManeuverType.BuildingExit);
        }
        else if (maneuver.HasLevelChanges() && maneuver.EndLevelRef().Length != 0)
        {
            maneuver.SetType(DirectionsLegManeuverType.LevelChange);
        }
        else if (currEdge != null && currEdge.GetTravelMode() == TravelMode.Pedestrian && prevEdge != null
                 && (prevEdge.GetTravelMode() == TravelMode.Drive))
        {
            maneuver.SetType(DirectionsLegManeuverType.ParkVehicle);
        }
        // Process simple direction
        else
        {
            SetSimpleDirectionalManeuverType(maneuver, prevEdge, currEdge);
        }
    }

    /// <summary>Sets the simple directional maneuver type from the turn degree. Faithful port of <c>SetSimpleDirectionalManeuverType()</c>.</summary>
    protected void SetSimpleDirectionalManeuverType(
        Maneuver maneuver,
        EnhancedTripLeg_Edge? prevEdge,
        EnhancedTripLeg_Edge? currEdge)
    {
        switch (Turn.GetType(maneuver.TurnDegree()))
        {
            case Turn.Type.Straight:
            {
                maneuver.SetType(DirectionsLegManeuverType.Continue);

                if (_tripPath != null)
                {
                    EnhancedTripLeg_Edge? manBeginEdge = TripPath.GetCurrEdge((int)maneuver.BeginNodeIndex());
                    EnhancedTripLeg_Node node = TripPath.GetEnhancedNode((int)maneuver.BeginNodeIndex());
                    if (prevEdge == null || currEdge == null)
                    {
                        break;
                    }

                    // If the maneuver begin edge is a turn channel
                    // and the relative direction is not a keep straight
                    // then set as slight right / slight left based on relative keep right / keep left
                    if (manBeginEdge != null && manBeginEdge.IsTurnChannelUse()
                        && (maneuver.BeginRelativeDirection() != Maneuver.RelativeDirection.KeepStraight))
                    {
                        if (maneuver.BeginRelativeDirection() == Maneuver.RelativeDirection.KeepRight)
                        {
                            maneuver.SetType(DirectionsLegManeuverType.SlightRight);
                        }
                        else if (maneuver.BeginRelativeDirection() == Maneuver.RelativeDirection.KeepLeft)
                        {
                            maneuver.SetType(DirectionsLegManeuverType.SlightLeft);
                        }
                    }
                    // If internal intersection at beginning of maneuver
                    else if (currEdge.InternalIntersection())
                    {
                        // Straight turn type but left relative direction
                        if ((maneuver.BeginRelativeDirection() == Maneuver.RelativeDirection.Left)
                            || (maneuver.BeginRelativeDirection() == Maneuver.RelativeDirection.KeepLeft))
                        {
                            maneuver.SetType(DirectionsLegManeuverType.SlightLeft);
                        }
                        // Straight turn type but right relative direction
                        else if ((maneuver.BeginRelativeDirection() == Maneuver.RelativeDirection.Right)
                                 || (maneuver.BeginRelativeDirection() == Maneuver.RelativeDirection.KeepRight))
                        {
                            maneuver.SetType(DirectionsLegManeuverType.SlightRight);
                        }
                    }
                    else if (currEdge.IsHighway() && (maneuver.Length() < ShortContinueThreshold))
                    {
                        // Keep as short continue - no adjustment needed
                        break;
                    }
                    else if ((maneuver.BeginRelativeDirection() == Maneuver.RelativeDirection.KeepRight)
                             && node.HasSimilarStraightSignificantRoadClassXEdge(maneuver.TurnDegree(),
                                 prevEdge.EndHeading(), prevEdge.GetTravelMode(), prevEdge.GetRoadClass()))
                    {
                        // Handle highways
                        if (currEdge.IsHighway()
                            || node.HasForwardTraversableUseXEdge(prevEdge.EndHeading(), prevEdge.GetTravelMode(), Use.Ramp))
                        {
                            if (node.HasSimilarStraightNonRampOrSameNameRampXEdge(maneuver.TurnDegree(),
                                    prevEdge.EndHeading(), prevEdge.GetTravelMode()))
                            {
                                maneuver.SetType(DirectionsLegManeuverType.StayRight);
                            }
                        }
                        else
                        {
                            maneuver.SetType(DirectionsLegManeuverType.SlightRight);
                        }
                    }
                    else if ((maneuver.BeginRelativeDirection() == Maneuver.RelativeDirection.KeepLeft)
                             && node.HasSimilarStraightSignificantRoadClassXEdge(maneuver.TurnDegree(),
                                 prevEdge.EndHeading(), prevEdge.GetTravelMode(), prevEdge.GetRoadClass()))
                    {
                        // Handle highways
                        if (currEdge.IsHighway()
                            || node.HasForwardTraversableUseXEdge(prevEdge.EndHeading(), prevEdge.GetTravelMode(), Use.Ramp))
                        {
                            if (node.HasSimilarStraightNonRampOrSameNameRampXEdge(maneuver.TurnDegree(),
                                    prevEdge.EndHeading(), prevEdge.GetTravelMode()))
                            {
                                maneuver.SetType(DirectionsLegManeuverType.StayLeft);
                            }
                        }
                        else
                        {
                            maneuver.SetType(DirectionsLegManeuverType.SlightLeft);
                        }
                    }
                }

                break;
            }

            case Turn.Type.SlightRight:
            {
                maneuver.SetType(DirectionsLegManeuverType.SlightRight);

                var xedgeCounts = new IntersectingEdgeCounts();
                EnhancedTripLeg_Node node = TripPath.GetEnhancedNode((int)maneuver.BeginNodeIndex());
                if (prevEdge == null || currEdge == null)
                {
                    break;
                }

                node.CalculateRightLeftIntersectingEdgeCounts(prevEdge.EndHeading(), prevEdge.GetTravelMode(), ref xedgeCounts);
                if ((maneuver.BeginRelativeDirection() == Maneuver.RelativeDirection.KeepStraight)
                    && !node.HasForwardTraversableSignificantRoadClassXEdge(prevEdge.EndHeading(),
                        prevEdge.GetTravelMode(), prevEdge.GetRoadClass())
                    && ((xedgeCounts.Right > 0) || ((xedgeCounts.Right == 0) && (xedgeCounts.Left == 0))))
                {
                    maneuver.SetType(DirectionsLegManeuverType.Continue);
                }

                break;
            }

            case Turn.Type.Right:
            {
                maneuver.SetType(DirectionsLegManeuverType.Right);

                EnhancedTripLeg_Node node = TripPath.GetEnhancedNode((int)maneuver.BeginNodeIndex());
                if (prevEdge == null || currEdge == null)
                {
                    break;
                }

                if (node.HasTraversableOutboundIntersectingEdge(maneuver.GetTravelMode()))
                {
                    uint rightMostTurnDegree = node.GetRightMostTurnDegree(maneuver.TurnDegree(),
                        prevEdge.EndHeading(), maneuver.GetTravelMode());
                    if (maneuver.TurnDegree() == rightMostTurnDegree)
                    {
                        maneuver.SetType(DirectionsLegManeuverType.Right);
                        break;
                    }
                    else if ((maneuver.TurnDegree() < rightMostTurnDegree)
                             && !node.HasSpecifiedTurnXEdge(Turn.Type.SlightRight, prevEdge.EndHeading(), maneuver.GetTravelMode()))
                    {
                        maneuver.SetType(DirectionsLegManeuverType.SlightRight);
                        break;
                    }
                    else if ((maneuver.TurnDegree() > rightMostTurnDegree)
                             && !node.HasSpecifiedTurnXEdge(Turn.Type.SharpRight, prevEdge.EndHeading(), maneuver.GetTravelMode()))
                    {
                        maneuver.SetType(DirectionsLegManeuverType.SharpRight);
                        break;
                    }
                }

                break;
            }

            case Turn.Type.SharpRight:
            {
                maneuver.SetType(DirectionsLegManeuverType.SharpRight);
                break;
            }

            case Turn.Type.Reverse:
            {
                if (maneuver.InternalLeftTurnCount() > maneuver.InternalRightTurnCount())
                {
                    maneuver.SetType(DirectionsLegManeuverType.UturnLeft);
                }
                else if (maneuver.InternalRightTurnCount() > maneuver.InternalLeftTurnCount())
                {
                    maneuver.SetType(DirectionsLegManeuverType.UturnRight);
                }
                else if (maneuver.BeginRelativeDirection() == Maneuver.RelativeDirection.KeepLeft)
                {
                    maneuver.SetType(DirectionsLegManeuverType.UturnLeft);
                }
                else if (maneuver.BeginRelativeDirection() == Maneuver.RelativeDirection.KeepRight)
                {
                    maneuver.SetType(DirectionsLegManeuverType.UturnRight);
                }
                else if (TripPath.GetCurrEdge((int)maneuver.BeginNodeIndex())!.DriveOnRight())
                {
                    if (maneuver.TurnDegree() < 180)
                    {
                        maneuver.SetType(DirectionsLegManeuverType.UturnRight);
                    }
                    else
                    {
                        maneuver.SetType(DirectionsLegManeuverType.UturnLeft);
                    }
                }
                else
                {
                    if (maneuver.TurnDegree() > 180)
                    {
                        maneuver.SetType(DirectionsLegManeuverType.UturnLeft);
                    }
                    else
                    {
                        maneuver.SetType(DirectionsLegManeuverType.UturnRight);
                    }
                }

                break;
            }

            case Turn.Type.SharpLeft:
            {
                maneuver.SetType(DirectionsLegManeuverType.SharpLeft);
                break;
            }

            case Turn.Type.Left:
            {
                maneuver.SetType(DirectionsLegManeuverType.Left);

                EnhancedTripLeg_Node node = TripPath.GetEnhancedNode((int)maneuver.BeginNodeIndex());
                if (prevEdge == null || currEdge == null)
                {
                    break;
                }

                if (node.HasTraversableOutboundIntersectingEdge(maneuver.GetTravelMode()))
                {
                    uint leftMostTurnDegree = node.GetLeftMostTurnDegree(maneuver.TurnDegree(),
                        prevEdge.EndHeading(), maneuver.GetTravelMode());
                    if (maneuver.TurnDegree() == leftMostTurnDegree)
                    {
                        maneuver.SetType(DirectionsLegManeuverType.Left);
                        break;
                    }
                    else if ((maneuver.TurnDegree() > leftMostTurnDegree)
                             && !node.HasSpecifiedTurnXEdge(Turn.Type.SlightLeft, prevEdge.EndHeading(), maneuver.GetTravelMode()))
                    {
                        maneuver.SetType(DirectionsLegManeuverType.SlightLeft);
                        break;
                    }
                    else if ((maneuver.TurnDegree() < leftMostTurnDegree)
                             && !node.HasSpecifiedTurnXEdge(Turn.Type.SharpLeft, prevEdge.EndHeading(), maneuver.GetTravelMode()))
                    {
                        maneuver.SetType(DirectionsLegManeuverType.SharpLeft);
                        break;
                    }
                }

                break;
            }

            case Turn.Type.SlightLeft:
            {
                maneuver.SetType(DirectionsLegManeuverType.SlightLeft);

                var xedgeCounts = new IntersectingEdgeCounts();
                EnhancedTripLeg_Node node = TripPath.GetEnhancedNode((int)maneuver.BeginNodeIndex());
                if (prevEdge == null || currEdge == null)
                {
                    break;
                }

                node.CalculateRightLeftIntersectingEdgeCounts(prevEdge.EndHeading(), prevEdge.GetTravelMode(), ref xedgeCounts);
                if ((maneuver.BeginRelativeDirection() == Maneuver.RelativeDirection.KeepStraight)
                    && !node.HasForwardTraversableSignificantRoadClassXEdge(prevEdge.EndHeading(),
                        prevEdge.GetTravelMode(), prevEdge.GetRoadClass())
                    && ((xedgeCounts.Left > 0) || ((xedgeCounts.Right == 0) && (xedgeCounts.Left == 0))))
                {
                    maneuver.SetType(DirectionsLegManeuverType.Continue);
                }

                break;
            }
        }
    }

    /// <summary>Determines the begin cardinal direction. Faithful port of <c>DetermineCardinalDirection()</c>.</summary>
    protected DirectionsLegManeuverCardinalDirection DetermineCardinalDirection(uint heading)
    {
        if ((heading > 336) || (heading < 24))
        {
            return DirectionsLegManeuverCardinalDirection.North;
        }
        else if ((heading > 23) && (heading < 67))
        {
            return DirectionsLegManeuverCardinalDirection.NorthEast;
        }
        else if ((heading > 66) && (heading < 114))
        {
            return DirectionsLegManeuverCardinalDirection.East;
        }
        else if ((heading > 113) && (heading < 157))
        {
            return DirectionsLegManeuverCardinalDirection.SouthEast;
        }
        else if ((heading > 156) && (heading < 204))
        {
            return DirectionsLegManeuverCardinalDirection.South;
        }
        else if ((heading > 203) && (heading < 247))
        {
            return DirectionsLegManeuverCardinalDirection.SouthWest;
        }
        else if ((heading > 246) && (heading < 294))
        {
            return DirectionsLegManeuverCardinalDirection.West;
        }
        else if ((heading > 293) && (heading < 337))
        {
            return DirectionsLegManeuverCardinalDirection.NorthWest;
        }

        throw new ValhallaException(220);
    }

    /// <summary>Returns true if the maneuver may include the previous edge. Faithful port of <c>CanManeuverIncludePrevEdge()</c>.</summary>
    protected bool CanManeuverIncludePrevEdge(Maneuver maneuver, int nodeIndex)
    {
        EnhancedTripLeg_Edge prevEdge = TripPath.GetPrevEdge(nodeIndex)!;
        EnhancedTripLeg_Edge currEdge = TripPath.GetCurrEdge(nodeIndex)!;
        EnhancedTripLeg_Node node = TripPath.GetEnhancedNode(nodeIndex);
        uint turnDegree = Util.GetTurnDegree(prevEdge.EndHeading(), currEdge.BeginHeading());

        // PORT-NOTE (DEFER): curr_edge->pedestrian_type() == kBlind is not carried by the ported
        // TripEdge (see Produce); the blind-routing node-type guard cannot trigger.
        if (EdgeIsBlind() && maneuver.HasNodeType())
        {
            return false;
        }

        if (node.GetNodeType() == NodeType.BikeShare)
        {
            return false;
        }

        if (node.GetNodeType() == NodeType.Parking)
        {
            return false;
        }

        // Process transit
        if ((maneuver.GetTravelMode() == TravelMode.PublicTransit) && (prevEdge.GetTravelMode() != TravelMode.PublicTransit))
        {
            return false;
        }

        if ((prevEdge.GetTravelMode() == TravelMode.PublicTransit) && (maneuver.GetTravelMode() != TravelMode.PublicTransit))
        {
            return false;
        }

        if ((maneuver.GetTravelMode() == TravelMode.PublicTransit) && (prevEdge.GetTravelMode() == TravelMode.PublicTransit))
        {
            // PORT-NOTE (DEFER): combining transit requires matching block_id + trip_id (not carried);
            // conservatively do not combine.
            return false;
        }

        // Process transit connection
        if (maneuver.TransitConnection() && prevEdge.IsTransitConnection())
        {
            // Logic for a transit entrance in reverse
            if (prevEdge.IsEgressConnectionUse() && currEdge.IsPlatformConnectionUse())
            {
                return true;
            }
            else if (prevEdge.IsTransitConnectionUse() && currEdge.IsEgressConnectionUse())
            {
                return true;
            }

            // Logic for a transit exit in reverse
            if (prevEdge.IsEgressConnectionUse() && currEdge.IsTransitConnectionUse())
            {
                return true;
            }
            else if (prevEdge.IsPlatformConnectionUse() && currEdge.IsEgressConnectionUse())
            {
                return true;
            }

            // Combine for station transfer
            if (prevEdge.IsPlatformConnectionUse() && currEdge.IsPlatformConnectionUse())
            {
                return true;
            }

            return false;
        }
        else if (maneuver.TransitConnection() || prevEdge.IsTransitConnection())
        {
            return false;
        }

        // Process driving side
        if (maneuver.DriveOnRight() != prevEdge.DriveOnRight())
        {
            return false;
        }

        // Process elevator
        if (maneuver.Elevator() && !prevEdge.IsElevatorUse())
        {
            return false;
        }

        if (prevEdge.IsElevatorUse() && !maneuver.Elevator())
        {
            return false;
        }

        if (maneuver.Elevator() && prevEdge.IsElevatorUse())
        {
            return true;
        }

        if (node.IsElevator())
        {
            return false;
        }

        // Process indoor steps
        // PORT-NOTE: the ported TripEdge does not carry indoor(); indoor-steps tests reduce to false.
        if (maneuver.IndoorSteps())
        {
            return false;
        }

        // Process steps
        if (maneuver.IsSteps() && !prevEdge.IsStepsUse())
        {
            return false;
        }

        if (prevEdge.IsStepsUse() && !maneuver.IsSteps())
        {
            return false;
        }

        if (maneuver.IsSteps() && prevEdge.IsStepsUse())
        {
            return true;
        }

        // Process escalator
        if (maneuver.Escalator() && !prevEdge.IsEscalatorUse())
        {
            return false;
        }

        if (prevEdge.IsEscalatorUse() && !maneuver.Escalator())
        {
            return false;
        }

        if (maneuver.Escalator() && prevEdge.IsEscalatorUse())
        {
            return true;
        }

        // Process building entrance
        if (node.IsBuildingEntrance())
        {
            return false;
        }

        if (maneuver.HasLevelChanges() != HasLevelChanges(prevEdge))
        {
            return false;
        }

        // Process travel mode and travel types (unnamed pedestrian and bike)
        if (maneuver.GetTravelMode() != prevEdge.GetTravelMode())
        {
            return false;
        }

        if (maneuver.UnnamedWalkway() != prevEdge.IsUnnamedWalkway())
        {
            return false;
        }

        if (maneuver.UnnamedCycleway() != prevEdge.IsUnnamedCycleway())
        {
            return false;
        }

        if (maneuver.UnnamedMountainBikeTrail() != prevEdge.IsUnnamedMountainBikeTrail())
        {
            return false;
        }

        // Process roundabouts
        if (AreRoundaboutsProcessable(prevEdge.GetTravelMode()))
        {
            if (maneuver.Roundabout() && !prevEdge.Roundabout())
            {
                return false;
            }

            if (prevEdge.Roundabout() && !maneuver.Roundabout())
            {
                return false;
            }

            if (maneuver.Roundabout() && prevEdge.Roundabout())
            {
                return true;
            }
        }

        // Process fork
        if (IsFork(nodeIndex, prevEdge, currEdge) || IsPedestrianFork(nodeIndex, prevEdge, currEdge))
        {
            maneuver.SetFork(true);
            return false;
        }

        // Process internal intersection - cannot be the first edge in the trip
        if (prevEdge.InternalIntersection() && !maneuver.InternalIntersection())
        {
            return false;
        }
        else if (!prevEdge.InternalIntersection() && maneuver.InternalIntersection())
        {
            return false;
        }
        else if (prevEdge.InternalIntersection() && !TripPath.IsFirstNodeIndex(nodeIndex - 1)
                 && maneuver.InternalIntersection())
        {
            return true;
        }

        // Process simple turn channel
        if (prevEdge.IsTurnChannelUse() && !maneuver.TurnChannel())
        {
            return false;
        }
        else if (!prevEdge.IsTurnChannelUse() && maneuver.TurnChannel())
        {
            return false;
        }
        else if (prevEdge.IsTurnChannelUse() && maneuver.TurnChannel())
        {
            return true;
        }

        // Process exit signs
        if (maneuver.HasExitSign())
        {
            return false;
        }

        // Process ramps
        if (maneuver.Ramp() && !prevEdge.IsRampUse())
        {
            return false;
        }

        if (prevEdge.IsRampUse() && !maneuver.Ramp())
        {
            return false;
        }

        if (maneuver.Ramp() && prevEdge.IsRampUse())
        {
            // Do not combine if ramp to ramp is not forward
            if (!currEdge.IsForward(turnDegree))
            {
                return false;
            }

            return true;
        }

        // Process ferries
        if (maneuver.Ferry() && !prevEdge.IsFerryUse())
        {
            return false;
        }

        if (prevEdge.IsFerryUse() && !maneuver.Ferry())
        {
            return false;
        }

        if (maneuver.Ferry() && prevEdge.IsFerryUse())
        {
            return true;
        }

        // Process rail ferries
        if (maneuver.RailFerry() && !prevEdge.IsRailFerryUse())
        {
            return false;
        }

        if (prevEdge.IsRailFerryUse() && !maneuver.RailFerry())
        {
            return false;
        }

        if (maneuver.RailFerry() && prevEdge.IsRailFerryUse())
        {
            return true;
        }

        // Process simple u-turns
        if (turnDegree == 180)
        {
            // If drive on right then left u-turn
            if (prevEdge.DriveOnRight())
            {
                maneuver.SetType(DirectionsLegManeuverType.UturnLeft);
            }
            else
            {
                maneuver.SetType(DirectionsLegManeuverType.UturnRight);
            }

            return false;
        }

        // Process pencil point u-turns
        if (IsLeftPencilPointUturn(nodeIndex, prevEdge, currEdge))
        {
            maneuver.SetType(DirectionsLegManeuverType.UturnLeft);
            return false;
        }

        if (IsRightPencilPointUturn(nodeIndex, prevEdge, currEdge))
        {
            maneuver.SetType(DirectionsLegManeuverType.UturnRight);
            return false;
        }

        // Intersecting forward edge
        if (IsIntersectingForwardEdge(nodeIndex, prevEdge, currEdge))
        {
            maneuver.SetIntersectingForwardEdge(true);
            return false;
        }

        // Determine previous edge names and common base names
        StreetNames prevEdgeNames = StreetNamesFactory.Create(TripPath.GetCountryCode(nodeIndex), prevEdge.GetNameList());
        StreetNames commonBaseNames = prevEdgeNames.FindCommonBaseNames(maneuver.StreetNames());

        // Process 'T' intersection
        if (IsTee(nodeIndex, prevEdge, currEdge, commonBaseNames.Count != 0))
        {
            maneuver.SetTee(true);
            return false;
        }

        // Process non-forward transition with intersecting traversable edge
        if (!currEdge.IsStraightest(turnDegree,
                node.GetStraightestTraversableIntersectingEdgeTurnDegree(prevEdge.EndHeading(), prevEdge.GetTravelMode()))
            && !node.HasForwardTraversableIntersectingEdge(prevEdge.EndHeading(), prevEdge.GetTravelMode())
            && node.HasTraversableExcludeUseXEdge(prevEdge.GetTravelMode(), Use.Track))
        {
            return false;
        }

        // Process common base names
        if (commonBaseNames.Count != 0)
        {
            maneuver.SetStreetNames(commonBaseNames);
            return true;
        }

        // Process unnamed edge
        if (!maneuver.HasStreetNames() && prevEdge.IsUnnamed()
            && IncludeUnnamedPrevEdge(nodeIndex, prevEdge, currEdge))
        {
            return true;
        }

        return false;
    }

    /// <summary>Returns true if an unnamed previous edge should be included. Faithful port of <c>IncludeUnnamedPrevEdge()</c>.</summary>
    protected bool IncludeUnnamedPrevEdge(int nodeIndex, EnhancedTripLeg_Edge prevEdge, EnhancedTripLeg_Edge currEdge)
    {
        EnhancedTripLeg_Node node = TripPath.GetEnhancedNode(nodeIndex);

        if (!node.HasIntersectingEdges())
        {
            return true;
        }
        else if (currEdge.IsStraightest(
                     Util.GetTurnDegree(prevEdge.EndHeading(), currEdge.BeginHeading()),
                     node.GetStraightestIntersectingEdgeTurnDegree(prevEdge.EndHeading())))
        {
            return true;
        }

        return false;
    }

    /// <summary>Determines the merge-to relative direction. Faithful port of <c>DetermineMergeToRelativeDirection()</c>.</summary>
    protected Maneuver.RelativeDirection DetermineMergeToRelativeDirection(EnhancedTripLeg_Node node, EnhancedTripLeg_Edge prevEdge)
    {
        var xedgeCounts = new IntersectingEdgeCounts();
        node.CalculateRightLeftIntersectingEdgeCounts(prevEdge.EndHeading(), prevEdge.GetTravelMode(), ref xedgeCounts);
        if ((xedgeCounts.Left > 0) && (xedgeCounts.LeftSimilar == 0) && (xedgeCounts.Right == 0))
        {
            return Maneuver.RelativeDirection.KeepLeft;
        }
        else if ((xedgeCounts.Right > 0) && (xedgeCounts.RightSimilar == 0) && (xedgeCounts.Left == 0))
        {
            return Maneuver.RelativeDirection.KeepRight;
        }

        return Maneuver.RelativeDirection.None;
    }

    /// <summary>Returns true if the maneuver is a merge type. Faithful port of <c>IsMergeManeuverType()</c>.</summary>
    protected bool IsMergeManeuverType(Maneuver maneuver, EnhancedTripLeg_Edge? prevEdge, EnhancedTripLeg_Edge? currEdge)
    {
        EnhancedTripLeg_Node node = TripPath.GetEnhancedNode((int)maneuver.BeginNodeIndex());
        if (prevEdge != null && prevEdge.IsRampUse() && currEdge != null && !currEdge.IsRampUse()
            && (currEdge.IsHighway()
                || (((currEdge.GetRoadClass() == RoadClass.Trunk) || (currEdge.GetRoadClass() == RoadClass.Primary))
                    && currEdge.IsOneway() && currEdge.IsForward(maneuver.TurnDegree())
                    && node.HasIntersectingEdgeCurrNameConsistency())))
        {
            maneuver.SetMergeToRelativeDirection(DetermineMergeToRelativeDirection(node, prevEdge));
            return true;
        }

        return false;
    }

    // Return the (min,max) length in km for a deceleration lane as a function of the road's speed.
    private static (float Min, float Max) GetDecelerationLaneLength(float speedKph)
    {
        float length;

        if (speedKph < 80)
        {
            length = 0.1f;
        }
        else
        {
            length = (0.00141994f * speedKph) + 0.03509388f;
        }

        const float pctTol = 0.35f;
        float tol = length * pctTol;
        return (length - tol, length + tol);
    }

    /// <summary>Returns true if the node is a fork. Faithful port of <c>IsFork()</c>.</summary>
    protected bool IsFork(int nodeIndex, EnhancedTripLeg_Edge prevEdge, EnhancedTripLeg_Edge currEdge)
    {
        EnhancedTripLeg_Node node = TripPath.GetEnhancedNode(nodeIndex);

        // Must have 1 or 2 intersecting edges
        if ((node.IntersectingEdgeSize() < 1) || (node.IntersectingEdgeSize() > 2))
        {
            return false;
        }

        if (node.Fork()
            && currEdge.IsWiderForward(Util.GetTurnDegree(prevEdge.EndHeading(), currEdge.BeginHeading()))
            && node.HasWiderForwardTraversableIntersectingEdge(prevEdge.EndHeading(), currEdge.GetTravelMode()))
        {
            // If node is a motorway junction and current edge is not a service road class and an
            // intersecting edge is a service road class then not a fork
            if (node.IsMotorwayJunction() && (currEdge.GetRoadClass() != RoadClass.ServiceOther)
                && node.HasSpecifiedRoadClassXEdge(RoadClass.ServiceOther))
            {
                return false;
            }

            var xedgeCounts = new IntersectingEdgeCounts();
            node.CalculateRightLeftIntersectingEdgeCounts(prevEdge.EndHeading(), prevEdge.GetTravelMode(), ref xedgeCounts);

            if (((xedgeCounts.LeftSimilarTraversableOutbound > 0) || (xedgeCounts.RightSimilarTraversableOutbound > 0))
                || (((xedgeCounts.LeftTraversableOutbound > 0) || (xedgeCounts.RightTraversableOutbound > 0))
                    && currEdge.IsRampUse()
                    && !node.IsStraightestTraversableIntersectingEdgeReversed(prevEdge.EndHeading(), prevEdge.GetTravelMode()))
                || (currEdge.IsForkForward(Util.GetTurnDegree(prevEdge.EndHeading(), currEdge.BeginHeading()))
                    && node.HasOnlyForwardTraversableRoadClassXEdges(prevEdge.EndHeading(), prevEdge.GetTravelMode(), prevEdge.GetRoadClass())))
            {
                return true;
            }
        }
        else if (prevEdge.IsHighway() && currEdge.IsHighway()
                 && currEdge.IsWiderForward(Util.GetTurnDegree(prevEdge.EndHeading(), currEdge.BeginHeading()))
                 && node.HasWiderForwardTraversableHighwayXEdge(prevEdge.EndHeading(), prevEdge.GetTravelMode()))
        {
            return true;
        }
        else if (((int)prevEdge.GetRoadClass() >= (int)currEdge.GetRoadClass()) && !prevEdge.IsRampUse()
                 && !prevEdge.IsTurnChannelUse() && !prevEdge.IsFerryUse() && !prevEdge.IsRailFerryUse()
                 && !currEdge.IsRampUse() && !currEdge.IsTurnChannelUse() && !currEdge.IsFerryUse()
                 && !currEdge.IsRailFerryUse()
                 && currEdge.IsForkForward(Util.GetTurnDegree(prevEdge.EndHeading(), currEdge.BeginHeading()))
                 && node.HasOnlyForwardTraversableRoadClassXEdges(prevEdge.EndHeading(), prevEdge.GetTravelMode(), prevEdge.GetRoadClass()))
        {
            return true;
        }
        else if (currEdge.IsForkForward(Util.GetTurnDegree(prevEdge.EndHeading(), currEdge.BeginHeading()))
                 && !prevEdge.IsRampUse() && !prevEdge.IsTurnChannelUse() && !prevEdge.IsFerryUse()
                 && !prevEdge.IsRailFerryUse() && !currEdge.IsRampUse() && !currEdge.IsTurnChannelUse()
                 && !currEdge.IsFerryUse() && !currEdge.IsRailFerryUse()
                 && node.HasRoadForkTraversableIntersectingEdge(prevEdge.EndHeading(), prevEdge.GetTravelMode(),
                     (prevEdge.GetRoadClass() == RoadClass.ServiceOther) || (currEdge.GetRoadClass() == RoadClass.ServiceOther)))
        {
            return true;
        }
        else if (node.IntersectingEdgeSize() == 1)
        {
            EnhancedTripLeg_IntersectingEdge xedge = node.GetIntersectingEdge(0);

            if (prevEdge.IsHighway()
                && ((currEdge.IsHighway() && (xedge.GetUse() == Use.Ramp))
                    || (xedge.IsHighway() && currEdge.IsRampUse()))
                && HasLaneBifurcation(nodeIndex, prevEdge, currEdge, xedge)
                && prevEdge.IsForkForward(Util.GetTurnDegree(prevEdge.EndHeading(), currEdge.BeginHeading()))
                && prevEdge.IsForkForward(Util.GetTurnDegree(prevEdge.EndHeading(), xedge.BeginHeading())))
            {
                return true;
            }
        }

        return false;
    }

    // Faithful port of the has_lane_bifurcation lambda in IsFork.
    private bool HasLaneBifurcation(int nodeIndex, EnhancedTripLeg_Edge prevEdge, EnhancedTripLeg_Edge currEdge, EnhancedTripLeg_IntersectingEdge xedge)
    {
        uint prevLaneCount = prevEdge.LaneCount();
        uint currLaneCount = currEdge.LaneCount();

        // Going from N+1 lanes to N lanes. Is this really a highway bifurcation or just a
        // deceleration lane for an exit?
        if ((currLaneCount > 1) && (prevLaneCount == currLaneCount + 1) && (xedge.GetUse() == Use.Ramp))
        {
            int delta = 1;
            EnhancedTripLeg_Edge? prevAtDelta = TripPath.GetPrevEdge(nodeIndex, delta);
            uint origPrevAtDeltaLaneCount = prevAtDelta!.LaneCount();
            (float minDecelerationLaneLengthKm, float maxDecelerationLaneLengthKm) =
                GetDecelerationLaneLength(prevAtDelta.DefaultSpeed());
            float aggLaneLengthKm = prevAtDelta.LengthKm();

            while (true)
            {
                delta++;
                prevAtDelta = TripPath.GetPrevEdge(nodeIndex, delta);
                if (prevAtDelta == null)
                {
                    aggLaneLengthKm = 0.0f;
                    break;
                }

                uint prevAtDeltaLaneCount = prevAtDelta.LaneCount();
                bool extraLaneGoesAway = prevAtDeltaLaneCount < origPrevAtDeltaLaneCount;
                if (extraLaneGoesAway)
                {
                    break;
                }

                aggLaneLengthKm += prevAtDelta.LengthKm();

                if (aggLaneLengthKm > maxDecelerationLaneLengthKm)
                {
                    break;
                }
            }

            if ((aggLaneLengthKm < maxDecelerationLaneLengthKm) && (aggLaneLengthKm > minDecelerationLaneLengthKm))
            {
                return false;
            }
        }

        uint postSplitMinCount = (prevLaneCount + 1) / 2;
        uint xedgeLaneCount = xedge.LaneCount();
        if ((prevLaneCount == 2) && (currLaneCount == 1) && (xedgeLaneCount == 1))
        {
            return true;
        }
        else if ((prevLaneCount > 2) && (currLaneCount == postSplitMinCount) && (xedgeLaneCount == postSplitMinCount))
        {
            return true;
        }

        return false;
    }

    /// <summary>Returns true if a pedestrian fork. Faithful port of <c>IsPedestrianFork()</c>.</summary>
    protected bool IsPedestrianFork(int nodeIndex, EnhancedTripLeg_Edge prevEdge, EnhancedTripLeg_Edge currEdge)
    {
        static bool IsRelativeStraight(uint turnDegree) => turnDegree > 315 || turnDegree < 45;

        EnhancedTripLeg_Node node = TripPath.GetEnhancedNode(nodeIndex);
        uint pathTurnDegree = Util.GetTurnDegree(prevEdge.EndHeading(), currEdge.BeginHeading());
        bool isPedestrianTravelMode = (prevEdge.GetTravelMode() == TravelMode.Pedestrian)
            && (currEdge.GetTravelMode() == TravelMode.Pedestrian);

        if (isPedestrianTravelMode && IsRelativeStraight(pathTurnDegree) && (node.IntersectingEdgeSize() < 3))
        {
            var xedgeCounts = new IntersectingEdgeCounts();
            node.CalculateRightLeftIntersectingEdgeCounts(prevEdge.EndHeading(), prevEdge.GetTravelMode(), ref xedgeCounts);

            var xedgeUse = new UseBox();
            uint straightestTraversableXedgeTurnDegree =
                node.GetStraightestTraversableIntersectingEdgeTurnDegree(prevEdge.EndHeading(), prevEdge.GetTravelMode(), xedgeUse);

            bool isCurrentAndIntersectingEdgeOfSimilarUse =
                xedgeUse.HasValue
                && (currEdge.GetUse() == xedgeUse.Value
                    || (currEdge.IsFootwayUse() && (xedgeUse.Value == Use.PedestrianCrossing || xedgeUse.Value == Use.Footway)));

            if ((((xedgeCounts.LeftSimilarTraversableOutbound > 0) || (xedgeCounts.RightSimilarTraversableOutbound > 0))
                 || IsRelativeStraight(straightestTraversableXedgeTurnDegree))
                && isCurrentAndIntersectingEdgeOfSimilarUse)
            {
                return true;
            }

            if (prevEdge.Roundabout() && !currEdge.Roundabout())
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Returns true if a 'T' intersection. Faithful port of <c>IsTee()</c>.</summary>
    protected bool IsTee(int nodeIndex, EnhancedTripLeg_Edge prevEdge, EnhancedTripLeg_Edge currEdge, bool prevEdgeHasCommonBaseName)
    {
        EnhancedTripLeg_Node node = TripPath.GetEnhancedNode(nodeIndex);

        // Verify only one intersecting edge
        if (node.IntersectingEdgeSize() == 1)
        {
            Turn.Type turnType = Turn.GetType(Util.GetTurnDegree(prevEdge.EndHeading(), currEdge.BeginHeading()));
            Turn.Type xturnType = Turn.GetType(Util.GetTurnDegree(prevEdge.EndHeading(), node.IntersectingEdge(0).BeginHeading));

            // Intersecting edge must be traversable
            if (!node.GetIntersectingEdge(0).IsTraversable(prevEdge.GetTravelMode()))
            {
                return false;
            }

            if (prevEdgeHasCommonBaseName && !node.HasTraversableExcludeUseXEdge(prevEdge.GetTravelMode(), Use.Track))
            {
                return false;
            }

            if ((turnType == Turn.Type.Right) && (xturnType == Turn.Type.Left))
            {
                return true;
            }
            else if ((turnType == Turn.Type.Left) && (xturnType == Turn.Type.Right))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Returns true if a left pencil point u-turn. Faithful port of <c>IsLeftPencilPointUturn()</c>.</summary>
    protected bool IsLeftPencilPointUturn(int nodeIndex, EnhancedTripLeg_Edge prevEdge, EnhancedTripLeg_Edge currEdge)
    {
        uint turnDegree = Util.GetTurnDegree(prevEdge.EndHeading(), currEdge.BeginHeading());

        if (currEdge.DriveOnRight() && (turnDegree > 179) && (turnDegree < 226)
            && prevEdge.IsOneway() && currEdge.IsOneway())
        {
            var xedgeCounts = new IntersectingEdgeCounts();
            EnhancedTripLeg_Node node = TripPath.GetEnhancedNode(nodeIndex);
            node.CalculateRightLeftIntersectingEdgeCounts(prevEdge.EndHeading(), prevEdge.GetTravelMode(), ref xedgeCounts);

            StreetNames prevEdgeNames = StreetNamesFactory.Create(TripPath.GetCountryCode(nodeIndex), prevEdge.GetNameList());
            StreetNames currEdgeNames = StreetNamesFactory.Create(TripPath.GetCountryCode(nodeIndex), currEdge.GetNameList());
            StreetNames commonBaseNames = prevEdgeNames.FindCommonBaseNames(currEdgeNames);

            if ((xedgeCounts.LeftTraversableOutbound == 0) && commonBaseNames.Count != 0)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Returns true if a right pencil point u-turn. Faithful port of <c>IsRightPencilPointUturn()</c>.</summary>
    protected bool IsRightPencilPointUturn(int nodeIndex, EnhancedTripLeg_Edge prevEdge, EnhancedTripLeg_Edge currEdge)
    {
        uint turnDegree = Util.GetTurnDegree(prevEdge.EndHeading(), currEdge.BeginHeading());

        if (currEdge.DriveOnRight() && (turnDegree > 134) && (turnDegree < 181)
            && prevEdge.IsOneway() && currEdge.IsOneway())
        {
            var xedgeCounts = new IntersectingEdgeCounts();
            EnhancedTripLeg_Node node = TripPath.GetEnhancedNode(nodeIndex);
            node.CalculateRightLeftIntersectingEdgeCounts(prevEdge.EndHeading(), prevEdge.GetTravelMode(), ref xedgeCounts);

            StreetNames prevEdgeNames = StreetNamesFactory.Create(TripPath.GetCountryCode(nodeIndex), prevEdge.GetNameList());
            StreetNames currEdgeNames = StreetNamesFactory.Create(TripPath.GetCountryCode(nodeIndex), currEdge.GetNameList());
            StreetNames commonBaseNames = prevEdgeNames.FindCommonBaseNames(currEdgeNames);

            if ((xedgeCounts.RightTraversableOutbound == 0) && commonBaseNames.Count != 0)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Returns true if an intersecting forward edge. Faithful port of <c>IsIntersectingForwardEdge()</c>.</summary>
    protected bool IsIntersectingForwardEdge(int nodeIndex, EnhancedTripLeg_Edge prevEdge, EnhancedTripLeg_Edge currEdge)
    {
        EnhancedTripLeg_Node node = TripPath.GetEnhancedNode(nodeIndex);
        uint turnDegree = Util.GetTurnDegree(prevEdge.EndHeading(), currEdge.BeginHeading());

        if (node.HasIntersectingEdges() && !node.IsMotorwayJunction() && !node.Fork()
            && !(currEdge.IsHighway() && prevEdge.IsHighway()))
        {
            if (!currEdge.IsForward(turnDegree)
                && node.HasForwardTraversableExcludeUseXEdge(prevEdge.EndHeading(), prevEdge.GetTravelMode(), Use.Track))
            {
                return true;
            }
            else if (currEdge.IsForward(turnDegree)
                     && node.HasForwardTraversableSignificantRoadClassXEdge(prevEdge.EndHeading(), prevEdge.GetTravelMode(), prevEdge.GetRoadClass())
                     && !currEdge.IsStraightest(turnDegree,
                         node.GetStraightestTraversableIntersectingEdgeTurnDegree(prevEdge.EndHeading(), prevEdge.GetTravelMode())))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Calculates and sets the begin relative direction. Faithful port of <c>DetermineRelativeDirection(Maneuver&amp;)</c>.</summary>
    protected void DetermineRelativeDirection(Maneuver maneuver)
    {
        EnhancedTripLeg_Edge prevEdge = TripPath.GetPrevEdge((int)maneuver.BeginNodeIndex())!;
        EnhancedTripLeg_Edge currEdge = TripPath.GetCurrEdge((int)maneuver.BeginNodeIndex())!;

        var xedgeCounts = new IntersectingEdgeCounts();
        EnhancedTripLeg_Node node = TripPath.GetEnhancedNode((int)maneuver.BeginNodeIndex());
        node.CalculateRightLeftIntersectingEdgeCounts(prevEdge.EndHeading(), prevEdge.GetTravelMode(), ref xedgeCounts);

        Maneuver.RelativeDirection relativeDirection = DetermineRelativeDirection(maneuver.TurnDegree());
        maneuver.SetBeginRelativeDirection(relativeDirection);

        // Adjust keep straight, if needed
        if (relativeDirection == Maneuver.RelativeDirection.KeepStraight)
        {
            if ((xedgeCounts.RightSimilarTraversableOutbound == 0) && (xedgeCounts.LeftSimilarTraversableOutbound > 0))
            {
                maneuver.SetBeginRelativeDirection(Maneuver.RelativeDirection.KeepRight);
            }
            else if ((xedgeCounts.RightSimilarTraversableOutbound > 0) && (xedgeCounts.LeftSimilarTraversableOutbound == 0))
            {
                maneuver.SetBeginRelativeDirection(Maneuver.RelativeDirection.KeepLeft);
            }
            else if ((xedgeCounts.LeftSimilarTraversableOutbound == 0) && (xedgeCounts.LeftTraversableOutbound > 0)
                     && (xedgeCounts.RightTraversableOutbound == 0))
            {
                if (!currEdge.IsStraightest(maneuver.TurnDegree(),
                        node.GetStraightestTraversableIntersectingEdgeTurnDegree(prevEdge.EndHeading(), prevEdge.GetTravelMode())))
                {
                    maneuver.SetBeginRelativeDirection(Maneuver.RelativeDirection.KeepRight);
                }
                else if (maneuver.TurnChannel() && (Turn.GetType(maneuver.TurnDegree()) != Turn.Type.Straight))
                {
                    maneuver.SetBeginRelativeDirection(Maneuver.RelativeDirection.KeepRight);
                }
                else if (maneuver.Fork())
                {
                    maneuver.SetBeginRelativeDirection(Maneuver.RelativeDirection.KeepRight);
                }
            }
            else if ((xedgeCounts.RightSimilarTraversableOutbound == 0) && (xedgeCounts.RightTraversableOutbound > 0)
                     && (xedgeCounts.LeftTraversableOutbound == 0))
            {
                if (!currEdge.IsStraightest(maneuver.TurnDegree(),
                        node.GetStraightestTraversableIntersectingEdgeTurnDegree(prevEdge.EndHeading(), prevEdge.GetTravelMode())))
                {
                    maneuver.SetBeginRelativeDirection(Maneuver.RelativeDirection.KeepLeft);
                }
                else if (maneuver.TurnChannel() && (Turn.GetType(maneuver.TurnDegree()) != Turn.Type.Straight))
                {
                    maneuver.SetBeginRelativeDirection(Maneuver.RelativeDirection.KeepLeft);
                }
                else if (maneuver.Fork())
                {
                    maneuver.SetBeginRelativeDirection(Maneuver.RelativeDirection.KeepLeft);
                }
            }
        }
        else if ((relativeDirection == Maneuver.RelativeDirection.Left)
                 && (Turn.GetType(maneuver.TurnDegree()) == Turn.Type.SlightLeft)
                 && node.HasSpecifiedTurnXEdge(Turn.Type.Left, prevEdge.EndHeading(), maneuver.GetTravelMode()))
        {
            maneuver.SetBeginRelativeDirection(Maneuver.RelativeDirection.KeepLeft);
        }
        else if ((relativeDirection == Maneuver.RelativeDirection.Right)
                 && (Turn.GetType(maneuver.TurnDegree()) == Turn.Type.SlightRight)
                 && node.HasSpecifiedTurnXEdge(Turn.Type.Right, prevEdge.EndHeading(), maneuver.GetTravelMode()))
        {
            maneuver.SetBeginRelativeDirection(Maneuver.RelativeDirection.KeepRight);
        }
    }

    /// <summary>Maps a turn degree to a relative direction. Faithful port of <c>DetermineRelativeDirection(uint32_t)</c>.</summary>
    public static Maneuver.RelativeDirection DetermineRelativeDirection(uint turnDegree)
    {
        if ((turnDegree > 329) || (turnDegree < 31))
        {
            return Maneuver.RelativeDirection.KeepStraight;
        }
        else if ((turnDegree > 30) && (turnDegree < 160))
        {
            return Maneuver.RelativeDirection.Right;
        }
        else if ((turnDegree > 159) && (turnDegree < 201))
        {
            return Maneuver.RelativeDirection.Reverse;
        }
        else if ((turnDegree > 200) && (turnDegree < 330))
        {
            return Maneuver.RelativeDirection.Left;
        }
        else
        {
            return Maneuver.RelativeDirection.None;
        }
    }

    /// <summary>Returns true if the internal intersection name is usable. Faithful port of <c>UsableInternalIntersectionName()</c>.</summary>
    protected bool UsableInternalIntersectionName(Maneuver maneuver, int nodeIndex)
    {
        EnhancedTripLeg_Edge prevEdge = TripPath.GetPrevEdge(nodeIndex)!;
        EnhancedTripLeg_Edge? prevPrevEdge = TripPath.GetPrevEdge(nodeIndex, 2);
        uint prevPrev2PrevTurnDegree = 0;
        if (prevPrevEdge != null)
        {
            prevPrev2PrevTurnDegree = Util.GetTurnDegree(prevPrevEdge.EndHeading(), prevEdge.BeginHeading());
        }

        Maneuver.RelativeDirection relativeDirection = DetermineRelativeDirection(prevPrev2PrevTurnDegree);

        if (maneuver.InternalIntersection()
            && ((prevEdge.DriveOnRight() && (relativeDirection == Maneuver.RelativeDirection.Left))
                || (!prevEdge.DriveOnRight() && (relativeDirection == Maneuver.RelativeDirection.Right))))
        {
            return true;
        }

        return false;
    }

    /// <summary>Updates the internal left/right turn counts. Faithful port of <c>UpdateInternalTurnCount()</c>.</summary>
    protected void UpdateInternalTurnCount(Maneuver maneuver, int nodeIndex)
    {
        EnhancedTripLeg_Edge prevEdge = TripPath.GetPrevEdge(nodeIndex)!;
        EnhancedTripLeg_Edge? prevPrevEdge = TripPath.GetPrevEdge(nodeIndex, 2);
        uint prevPrev2PrevTurnDegree = 0;
        if (prevPrevEdge != null)
        {
            prevPrev2PrevTurnDegree = Util.GetTurnDegree(prevPrevEdge.EndHeading(), prevEdge.BeginHeading());
        }

        Maneuver.RelativeDirection relativeDirection = DetermineRelativeDirection(prevPrev2PrevTurnDegree);

        if (relativeDirection == Maneuver.RelativeDirection.Right)
        {
            maneuver.SetInternalRightTurnCount(maneuver.InternalRightTurnCount() + 1);
        }

        if (relativeDirection == Maneuver.RelativeDirection.Left)
        {
            maneuver.SetInternalLeftTurnCount(maneuver.InternalLeftTurnCount() + 1);
        }
    }

    /// <summary>Returns the speed for the travel mode. Faithful port of <c>GetSpeed()</c>.</summary>
    protected float GetSpeed(TravelMode travelMode, float edgeSpeed)
    {
        if (travelMode == TravelMode.Pedestrian)
        {
            return 5.1f;
        }
        else if (travelMode == TravelMode.Bicycle)
        {
            return 20.0f;
        }
        else
        {
            return edgeSpeed;
        }
    }

    /// <summary>Returns true if the current turn channel maneuver can be combined. Faithful port of <c>IsTurnChannelManeuverCombinable()</c>.</summary>
    protected bool IsTurnChannelManeuverCombinable(
        LinkedListNode<Maneuver> prevMan,
        LinkedListNode<Maneuver> currMan,
        LinkedListNode<Maneuver> nextMan,
        bool startMan)
    {
        Maneuver curr = currMan.Value;
        Maneuver next = nextMan.Value;

        if (curr.TurnChannel() && (currMan != nextMan) && !next.IsDestinationType())
        {
            uint newTurnDegree;
            if (startMan)
            {
                newTurnDegree = Util.GetTurnDegree(curr.EndHeading(), next.BeginHeading());
            }
            else
            {
                newTurnDegree = Util.GetTurnDegree(prevMan.Value.EndHeading(), next.BeginHeading());
            }

            Turn.Type newTurnType = Turn.GetType(newTurnDegree);

            int turnChannelEndNodeIndex = (int)curr.EndNodeIndex();
            EnhancedTripLeg_Node node = TripPath.GetEnhancedNode(turnChannelEndNodeIndex);
            EnhancedTripLeg_Edge? prevEdge = TripPath.GetPrevEdge(turnChannelEndNodeIndex);
            EnhancedTripLeg_Edge? currEdge = TripPath.GetCurrEdge(turnChannelEndNodeIndex);

            if (prevEdge == null || currEdge == null)
            {
                return false;
            }

            uint postTurnChannelTurnDegree = Util.GetTurnDegree(prevEdge.EndHeading(), currEdge.BeginHeading());

            static bool IsWithinTurnChannelRange(uint turnDegree)
                => (turnDegree >= TurnChannelTurnDegreeLowerBound) || (turnDegree <= TurnChannelTurnDegreeUpperBound);

            bool commonTurnChannelCriteria =
                (curr.Length() <= (GraphConstants.MaxTurnChannelLength * Constants.KmPerMeter))
                && !node.HasForwardTraversableIntersectingEdge(curr.EndHeading(), curr.GetTravelMode())
                && (IsWithinTurnChannelRange(postTurnChannelTurnDegree) || (curr.Length() < ShortTurnChannelThreshold));

            if (!commonTurnChannelCriteria)
            {
                return false;
            }

            // Process simple right turn channel
            if (((curr.BeginRelativeDirection() == Maneuver.RelativeDirection.KeepRight)
                 || (curr.BeginRelativeDirection() == Maneuver.RelativeDirection.Right))
                && (next.BeginRelativeDirection() != Maneuver.RelativeDirection.Left)
                && ((newTurnType == Turn.Type.SlightRight) || (newTurnType == Turn.Type.Right)
                    || (newTurnType == Turn.Type.SharpRight) || (newTurnType == Turn.Type.Reverse)
                    || (newTurnType == Turn.Type.Straight)))
            {
                return true;
            }

            // Process simple left turn channel
            if (((curr.BeginRelativeDirection() == Maneuver.RelativeDirection.KeepLeft)
                 || (curr.BeginRelativeDirection() == Maneuver.RelativeDirection.Left))
                && (next.BeginRelativeDirection() != Maneuver.RelativeDirection.Right)
                && ((newTurnType == Turn.Type.SlightLeft) || (newTurnType == Turn.Type.Left)
                    || (newTurnType == Turn.Type.SharpLeft) || (newTurnType == Turn.Type.Reverse)
                    || (newTurnType == Turn.Type.Straight)))
            {
                return true;
            }

            // Process simple straight "turn channel"
            if ((curr.BeginRelativeDirection() == Maneuver.RelativeDirection.KeepStraight)
                && (newTurnType == Turn.Type.Straight))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Returns true if the current and next ramp maneuvers can be combined. Faithful port of <c>AreRampManeuversCombinable()</c>.</summary>
    protected bool AreRampManeuversCombinable(LinkedListNode<Maneuver> currMan, LinkedListNode<Maneuver> nextMan)
    {
        Maneuver curr = currMan.Value;
        Maneuver next = nextMan.Value;
        if (curr.Ramp() && next.Ramp() && !next.Fork()
            && !curr.InternalIntersection() && !next.InternalIntersection())
        {
            EnhancedTripLeg_Node node = TripPath.GetEnhancedNode((int)next.BeginNodeIndex());
            if (!node.HasTraversableOutboundIntersectingEdge(next.GetTravelMode())
                || node.IsStraightestTraversableIntersectingEdgeReversed(curr.EndHeading(), next.GetTravelMode())
                || (next.Type() == DirectionsLegManeuverType.RampStraight))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Returns true if the next maneuver is obvious and combinable. Faithful port of <c>IsNextManeuverObvious()</c>.</summary>
    protected bool IsNextManeuverObvious(LinkedList<Maneuver> maneuvers, LinkedListNode<Maneuver> currMan, LinkedListNode<Maneuver> nextMan)
    {
        Maneuver curr = currMan.Value;
        Maneuver next = nextMan.Value;

        if (next.Type() == DirectionsLegManeuverType.Continue)
        {
            EnhancedTripLeg_Node node = TripPath.GetEnhancedNode((int)next.BeginNodeIndex());

            // Return true if there are no traversable intersecting edges
            if (!node.HasTraversableIntersectingEdge(next.GetTravelMode()))
            {
                return true;
            }

            // Return false if the maneuver has an exit number
            if (next.HasExitNumberSign())
            {
                return false;
            }

            // Process ramp forks
            if (curr.Ramp() && curr.Fork() && !curr.ContainsObviousManeuver())
            {
                if (curr.Type() == DirectionsLegManeuverType.StayStraight)
                {
                    return true;
                }
                else
                {
                    var xedgeCounts = new IntersectingEdgeCounts();
                    node.CalculateRightLeftIntersectingEdgeCounts(curr.EndHeading(), curr.GetTravelMode(), ref xedgeCounts);

                    if ((curr.Type() == DirectionsLegManeuverType.StayLeft) && (xedgeCounts.Left == 0))
                    {
                        return true;
                    }

                    if ((curr.Type() == DirectionsLegManeuverType.StayRight) && (xedgeCounts.Right == 0))
                    {
                        return true;
                    }
                }

                return false;
            }

            // Return true if a short continue maneuver and the following maneuver is not a continue
            if (next.Length() < ShortContinueThreshold)
            {
                LinkedListNode<Maneuver>? nextNextMan = nextMan.Next;
                if ((nextNextMan != null) && (nextNextMan.Value.Type() != DirectionsLegManeuverType.Continue))
                {
                    return true;
                }
            }

            // Return false at motorway junction
            if (node.GetNodeType() == NodeType.MotorWayJunction)
            {
                return false;
            }

            // Return true if not a non-backward traversable same name intersecting edge
            if (!node.HasNonBackwardTraversableSameNameRampIntersectingEdge(curr.EndHeading(), next.GetTravelMode()))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Returns true if roundabouts are processable for the travel mode. Faithful port of <c>AreRoundaboutsProcessable()</c>.</summary>
    protected bool AreRoundaboutsProcessable(TravelMode travelMode)
    {
        if ((travelMode == TravelMode.Drive) || (travelMode == TravelMode.Bicycle))
        {
            return true;
        }

        return false;
    }

    /// <summary>Reviews roundabouts and sets roundabout name + exit info. Faithful port of <c>ProcessRoundabouts()</c>.</summary>
    protected void ProcessRoundabouts(LinkedList<Maneuver> maneuvers)
    {
        LinkedListNode<Maneuver>? prevMan = maneuvers.First;
        LinkedListNode<Maneuver>? currMan = maneuvers.First;
        LinkedListNode<Maneuver>? nextMan = maneuvers.First;
        if (nextMan != null)
        {
            nextMan = nextMan.Next;
            currMan = nextMan;
        }

        if (nextMan != null)
        {
            nextMan = nextMan.Next;
        }

        while (nextMan != null)
        {
            if (currMan!.Value.Roundabout())
            {
                // Get the non route numbers for the roundabout
                StreetNames nonRouteNumbers = currMan.Value.StreetNames().GetNonRouteNumbers();

                // Clear out the current street name values
                currMan.Value.ClearStreetNames();
                currMan.Value.ClearBeginStreetNames();

                if (nonRouteNumbers.Count != 0)
                {
                    StreetNames prevCommonBaseNames = nonRouteNumbers.FindCommonBaseNames(prevMan!.Value.StreetNames());
                    StreetNames nextCommonBaseNames = nonRouteNumbers.FindCommonBaseNames(nextMan.Value.StreetNames());
                    if (prevCommonBaseNames.Count == 0 && nextCommonBaseNames.Count == 0)
                    {
                        currMan.Value.SetStreetNames(nonRouteNumbers);
                    }
                }

                // Process roundabout exit names and signs
                if (nextMan.Value.Type() == DirectionsLegManeuverType.RoundaboutExit)
                {
                    if (nextMan.Value.HasBeginStreetNames())
                    {
                        if (nextMan.Value.ContainsObviousManeuver())
                        {
                            currMan.Value.SetRoundaboutExitStreetNames(nextMan.Value.BeginStreetNames().Clone());
                        }
                        else
                        {
                            currMan.Value.SetRoundaboutExitBeginStreetNames(nextMan.Value.BeginStreetNames().Clone());
                            currMan.Value.SetRoundaboutExitStreetNames(nextMan.Value.StreetNames().Clone());
                        }
                    }
                    else
                    {
                        currMan.Value.SetRoundaboutExitStreetNames(nextMan.Value.StreetNames().Clone());
                    }

                    if (nextMan.Value.HasSigns())
                    {
                        currMan.Value.MutableRoundaboutExitSigns().CopyFrom(nextMan.Value.GetSigns());
                    }

                    // Suppress roundabout exit maneuver if user requested
                    if (!_options.RoundaboutExits)
                    {
                        currMan.Value.SetHasCombinedEnterExitRoundabout(true);
                        currMan.Value.SetRoundaboutLength(currMan.Value.Length());
                        currMan.Value.SetRoundaboutExitLength(nextMan.Value.Length());
                        currMan.Value.SetRoundaboutExitBeginHeading(nextMan.Value.BeginHeading());
                        currMan.Value.SetRoundaboutExitTurnDegree(nextMan.Value.TurnDegree());
                        currMan.Value.SetRoundaboutExitShapeIndex(currMan.Value.EndShapeIndex());
                        currMan.Value.SetHasLeftTraversableOutboundIntersectingEdge(
                            nextMan.Value.HasLeftTraversableOutboundIntersectingEdge());
                        currMan.Value.SetHasRightTraversableOutboundIntersectingEdge(
                            nextMan.Value.HasRightTraversableOutboundIntersectingEdge());

                        nextMan = CombineManeuvers(maneuvers, currMan, nextMan);
                    }
                }
            }

            // on to the next maneuver...
            prevMan = currMan;
            currMan = nextMan;
            nextMan = nextMan.Next;
        }
    }

    /// <summary>Sets the 'to stay on' attribute. Faithful port of <c>SetToStayOnAttribute()</c>.</summary>
    protected void SetToStayOnAttribute(LinkedList<Maneuver> maneuvers)
    {
        LinkedListNode<Maneuver>? prevMan = maneuvers.First;
        LinkedListNode<Maneuver>? currMan = maneuvers.First;
        LinkedListNode<Maneuver>? nextMan = maneuvers.First;
        if (nextMan != null)
        {
            nextMan = nextMan.Next;
            currMan = nextMan;
        }

        if (nextMan != null)
        {
            nextMan = nextMan.Next;
        }

        while (nextMan != null)
        {
            Maneuver curr = currMan!.Value;
            Maneuver prev = prevMan!.Value;
            switch (curr.Type())
            {
                case DirectionsLegManeuverType.SlightRight:
                case DirectionsLegManeuverType.SlightLeft:
                case DirectionsLegManeuverType.Right:
                case DirectionsLegManeuverType.SharpRight:
                case DirectionsLegManeuverType.SharpLeft:
                case DirectionsLegManeuverType.Left:
                    if (!curr.HasBeginStreetNames() && curr.HasSimilarNames(prev, true))
                    {
                        curr.SetToStayOn(true);
                    }

                    break;
                case DirectionsLegManeuverType.StayStraight:
                case DirectionsLegManeuverType.StayRight:
                case DirectionsLegManeuverType.StayLeft:
                    if (curr.HasSimilarNames(prev, true))
                    {
                        if (!curr.Ramp())
                        {
                            curr.SetToStayOn(true);
                        }
                        else if (curr.HasSimilarNames(nextMan.Value, true))
                        {
                            curr.SetToStayOn(true);
                        }
                    }

                    break;
                case DirectionsLegManeuverType.UturnRight:
                case DirectionsLegManeuverType.UturnLeft:
                    if (curr.HasSameNames(prev, true))
                    {
                        curr.SetToStayOn(true);
                    }

                    break;
                default:
                    break;
            }

            prevMan = currMan;
            currMan = nextMan;
            nextMan = nextMan.Next;
        }
    }

    /// <summary>Enhances signless interchange maneuvers. Faithful port of <c>EnhanceSignlessInterchnages()</c>.</summary>
    protected void EnhanceSignlessInterchnages(LinkedList<Maneuver> maneuvers)
    {
        LinkedListNode<Maneuver>? prevMan = maneuvers.First;
        LinkedListNode<Maneuver>? currMan = maneuvers.First;
        LinkedListNode<Maneuver>? nextMan = maneuvers.First;
        if (nextMan != null)
        {
            nextMan = nextMan.Next;
        }

        while (nextMan != null)
        {
            Maneuver curr = currMan!.Value;
            Maneuver prev = prevMan!.Value;
            Maneuver next = nextMan.Value;

            if ((curr.Ramp() || (curr.Fork() && !curr.HasStreetNames()))
                && !curr.HasExitSign() && !(prev.Ramp() || prev.Fork())
                && next.IsMergeType() && next.HasStreetNames())
            {
                StreetName front = next.StreetNames()[0];
                curr.MutableSigns().MutableExitBranchList().Add(new OdinSign(front.Value, front.IsRouteNumber, front.GetPronunciation()));
            }

            prevMan = currMan;
            currMan = nextMan;
            nextMan = nextMan.Next;
        }
    }

    /// <summary>Returns the expected turn lane direction for the maneuver. Faithful port of <c>GetExpectedTurnLaneDirection()</c>.</summary>
    protected ushort GetExpectedTurnLaneDirection(EnhancedTripLeg_Edge? turnLaneEdge, Maneuver maneuver)
    {
        if (turnLaneEdge != null)
        {
            switch (maneuver.Type())
            {
                case DirectionsLegManeuverType.UturnLeft:
                    if (turnLaneEdge.HasTurnLane(TurnLaneConstants.TurnLaneReverse))
                    {
                        return TurnLaneConstants.TurnLaneReverse;
                    }
                    else if (turnLaneEdge.HasTurnLane(TurnLaneConstants.TurnLaneLeft))
                    {
                        return TurnLaneConstants.TurnLaneLeft;
                    }

                    break;
                case DirectionsLegManeuverType.SharpLeft:
                    if (turnLaneEdge.HasTurnLane(TurnLaneConstants.TurnLaneSharpLeft))
                    {
                        return TurnLaneConstants.TurnLaneSharpLeft;
                    }
                    else if (turnLaneEdge.HasTurnLane(TurnLaneConstants.TurnLaneLeft))
                    {
                        return TurnLaneConstants.TurnLaneLeft;
                    }

                    break;
                case DirectionsLegManeuverType.Left:
                    if (turnLaneEdge.HasTurnLane(TurnLaneConstants.TurnLaneLeft))
                    {
                        return TurnLaneConstants.TurnLaneLeft;
                    }
                    else if (turnLaneEdge.HasTurnLane(TurnLaneConstants.TurnLaneSlightLeft) && (maneuver.TurnDegree() > 270))
                    {
                        return TurnLaneConstants.TurnLaneSlightLeft;
                    }
                    else if (turnLaneEdge.HasTurnLane(TurnLaneConstants.TurnLaneSharpLeft) && (maneuver.TurnDegree() < 270))
                    {
                        return TurnLaneConstants.TurnLaneSharpLeft;
                    }

                    break;
                case DirectionsLegManeuverType.SlightLeft:
                case DirectionsLegManeuverType.ExitLeft:
                    if (turnLaneEdge.HasTurnLane(TurnLaneConstants.TurnLaneSlightLeft))
                    {
                        return TurnLaneConstants.TurnLaneSlightLeft;
                    }
                    else if (turnLaneEdge.HasTurnLane(TurnLaneConstants.TurnLaneLeft))
                    {
                        return TurnLaneConstants.TurnLaneLeft;
                    }

                    break;
                case DirectionsLegManeuverType.RampLeft:
                    if ((maneuver.BeginRelativeDirection() == Maneuver.RelativeDirection.Left)
                        && turnLaneEdge.HasTurnLane(TurnLaneConstants.TurnLaneLeft))
                    {
                        return TurnLaneConstants.TurnLaneLeft;
                    }
                    else if (turnLaneEdge.HasTurnLane(TurnLaneConstants.TurnLaneSlightLeft))
                    {
                        return TurnLaneConstants.TurnLaneSlightLeft;
                    }
                    else if (turnLaneEdge.HasTurnLane(TurnLaneConstants.TurnLaneLeft))
                    {
                        return TurnLaneConstants.TurnLaneLeft;
                    }

                    break;
                case DirectionsLegManeuverType.StayLeft:
                    if (turnLaneEdge.HasTurnLane(TurnLaneConstants.TurnLaneSlightLeft))
                    {
                        return TurnLaneConstants.TurnLaneSlightLeft;
                    }
                    else if (turnLaneEdge.HasTurnLane(TurnLaneConstants.TurnLaneLeft))
                    {
                        return TurnLaneConstants.TurnLaneLeft;
                    }
                    else if (turnLaneEdge.HasTurnLane(TurnLaneConstants.TurnLaneThrough)
                             && (turnLaneEdge.HasTurnLane(TurnLaneConstants.TurnLaneRight)
                                 || turnLaneEdge.HasTurnLane(TurnLaneConstants.TurnLaneSlightRight)))
                    {
                        return TurnLaneConstants.TurnLaneThrough;
                    }

                    break;
                case DirectionsLegManeuverType.Becomes:
                case DirectionsLegManeuverType.Continue:
                case DirectionsLegManeuverType.RampStraight:
                case DirectionsLegManeuverType.StayStraight:
                    return TurnLaneConstants.TurnLaneThrough;
                case DirectionsLegManeuverType.StayRight:
                    if (turnLaneEdge.HasTurnLane(TurnLaneConstants.TurnLaneSlightRight))
                    {
                        return TurnLaneConstants.TurnLaneSlightRight;
                    }
                    else if (turnLaneEdge.HasTurnLane(TurnLaneConstants.TurnLaneRight))
                    {
                        return TurnLaneConstants.TurnLaneRight;
                    }
                    else if (turnLaneEdge.HasTurnLane(TurnLaneConstants.TurnLaneThrough)
                             && (turnLaneEdge.HasTurnLane(TurnLaneConstants.TurnLaneLeft)
                                 || turnLaneEdge.HasTurnLane(TurnLaneConstants.TurnLaneSlightLeft)))
                    {
                        return TurnLaneConstants.TurnLaneThrough;
                    }

                    break;
                case DirectionsLegManeuverType.SlightRight:
                case DirectionsLegManeuverType.ExitRight:
                    if (turnLaneEdge.HasTurnLane(TurnLaneConstants.TurnLaneSlightRight))
                    {
                        return TurnLaneConstants.TurnLaneSlightRight;
                    }
                    else if (turnLaneEdge.HasTurnLane(TurnLaneConstants.TurnLaneRight))
                    {
                        return TurnLaneConstants.TurnLaneRight;
                    }

                    break;
                case DirectionsLegManeuverType.RampRight:
                    if ((maneuver.BeginRelativeDirection() == Maneuver.RelativeDirection.Right)
                        && turnLaneEdge.HasTurnLane(TurnLaneConstants.TurnLaneRight))
                    {
                        return TurnLaneConstants.TurnLaneRight;
                    }
                    else if (turnLaneEdge.HasTurnLane(TurnLaneConstants.TurnLaneSlightRight))
                    {
                        return TurnLaneConstants.TurnLaneSlightRight;
                    }
                    else if (turnLaneEdge.HasTurnLane(TurnLaneConstants.TurnLaneRight))
                    {
                        return TurnLaneConstants.TurnLaneRight;
                    }

                    break;
                case DirectionsLegManeuverType.Right:
                    if (turnLaneEdge.HasTurnLane(TurnLaneConstants.TurnLaneRight))
                    {
                        return TurnLaneConstants.TurnLaneRight;
                    }
                    else if (turnLaneEdge.HasTurnLane(TurnLaneConstants.TurnLaneSlightRight) && (maneuver.TurnDegree() < 90))
                    {
                        return TurnLaneConstants.TurnLaneSlightRight;
                    }
                    else if (turnLaneEdge.HasTurnLane(TurnLaneConstants.TurnLaneSharpRight) && (maneuver.TurnDegree() > 90))
                    {
                        return TurnLaneConstants.TurnLaneSharpRight;
                    }

                    break;
                case DirectionsLegManeuverType.SharpRight:
                    if (turnLaneEdge.HasTurnLane(TurnLaneConstants.TurnLaneSharpRight))
                    {
                        return TurnLaneConstants.TurnLaneSharpRight;
                    }
                    else if (turnLaneEdge.HasTurnLane(TurnLaneConstants.TurnLaneRight))
                    {
                        return TurnLaneConstants.TurnLaneRight;
                    }

                    break;
                case DirectionsLegManeuverType.UturnRight:
                    if (turnLaneEdge.HasTurnLane(TurnLaneConstants.TurnLaneReverse))
                    {
                        return TurnLaneConstants.TurnLaneReverse;
                    }
                    else if (turnLaneEdge.HasTurnLane(TurnLaneConstants.TurnLaneRight))
                    {
                        return TurnLaneConstants.TurnLaneRight;
                    }

                    break;
                default:
                    return TurnLaneConstants.TurnLaneNone;
            }
        }

        return TurnLaneConstants.TurnLaneNone;
    }

    /// <summary>Processes the turn lanes at maneuver points and within maneuvers. Faithful port of <c>ProcessTurnLanes()</c>.</summary>
    protected void ProcessTurnLanes(LinkedList<Maneuver> maneuvers)
    {
        LinkedListNode<Maneuver>? prevMan = maneuvers.First;
        LinkedListNode<Maneuver>? currMan = maneuvers.First;
        LinkedListNode<Maneuver>? nextMan = maneuvers.First;

        if (nextMan != null)
        {
            nextMan = nextMan.Next;
            currMan = nextMan;
        }

        if (nextMan != null)
        {
            nextMan = nextMan.Next;
        }

        while (currMan != null)
        {
            Maneuver curr = currMan.Value;
            if (curr.GetTravelMode() == TravelMode.Drive)
            {
                EnhancedTripLeg_Edge? prevEdge = TripPath.GetPrevEdge((int)curr.BeginNodeIndex());
                if (prevEdge != null && (prevEdge.TurnLanesSize() > 0))
                {
                    if (!((curr.Length() < ShortForkThreshold)
                          && ((curr.Type() == DirectionsLegManeuverType.StayLeft)
                              || (curr.Type() == DirectionsLegManeuverType.StayRight)
                              || (curr.Type() == DirectionsLegManeuverType.StayStraight))))
                    {
                        prevEdge.ActivateTurnLanes(GetExpectedTurnLaneDirection(prevEdge, curr),
                            curr.Length(), curr.Type(), nextMan != null ? nextMan.Value.Type() : DirectionsLegManeuverType.None);
                    }
                }

                bool hasDirectionalIntersectingEdge = false;
                float remainingStepDistance = 0.0f;
                if (prevEdge != null)
                {
                    remainingStepDistance += prevEdge.LengthKm();
                }

                // Assign turn lanes within step, walking backwards from end to begin node
                for (uint index = prevMan!.Value.EndNodeIndex() - 1; index > prevMan.Value.BeginNodeIndex(); --index)
                {
                    EnhancedTripLeg_Node node = TripPath.GetEnhancedNode((int)index);
                    EnhancedTripLeg_Edge? innerPrevEdge = TripPath.GetPrevEdge((int)index);
                    if (innerPrevEdge != null)
                    {
                        if (!hasDirectionalIntersectingEdge)
                        {
                            var xedgeCounts = new IntersectingEdgeCounts();
                            node.CalculateRightLeftIntersectingEdgeCounts(innerPrevEdge.EndHeading(), innerPrevEdge.GetTravelMode(), ref xedgeCounts);
                            if (xedgeCounts.RightTraversableOutbound > 0 && curr.IsRightType())
                            {
                                hasDirectionalIntersectingEdge = true;
                            }
                            else if (xedgeCounts.LeftTraversableOutbound > 0 && curr.IsLeftType())
                            {
                                hasDirectionalIntersectingEdge = true;
                            }
                        }

                        if (innerPrevEdge.TurnLanesSize() > 0)
                        {
                            ushort turnLaneDirection = GetExpectedTurnLaneDirection(innerPrevEdge, curr);
                            if (remainingStepDistance < UpcomingLanesThreshold
                                && !hasDirectionalIntersectingEdge && turnLaneDirection != TurnLaneConstants.TurnLaneNone)
                            {
                                innerPrevEdge.ActivateTurnLanes(turnLaneDirection, curr.Length(), curr.Type(),
                                    nextMan != null ? nextMan.Value.Type() : DirectionsLegManeuverType.None);
                            }
                            else
                            {
                                innerPrevEdge.ActivateTurnLanes(TurnLaneConstants.TurnLaneThrough, remainingStepDistance,
                                    prevMan.Value.Type(), curr.Type());
                            }
                        }

                        remainingStepDistance += innerPrevEdge.LengthKm();
                    }
                }
            }

            prevMan = currMan;
            currMan = nextMan;
            if (nextMan != null)
            {
                nextMan = nextMan.Next;
            }
        }
    }

    /// <summary>Processes guidance view junctions and signboards. Faithful port of <c>ProcessGuidanceViews()</c>.</summary>
    protected void ProcessGuidanceViews(LinkedList<Maneuver> maneuvers)
    {
        // PORT-NOTE (DEFER): guidance-view junction / signboard image matching reads osm_changeset and
        // the per-edge sign.guidance_view_junctions / guidance_view_signboards lists, which the ported
        // Thor TripSign does not carry. With no such data the C++ loops produce no guidance views, so
        // this is a faithful no-op for the structural foundation.
    }

    /// <summary>Returns true if the ramp leads to a highway. Faithful port of <c>RampLeadsToHighway()</c>.</summary>
    protected bool RampLeadsToHighway(Maneuver maneuver)
    {
        if (maneuver.Ramp())
        {
            for (int nodeIndex = (int)maneuver.EndNodeIndex(); nodeIndex < TripPath.GetLastNodeIndex(); ++nodeIndex)
            {
                EnhancedTripLeg_Edge? currEdge = TripPath.GetCurrEdge(nodeIndex);
                if (currEdge != null && (currEdge.IsRampUse() || currEdge.IsTurnChannelUse() || currEdge.InternalIntersection()))
                {
                    continue;
                }
                else if (currEdge != null && currEdge.IsHighway())
                {
                    return true;
                }
                else
                {
                    return false;
                }
            }
        }

        return false;
    }

    /// <summary>Marks maneuvers with traversable outbound intersecting edges. Faithful port of <c>SetTraversableOutboundIntersectingEdgeFlags()</c>.</summary>
    protected void SetTraversableOutboundIntersectingEdgeFlags(LinkedList<Maneuver> maneuvers)
    {
        foreach (Maneuver maneuver in maneuvers)
        {
            bool foundFirstEdgeToProcess = false;
            for (int nodeIndex = (int)maneuver.BeginNodeIndex(); nodeIndex < (int)maneuver.EndNodeIndex(); ++nodeIndex)
            {
                if (!foundFirstEdgeToProcess)
                {
                    EnhancedTripLeg_Edge currEdge = TripPath.GetCurrEdge(nodeIndex)!;
                    if (currEdge.InternalIntersection() || currEdge.IsTurnChannelUse())
                    {
                        continue;
                    }

                    foundFirstEdgeToProcess = true;
                    continue;
                }

                EnhancedTripLeg_Node node = TripPath.GetEnhancedNode(nodeIndex);
                EnhancedTripLeg_Edge? prevEdge = TripPath.GetPrevEdge(nodeIndex);
                if (prevEdge != null)
                {
                    var xedgeCounts = new IntersectingEdgeCounts();
                    node.CalculateRightLeftIntersectingEdgeCounts(prevEdge.EndHeading(), prevEdge.GetTravelMode(), ref xedgeCounts);
                    if (xedgeCounts.RightTraversableOutbound > 0)
                    {
                        maneuver.SetHasRightTraversableOutboundIntersectingEdge(true);
                    }

                    if (xedgeCounts.LeftTraversableOutbound > 0)
                    {
                        maneuver.SetHasLeftTraversableOutboundIntersectingEdge(true);
                    }

                    if (maneuver.HasRightTraversableOutboundIntersectingEdge()
                        && maneuver.HasLeftTraversableOutboundIntersectingEdge())
                    {
                        break;
                    }
                }
            }
        }
    }

    /// <summary>Moves straight internal edges to the previous maneuver. Faithful port of <c>UpdateManeuverPlacementForInternalIntersectionTurns()</c>.</summary>
    protected void UpdateManeuverPlacementForInternalIntersectionTurns(LinkedList<Maneuver> maneuvers)
    {
        static bool IsTurnManeuver(DirectionsLegManeuverType maneuverType)
        {
            switch (maneuverType)
            {
                case DirectionsLegManeuverType.SlightRight:
                case DirectionsLegManeuverType.Right:
                case DirectionsLegManeuverType.SharpRight:
                case DirectionsLegManeuverType.UturnRight:
                case DirectionsLegManeuverType.UturnLeft:
                case DirectionsLegManeuverType.SharpLeft:
                case DirectionsLegManeuverType.Left:
                case DirectionsLegManeuverType.SlightLeft:
                case DirectionsLegManeuverType.RampRight:
                case DirectionsLegManeuverType.RampLeft:
                case DirectionsLegManeuverType.StayRight:
                case DirectionsLegManeuverType.StayLeft:
                    return true;
                default:
                    return false;
            }
        }

        static bool IsRelativeStraight(uint turnDegree)
            => (turnDegree >= RelativeStraightTurnDegreeLowerBound) || (turnDegree <= RelativeStraightTurnDegreeUpperBound);

        Maneuver? prevManeuver = null;
        foreach (Maneuver maneuver in maneuvers)
        {
            if (prevManeuver != null)
            {
                // Skip destination maneuver
                if (maneuver.IsDestinationType())
                {
                    break;
                }

                if (IsTurnManeuver(maneuver.Type()))
                {
                    uint originalManeuverEndNodeIndex = maneuver.EndNodeIndex();

                    for (uint nodeIndex = maneuver.BeginNodeIndex(); nodeIndex < originalManeuverEndNodeIndex; ++nodeIndex)
                    {
                        uint newNodeIndex = nodeIndex + 1;
                        EnhancedTripLeg_Edge prevEdge = TripPath.GetPrevEdge((int)nodeIndex)!;
                        EnhancedTripLeg_Edge edge = TripPath.GetCurrEdge((int)nodeIndex)!;

                        if ((newNodeIndex < originalManeuverEndNodeIndex)
                            && (prevManeuver.GetTravelMode() == maneuver.GetTravelMode())
                            && edge.InternalIntersection()
                            && IsRelativeStraight(Util.GetTurnDegree(prevEdge.EndHeading(), edge.BeginHeading())))
                        {
                            MoveInternalEdgeToPreviousManeuver(prevManeuver, maneuver, newNodeIndex, prevEdge, edge);
                        }
                        else
                        {
                            break;
                        }
                    }
                }
            }

            prevManeuver = maneuver;
        }
    }

    /// <summary>Moves a straight internal edge to the previous maneuver. Faithful port of <c>MoveInternalEdgeToPreviousManeuver()</c>.</summary>
    protected void MoveInternalEdgeToPreviousManeuver(
        Maneuver prevManeuver,
        Maneuver maneuver,
        uint newNodeIndex,
        EnhancedTripLeg_Edge prevEdge,
        EnhancedTripLeg_Edge edge)
    {
        // Update the previous maneuver
        prevManeuver.SetLength(prevManeuver.Length() + edge.LengthKm());
        prevManeuver.SetBasicTime(prevManeuver.BasicTime()
            + Util.GetTime(edge.LengthKm(), GetSpeed(prevManeuver.GetTravelMode(), edge.DefaultSpeed())));
        prevManeuver.SetEndNodeIndex(newNodeIndex);
        prevManeuver.SetEndShapeIndex(edge.EndShapeIndex());
        prevManeuver.SetTime(TripPath.Node((int)prevManeuver.EndNodeIndex()).ElapsedCost.Secs
            - TripPath.Node((int)prevManeuver.BeginNodeIndex()).ElapsedCost.Secs);

        // Update the maneuver
        maneuver.SetLength(maneuver.Length() - edge.LengthKm());
        maneuver.SetBasicTime(maneuver.BasicTime()
            - Util.GetTime(edge.LengthKm(), GetSpeed(maneuver.GetTravelMode(), edge.DefaultSpeed())));
        maneuver.SetBeginNodeIndex(newNodeIndex);
        maneuver.SetBeginShapeIndex(edge.EndShapeIndex());
        maneuver.SetTime(TripPath.Node((int)maneuver.EndNodeIndex()).ElapsedCost.Secs
            - TripPath.Node((int)maneuver.BeginNodeIndex()).ElapsedCost.Secs);

        // If the internal edge does not have turn lanes then copy the turn lanes from the previous edge
        if (edge.TurnLanesSize() == 0)
        {
            // PORT-NOTE: turn-lane masks live on the underlying Thor TripEdge; copying masks would
            // require write access not exposed by EnhancedTripLeg_Edge. The turn-lane copy only
            // affects ProcessTurnLanes activation, not maneuver structure, so it is a no-op here.
        }
    }

    /// <summary>Collapses a small end ramp fork maneuver. Faithful port of <c>CollapseSmallEndRampFork()</c>.</summary>
    protected void CollapseSmallEndRampFork(LinkedList<Maneuver> maneuvers)
    {
        LinkedListNode<Maneuver>? prevMan = maneuvers.First;
        LinkedListNode<Maneuver>? currMan = maneuvers.First;
        LinkedListNode<Maneuver>? nextMan = maneuvers.First;
        if (nextMan != null)
        {
            nextMan = nextMan.Next;
            currMan = nextMan;
        }

        if (nextMan != null)
        {
            nextMan = nextMan.Next;
        }

        static bool IsForkThenTurnSameDirection(DirectionsLegManeuverType currManeuverType, DirectionsLegManeuverType nextManeuverType)
        {
            if ((currManeuverType == DirectionsLegManeuverType.StayRight)
                && ((nextManeuverType == DirectionsLegManeuverType.SlightRight)
                    || (nextManeuverType == DirectionsLegManeuverType.Right)
                    || (nextManeuverType == DirectionsLegManeuverType.SharpRight)))
            {
                return true;
            }
            else if ((currManeuverType == DirectionsLegManeuverType.StayLeft)
                     && ((nextManeuverType == DirectionsLegManeuverType.SlightLeft)
                         || (nextManeuverType == DirectionsLegManeuverType.Left)
                         || (nextManeuverType == DirectionsLegManeuverType.SharpLeft)))
            {
                return true;
            }

            return false;
        }

        while (nextMan != null)
        {
            if ((prevMan != currMan) && !prevMan!.Value.HasCollapsedSmallEndRampFork()
                && prevMan.Value.Ramp() && currMan!.Value.Ramp() && !nextMan.Value.Ramp()
                && (currMan.Value.Length() <= SmallEndRampForkThreshold)
                && IsForkThenTurnSameDirection(currMan.Value.Type(), nextMan.Value.Type()))
            {
                currMan = CombineManeuvers(maneuvers, prevMan, currMan);
                prevMan.Value.SetHasCollapsedSmallEndRampFork(true);
                nextMan = nextMan.Next;
            }
            else
            {
                prevMan = currMan;
                currMan = nextMan;
                nextMan = nextMan.Next;
            }
        }
    }

    /// <summary>Collapses merge maneuvers into the previous ramp. Faithful port of <c>CollapseMergeManeuvers()</c>.</summary>
    protected void CollapseMergeManeuvers(LinkedList<Maneuver> maneuvers)
    {
        LinkedListNode<Maneuver>? currMan = maneuvers.First;
        LinkedListNode<Maneuver>? nextMan = maneuvers.First;
        if (nextMan != null)
        {
            nextMan = nextMan.Next;
        }

        while (nextMan != null)
        {
            Maneuver curr = currMan!.Value;
            Maneuver next = nextMan.Value;

            if (curr.Ramp() && next.IsMergeType() && !curr.HasCollapsedMergeManeuver())
            {
                // Disable the "to stay on" flag if not the same street names
                if (curr.ToStayOn() && !next.HasSameNames(curr, true))
                {
                    curr.SetToStayOn(false);
                }

                // Use the merge maneuver street names
                if (next.HasStreetNames())
                {
                    curr.SetStreetNames(next.StreetNames().Clone());
                }

                // Use merge maneuver guide signs
                if (!curr.HasSigns())
                {
                    if (next.HasGuideBranchSign())
                    {
                        curr.MutableSigns().MutableGuideBranchList().Clear();
                        foreach (OdinSign sign in next.GetSigns().GuideBranchList())
                        {
                            var copy = new OdinSign(sign.Text(), sign.IsRouteNumber(), sign.GetPronunciation());
                            copy.SetConsecutiveCount(sign.ConsecutiveCount());
                            curr.MutableSigns().MutableGuideBranchList().Add(copy);
                        }
                    }

                    if (next.HasGuideTowardSign())
                    {
                        curr.MutableSigns().MutableGuideTowardList().Clear();
                        foreach (OdinSign sign in next.GetSigns().GuideTowardList())
                        {
                            var copy = new OdinSign(sign.Text(), sign.IsRouteNumber(), sign.GetPronunciation());
                            copy.SetConsecutiveCount(sign.ConsecutiveCount());
                            curr.MutableSigns().MutableGuideTowardList().Add(copy);
                        }
                    }
                }

                nextMan = CombineManeuvers(maneuvers, currMan, nextMan);
                curr.SetHasCollapsedMergeManeuver(true);
            }

            currMan = nextMan;
            nextMan = nextMan.Next;
        }
    }

    // PORT-NOTE: the ported TripEdge does not carry a per-edge "levels" list; level changes are not
    // represented in the structural foundation, so has_level_changes(prev_edge->levels()) is false.
    private static bool HasLevelChanges(EnhancedTripLeg_Edge edge) => false;

    // PORT-NOTE: the ported TripEdge does not carry a per-edge pedestrian type, so the C++
    // blind-pedestrian branches (curr_edge->pedestrian_type() == kBlind) never trigger.
    private static bool EdgeIsBlind() => false;

    // Helper: erase a node from the list, returning the node that followed it (mirrors std::list::erase,
    // whose return is the iterator following the removed element - end() when it was the last).
    private static LinkedListNode<Maneuver>? EraseNode(LinkedList<Maneuver> maneuvers, LinkedListNode<Maneuver> node)
    {
        LinkedListNode<Maneuver>? next = node.Next;
        maneuvers.Remove(node);
        return next;
    }

    // The Combine* helpers always erase an element that has a successor (next_man / the element after
    // curr_man), so the returned node is non-null; the bang documents that invariant at the call site.
    private static LinkedListNode<Maneuver> Erase(LinkedList<Maneuver> maneuvers, LinkedListNode<Maneuver> node)
        => EraseNode(maneuvers, node)!;
}
