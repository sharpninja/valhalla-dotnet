// Faithful C# port of Valhalla odin DirectionsBuilder
// (valhalla/odin/directionsbuilder.h + src/odin/directionsbuilder.cc) @ 3.7.0.
// Source: valhalla/odin/directionsbuilder.h, src/odin/directionsbuilder.cc
//
// Top-level entry: turns a Thor TripLeg into a DirectionsLeg carrying ordered Maneuvers. Build wraps
// the leg in an EnhancedTripLeg, runs UpdateHeading (fix ~0-length edge headings), runs
// ManeuversBuilder::Build to produce maneuvers, then PopulateDirectionsLeg to transfer the maneuver
// structure and leg-level metadata into the result.
//
// PORT-NOTE: The C++ Build(Api&, MarkupFormatter) walks api.trip().routes().legs() and writes into
// api.directions(). There is no proto Api in this port, so the entry point takes a single TripLeg
// and returns a single DirectionsLeg (the per-leg core). The narrative (instructions) pass is
// DEFERRED - DirectionsType::instructions produces the same maneuver structure as
// DirectionsType::maneuvers here (only the localized prose, which is not ported, differs).
//
// PORT-NOTE (DEFER): PopulateDirectionsLeg's per-field copy into the proto DirectionsLeg.Maneuver is
// not needed - the odin Maneuver working objects already carry the structural fields the proto
// maneuver would receive, and DirectionsLeg.Maneuvers holds those same objects. The leg-level
// summary (length / time / bbox / has_time_restrictions) and the locations / level_changes / transit
// objects belong to the EXCLUDED request/serialization + transit layers; only the structural
// leg metadata (trip/leg ids, shape, toll/ferry/highway flags) is populated.

using System.Collections.Generic;

using SharpNinja.Valhalla.Sif;
using SharpNinja.Valhalla.Thor;

namespace SharpNinja.Valhalla.Odin;

/// <summary>
/// Builds the trip directions (a <see cref="DirectionsLeg"/>) from a trip path. Faithful port of
/// <c>valhalla::odin::DirectionsBuilder</c>.
/// </summary>
public static class DirectionsBuilder
{
    // Minimum edge length to verify heading (~3 feet). Faithful port of kMinEdgeLength.
    private const float MinEdgeLength = 0.001f;

    /// <summary>
    /// Returns the trip directions for the specified options and trip path. This calls
    /// <see cref="ManeuversBuilder.Build"/> to form the maneuver list and
    /// <see cref="PopulateDirectionsLeg"/> to transform it into the directions leg. Faithful port of
    /// <c>DirectionsBuilder::Build()</c> (reduced to a single leg - see file header).
    /// </summary>
    /// <param name="options">The directions options (units, directions type, roundabout exits).</param>
    /// <param name="tripPath">The trip path produced by thor's TripLegBuilder.</param>
    /// <returns>The directions leg with ordered maneuvers.</returns>
    public static DirectionsLeg Build(Options options, TripLeg tripPath)
    {
        // Validate trip path node list
        if (tripPath.Nodes.Count < 1)
        {
            throw new ValhallaException(210);
        }

        // Create an enhanced trip path from the specified trip_path
        var etp = new EnhancedTripLeg(tripPath);

        // Produce maneuvers if desired
        var maneuvers = new LinkedList<Maneuver>();
        if (options.DirectionsType != DirectionsType.None)
        {
            // Update the heading of ~0 length edges
            UpdateHeading(etp);

            var maneuversBuilder = new ManeuversBuilder(options, etp);
            maneuvers = maneuversBuilder.Build();

            // PORT-NOTE (DEFER): DirectionsType.Instructions would additionally run the
            // NarrativeBuilder to produce localized prose; that prose family is not ported.
        }

        // Return trip directions
        var tripDirections = new DirectionsLeg();
        PopulateDirectionsLeg(options, etp, maneuvers, tripDirections);
        return tripDirections;
    }

    /// <summary>Updates the heading of ~0 length edges. Faithful port of <c>UpdateHeading()</c>.</summary>
    /// <param name="etp">The enhanced trip path containing the edges to process.</param>
    public static void UpdateHeading(EnhancedTripLeg etp)
    {
        for (int x = 0; x < etp.NodeSize(); ++x)
        {
            EnhancedTripLeg_Edge? prevEdge = etp.GetPrevEdge(x);
            EnhancedTripLeg_Edge? currEdge = etp.GetCurrEdge(x);
            EnhancedTripLeg_Edge? nextEdge = etp.GetNextEdge(x);

            // If very short edge and no headings
            if (currEdge != null && (currEdge.LengthKm() <= MinEdgeLength)
                && (currEdge.BeginHeading() == 0) && (currEdge.EndHeading() == 0))
            {
                // Use next edge to set the current begin/end heading
                if (nextEdge != null && (nextEdge.LengthKm() > MinEdgeLength))
                {
                    currEdge.SetBeginHeading(nextEdge.BeginHeading());
                    currEdge.SetEndHeading(nextEdge.BeginHeading());
                }
                // Use prev edge to set the current begin/end heading
                else if (prevEdge != null && (prevEdge.LengthKm() > MinEdgeLength))
                {
                    currEdge.SetBeginHeading(prevEdge.EndHeading());
                    currEdge.SetEndHeading(prevEdge.EndHeading());
                }
            }
        }
    }

    /// <summary>
    /// Transfers the maneuver list and leg-level metadata into the directions leg. Faithful port of
    /// <c>PopulateDirectionsLeg()</c> (structural subset - see file header).
    /// </summary>
    /// <param name="options">The directions options.</param>
    /// <param name="etp">The enhanced trip path (shape, summary).</param>
    /// <param name="maneuvers">The maneuver list produced by the maneuver builder.</param>
    /// <param name="tripDirections">The directions leg to populate.</param>
    public static void PopulateDirectionsLeg(
        Options options,
        EnhancedTripLeg etp,
        LinkedList<Maneuver> maneuvers,
        DirectionsLeg tripDirections)
    {
        // PORT-NOTE: the C++ copies each odin Maneuver into a proto DirectionsLeg.Maneuver field by
        // field. Here the odin Maneuver working objects already carry the structural fields, so the
        // ordered maneuvers are transferred directly into the result list.
        foreach (Maneuver maneuver in maneuvers)
        {
            tripDirections.Maneuvers.Add(maneuver);
        }

        // Populate trip and leg IDs.
        // PORT-NOTE: the ported TripLeg carries no trip_id / leg_id / leg_count (request-layer); leave
        // the defaults (0).

        // Populate shape
        tripDirections.Shape = etp.Shape();

        // Populate toll, highway, ferry tags
        TripSummary summary = etp.Summary();
        tripDirections.HasToll = summary.HasToll;
        tripDirections.HasHighway = summary.HasHighway;
        tripDirections.HasFerry = summary.HasFerry;
    }
}
