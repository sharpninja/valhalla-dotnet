// Faithful C# port of Valhalla mjolnir directededgebuilder.h + src/mjolnir/directededgebuilder.cc
// @ 3.7.0.
// Sources:
//   F:/github/valhalla/valhalla/mjolnir/directededgebuilder.h
//   F:/github/valhalla/src/mjolnir/directededgebuilder.cc
//
// Builds a baldr DirectedEdge (the on-disk tile record) given an OSM way and other properties.
// The C++ class derives from baldr::DirectedEdge; here we return a configured baldr.DirectedEdge
// value (the ported C# DirectedEdge is a struct).

using System;

using SharpNinja.Valhalla.Baldr;

namespace SharpNinja.Valhalla.Mjolnir;

/// <summary>
/// Builds a <see cref="DirectedEdge"/> given an OSM way and other properties. Faithful port of the
/// C++ <c>class DirectedEdgeBuilder</c>.
/// </summary>
public static class DirectedEdgeBuilder
{
    /// <summary>Minimum edge length (meters). Mirrors C++ <c>kMinimumEdgeLength</c>.</summary>
    public const uint MinimumEdgeLength = 1;

    /// <summary>
    /// Construct a directed edge with arguments. Faithful port of the C++
    /// <c>DirectedEdgeBuilder::DirectedEdgeBuilder(...)</c> constructor.
    /// </summary>
    /// <param name="way">OSM way info generated from parsing OSM tags with Lua.</param>
    /// <param name="endnode">GraphId of the end node of this directed edge.</param>
    /// <param name="forward">Whether this directed edge is forward along the edge info.</param>
    /// <param name="length">Length in meters.</param>
    /// <param name="speed">Average speed in kph.</param>
    /// <param name="truckSpeed">Truck speed limit in kph.</param>
    /// <param name="use">Use of the edge.</param>
    /// <param name="rc">Road class / importance.</param>
    /// <param name="localidx">Index of the edge (from the node) on the local level.</param>
    /// <param name="signal">Traffic signal.</param>
    /// <param name="stopSign">Stop sign.</param>
    /// <param name="yieldSign">Yield sign.</param>
    /// <param name="minor">Does the stop or yield only apply to minor roads?</param>
    /// <param name="restrictions">Mask of simple turn restrictions at the end node of this edge.</param>
    /// <param name="bikeNetwork">Mask of bike_networks from relations.</param>
    /// <param name="reclassFerry">Whether this edge was in a ferry path.</param>
    /// <param name="rcHierarchy">The road class for hierarchies.</param>
    /// <returns>The constructed directed edge.</returns>
    public static DirectedEdge Build(
        OSMWay way,
        GraphId endnode,
        bool forward,
        uint length,
        uint speed,
        uint truckSpeed,
        Use use,
        RoadClass rc,
        uint localidx,
        bool signal,
        bool stopSign,
        bool yieldSign,
        bool minor,
        uint restrictions,
        uint bikeNetwork,
        bool reclassFerry,
        RoadClass rcHierarchy)
    {
        ArgumentNullException.ThrowIfNull(way);

        DirectedEdge de = DirectedEdge.Create();

        de.SetEndNode(endnode);
        de.SetUse(use);
        de.SetSpeed(speed);            // KPH
        de.SetTruckSpeed(truckSpeed);  // KPH

        // Protect against 0 length edges.
        de.SetLength(Math.Max(length, MinimumEdgeLength), true);

        // Override use for ferries/rail ferries. TODO - set this in lua.
        if (way.Ferry() && way.UseValue() != Use.Construction)
        {
            de.SetUse(Use.Ferry);
        }

        if (way.Rail() && way.UseValue() != Use.Construction)
        {
            de.SetUse(Use.RailFerry);
        }

        de.SetToll(way.Toll());

        // Set flag indicating this edge has a bike network.
        if (bikeNetwork != 0)
        {
            de.SetBikeNetwork(true);
        }

        de.SetTruckRoute(way.TruckRoute());

        if (rcHierarchy < RoadClass.Invalid)
        {
            // Hijack shortcut flag to indicate whether this needs to be moved in hierarchy builder;
            // will be reset there.
            de.SetHierarchyRoadClass(rcHierarchy);
        }

        // Ferries should never be set to destination only. For other paths, set destination only to
        // true if we didn't reclassify for ferry and either destination only or no thru is set.
        if (way.Ferry())
        {
            de.SetDestOnly(false);
        }
        else
        {
            de.SetDestOnly(!reclassFerry && (way.DestinationOnly() || way.NoThruTraffic()));
        }

        de.SetDestOnlyHgv(way.DestinationOnlyHgv());
        de.SetDismount(way.Dismount());
        de.SetUseSidepath(way.UseSidepath());
        de.SetSacScale(way.SacScaleValue());
        de.SetSurface(way.SurfaceValue());
        de.SetTunnel(way.Tunnel());
        de.SetRoundabout(way.Roundabout());
        de.SetBridge(way.Bridge());
        de.SetIndoor(way.Indoor());
        de.SetLink(way.Link());
        de.SetHovType(way.HovType());
        de.SetClassification(rc);
        de.SetLocalEdgeIdx(localidx);
        de.SetRestrictions(restrictions);
        de.SetTrafficSignal(signal);

        de.SetStopSign(stopSign);
        de.SetYieldSign(yieldSign);

        // Temporarily set the deadend flag to indicate if the stop or yield should be at the minor
        // roads.
        de.SetDeadend(minor);

        de.SetSidewalkLeft(way.SidewalkLeft());
        de.SetSidewalkRight(way.SidewalkRight());

        bool taggedSpeed =
            way.TaggedSpeed() || way.ForwardTaggedSpeed() || way.BackwardTaggedSpeed();
        de.SetSpeedType(taggedSpeed ? SpeedType.Tagged : SpeedType.Classified);

        de.SetLit(way.Lit());

        // Set forward flag and access modes (based on direction).
        de.SetForward(forward);
        uint forwardAccess = 0;
        uint reverseAccess = 0;

        if ((way.AutoForward() && forward) || (way.AutoBackward() && !forward))
        {
            forwardAccess |= GraphConstants.AutoAccess;
        }

        if ((way.AutoForward() && !forward) || (way.AutoBackward() && forward))
        {
            reverseAccess |= GraphConstants.AutoAccess;
        }

        if ((way.TruckForward() && forward) || (way.TruckBackward() && !forward))
        {
            forwardAccess |= GraphConstants.TruckAccess;
        }

        if ((way.TruckForward() && !forward) || (way.TruckBackward() && forward))
        {
            reverseAccess |= GraphConstants.TruckAccess;
        }

        if ((way.BusForward() && forward) || (way.BusBackward() && !forward))
        {
            forwardAccess |= GraphConstants.BusAccess;
        }

        if ((way.BusForward() && !forward) || (way.BusBackward() && forward))
        {
            reverseAccess |= GraphConstants.BusAccess;
        }

        if ((way.BikeForward() && forward) || (way.BikeBackward() && !forward))
        {
            forwardAccess |= GraphConstants.BicycleAccess;
        }

        if ((way.BikeForward() && !forward) || (way.BikeBackward() && forward))
        {
            reverseAccess |= GraphConstants.BicycleAccess;
        }

        if ((way.MopedForward() && forward) || (way.MopedBackward() && !forward))
        {
            forwardAccess |= GraphConstants.MopedAccess;
        }

        if ((way.MopedForward() && !forward) || (way.MopedBackward() && forward))
        {
            reverseAccess |= GraphConstants.MopedAccess;
        }

        if ((way.MotorcycleForward() && forward) || (way.MotorcycleBackward() && !forward))
        {
            forwardAccess |= GraphConstants.MotorcycleAccess;
        }

        if ((way.MotorcycleForward() && !forward) || (way.MotorcycleBackward() && forward))
        {
            reverseAccess |= GraphConstants.MotorcycleAccess;
        }

        if ((way.EmergencyForward() && forward) || (way.EmergencyBackward() && !forward))
        {
            forwardAccess |= GraphConstants.EmergencyAccess;
        }

        if ((way.EmergencyForward() && !forward) || (way.EmergencyBackward() && forward))
        {
            reverseAccess |= GraphConstants.EmergencyAccess;
        }

        if ((way.HovForward() && forward) || (way.HovBackward() && !forward))
        {
            forwardAccess |= GraphConstants.HovAccess;
        }

        if ((way.HovForward() && !forward) || (way.HovBackward() && forward))
        {
            reverseAccess |= GraphConstants.HovAccess;
        }

        if ((way.TaxiForward() && forward) || (way.TaxiBackward() && !forward))
        {
            forwardAccess |= GraphConstants.TaxiAccess;
        }

        if ((way.TaxiForward() && !forward) || (way.TaxiBackward() && forward))
        {
            reverseAccess |= GraphConstants.TaxiAccess;
        }

        if ((way.PedestrianForward() && forward) || (way.PedestrianBackward() && !forward))
        {
            forwardAccess |= GraphConstants.PedestrianAccess;
        }

        if ((way.PedestrianForward() && !forward) || (way.PedestrianBackward() && forward))
        {
            reverseAccess |= GraphConstants.PedestrianAccess;
        }

        if (way.UseValue() != Use.Steps && way.UseValue() != Use.Construction &&
            way.SurfaceValue() != Surface.Impassable)
        {
            if (way.WheelchairTag() && way.Wheelchair())
            {
                forwardAccess |= GraphConstants.WheelchairAccess;
                reverseAccess |= GraphConstants.WheelchairAccess;
            }
            else if (!way.WheelchairTag())
            {
                if ((way.PedestrianForward() && forward) || (way.PedestrianBackward() && !forward))
                {
                    forwardAccess |= GraphConstants.WheelchairAccess;
                }

                if ((way.PedestrianForward() && !forward) || (way.PedestrianBackward() && forward))
                {
                    reverseAccess |= GraphConstants.WheelchairAccess;
                }
            }
        }

        // Set access modes.
        de.SetForwardAccess(forwardAccess);
        de.SetReverseAccess(reverseAccess);

        return de;
    }
}
