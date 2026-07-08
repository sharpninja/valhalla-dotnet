// Faithful C# port of Valhalla thor TripLegBuilder (valhalla @ 3.7.0).
// Source: F:/github/valhalla/src/thor/triplegbuilder.cc (+ valhalla/thor/triplegbuilder.h)
//
// Turns a found path (the ordered std::vector<PathInfo> produced by a PathAlgorithm) into a
// TripLeg result: the ordered edges, the decoded + trimmed shape (via Midgard), the per-edge and
// per-node attributes, the admin table, the bounding box, and the leg summary. In the engine this
// builds the protobuf TripLeg which odin later turns into maneuvers; here the result is the
// de-protobuf'd <see cref="TripLeg"/> (see TripLeg.cs).
//
// PORT SCOPE (point-to-point auto/truck only). The following C++ surfaces are intentionally omitted
// because they belong to EXCLUDED modules or to attributes a default route request does not need:
//   - AttributesController gating: there is no proto/api request surface, so the builder behaves as
//     a default route (maneuver-generation) controller and always populates the kept subset.
//   - Incidents (SetShapeAttributes incident cutting, UpdateIncident), closures, traffic cutting,
//     per-shape-point shape_attributes (time/length/speed/congestion) -- live-traffic/incident slice.
//   - Elevation sampling (SetElevation), landmarks (AddLandmarks), conditional speed limits, lane
//     connectivity, faded/non-faded speeds, transit route info, recosting
//     (AccumulateRecostingInfoForward), guidance views, pronunciation/linguistics.
//   - MultimodalBuilder (transit) -- multimodal is EXCLUDED.
// What is faithfully ported: CopyLocations (origin/dest snap percent + side-of-street), the main
// per-edge loop assembling ordered edges, the shape decode/reverse/trim logic (trim_shape), begin/
// end headings (GetOffsetForHeading + HeadingAlong/AtEndOfPolyline), names + tunnel/bridge names,
// signs (edge + named-junction), turn lanes, the full per-edge flag/classification/use/speed set,
// per-node type/traffic-signal/fork/elapsed-and-transition-cost/admin-index/time-zone, intersecting
// edges (AddIntersectingEdges incl. NodeTransitions, shortcut/opp/path skipping), the dedup'd admin
// table (GetAdminIndex/AssignAdmins), the bounding box (SetBoundingBox), the encoded shape, and the
// toll/ferry/highway summary.

using System;
using System.Collections.Generic;

using SharpNinja.Valhalla.Baldr;
using SharpNinja.Valhalla.Midgard;
using SharpNinja.Valhalla.Sif;

// Aliases to read like the C++ signatures.
using GraphTilePtr = SharpNinja.Valhalla.Baldr.GraphTile;
using DirectedEdgeRec = SharpNinja.Valhalla.Baldr.DirectedEdge;

namespace SharpNinja.Valhalla.Thor;

/// <summary>
/// Builds a <see cref="TripLeg"/> from a found path (a sequence of <see cref="PathInfo"/>). Faithful
/// port of <c>valhalla::thor::TripLegBuilder</c> (see file header for the omitted surfaces).
/// </summary>
public static class TripLegBuilder
{
    // arbitrary time of week constants from the C++ anonymous namespace (only used by the excluded
    // faded-speeds path; kept here as documentation of parity).
    private const byte NotTagged = 0;

    /// <summary>
    /// Forms a trip leg out of a path. Faithful port of <c>TripLegBuilder::Build</c> (default-route
    /// attribute set; see file header for omitted surfaces).
    /// </summary>
    /// <param name="graphreader">A way of accessing graph information.</param>
    /// <param name="modeCosting">The costing objects (indexed by travel mode).</param>
    /// <param name="path">The found path (ordered list of <see cref="PathInfo"/>).</param>
    /// <param name="origin">The origin location with its correlated path edges filled in from loki.</param>
    /// <param name="dest">The destination location with its correlated path edges filled in from loki.</param>
    /// <param name="algorithms">The graph search algorithm names used to create the path.</param>
    /// <param name="interruptCallback">A way to abort processing if the request was cancelled.</param>
    /// <returns>The assembled <see cref="TripLeg"/>.</returns>
    public static TripLeg Build(
        GraphReader graphreader,
        ModeCosting modeCosting,
        IReadOnlyList<PathInfo> path,
        PathLocation origin,
        PathLocation dest,
        IReadOnlyList<string>? algorithms = null,
        Action? interruptCallback = null)
    {
        if (graphreader is null)
        {
            throw new ArgumentNullException(nameof(graphreader));
        }

        if (modeCosting is null)
        {
            throw new ArgumentNullException(nameof(modeCosting));
        }

        if (path is null)
        {
            throw new ArgumentNullException(nameof(path));
        }

        if (origin is null)
        {
            throw new ArgumentNullException(nameof(origin));
        }

        if (dest is null)
        {
            throw new ArgumentNullException(nameof(dest));
        }

        // Test interrupt prior to building trip path
        interruptCallback?.Invoke();

        if (path.Count == 0)
        {
            throw new ArgumentException("TripLegBuilder.Build requires a non-empty path", nameof(path));
        }

        var tripPath = new TripLeg();

        // Remember what algorithms were used to create this leg
        if (algorithms != null)
        {
            tripPath.Algorithms.AddRange(algorithms);
        }

        // Create an array of travel types per mode
        var travelTypes = new byte[(int)TravelMode.MaxTravelMode];
        for (int i = 0; i < travelTypes.Length; i++)
        {
            travelTypes[i] = modeCosting[i] != null ? modeCosting[i]!.TravelType() : (byte)0;
        }

        // Start node is the end node of the opposing edge to the first edge in the path.
        GraphTilePtr? beginTile = null;
        DirectedEdgeRec? oppEdge = graphreader.GetOpposingEdge(path[0].Edgeid, ref beginTile);
        if (oppEdge is null)
        {
            throw new InvalidOperationException("TripLegBuilder.Build failed: opposing edge of first path edge not found");
        }

        GraphId startnode = oppEdge.Value.EndNode;

        // Partial edge at the start and side of street (sos)
        float startPct = 0f;
        Location.SideOfStreetType startSos = Location.SideOfStreetType.None;
        PointLL startVrt = new PointLL();
        foreach (PathLocation.PathEdge e in origin.Edges)
        {
            if (e.Id == path[0].Edgeid)
            {
                startPct = (float)e.PercentAlong;
                startSos = e.Sos;
                startVrt = e.Projected;
                break;
            }
        }

        // Partial edge at the end
        float endPct = 1f;
        Location.SideOfStreetType endSos = Location.SideOfStreetType.None;
        PointLL endVrt = new PointLL();
        GraphId lastEdgeId = path[path.Count - 1].Edgeid;
        foreach (PathLocation.PathEdge e in dest.Edges)
        {
            if (e.Id == lastEdgeId)
            {
                endPct = (float)e.PercentAlong;
                endSos = e.Sos;
                endVrt = e.Projected;
                break;
            }
        }

        _ = startSos;
        _ = endSos;

        // Structures to process admins (dedup map + ordered list, exactly like GetAdminIndex).
        var adminInfoMap = new Dictionary<AdminInfo, uint>();
        var adminInfoList = new List<AdminInfo>();

        // Iterate through path
        uint priorOppLocalIndex = uint.MaxValue;
        var tripShape = new List<PointLL>();
        ulong osmchangeset = 0;
        int edgeIndex = 0;
        DirectedEdgeRec? prevDe = null;
        GraphTilePtr? graphtile = null;

        bool hasToll = false;
        bool hasFerry = false;
        bool hasHighway = false;

        // loop over the edges to build the trip leg
        for (int pi = 0; pi < path.Count; pi++, edgeIndex++)
        {
            PathInfo edgeItr = path[pi];
            GraphId edge = edgeItr.Edgeid;
            graphtile = graphreader.GetGraphTile(edge, ref graphtile);
            if (graphtile is null)
            {
                throw new InvalidOperationException("TripLegBuilder.Build failed: tile gone for path edge");
            }

            DirectedEdgeRec directededge = graphtile.DirectedEdge(edge);
            TravelMode mode = edgeItr.Mode;
            byte travelType = travelTypes[(int)mode];
            DynamicCost? costing = modeCosting[(int)mode];

            bool isFirstEdge = pi == 0;
            bool isLastEdge = pi == path.Count - 1;

            // By default the `startnode` is the endnode of the previous directed edge. For a
            // disconnected edge (trace-matching discontinuity) recompute it from the opposing edge.
            if (edgeItr.IsDisconnected)
            {
                GraphTilePtr? oppTile = graphtile;
                DirectedEdgeRec? opp = graphreader.GetOpposingEdge(directededge, ref oppTile);
                if (opp.HasValue)
                {
                    startnode = opp.Value.EndNode;
                }
            }

            if (directededge.Toll)
            {
                hasToll = true;
            }

            if (directededge.Use == Use.Ferry)
            {
                hasFerry = true;
            }

            if (directededge.Classification == RoadClass.Motorway)
            {
                hasHighway = true;
            }

            // Set node attributes - only set if they are true since they are optional
            GraphTilePtr? startTile = graphtile;
            graphreader.GetGraphTile(startnode, ref startTile);
            if (startTile is null)
            {
                throw new InvalidOperationException("TripLegBuilder.Build failed: start tile gone");
            }

            NodeInfo node = startTile.Node(startnode);

            if (osmchangeset == 0)
            {
                osmchangeset = startTile.Header().DatasetId();
            }

            // Add a node to the trip path and set its attributes.
            var tripNode = new TripNode
            {
                Type = node.Type,
                TrafficSignal = node.TrafficSignal,
                Fork = node.Intersection == IntersectionType.Fork,
            };

            // Assign the elapsed time from the start of the leg
            if (pi == 0)
            {
                tripNode.ElapsedCost = new Cost(0f, 0f);
            }
            else
            {
                tripNode.ElapsedCost = path[pi - 1].ElapsedCost;
            }

            // Assign the admin index
            tripNode.AdminIndex = GetAdminIndex(startTile.AdminInfo((int)node.AdminIndex), adminInfoMap, adminInfoList);

            // Transition cost entering this edge
            tripNode.TransitionCost = edgeItr.TransitionCost;

            tripPath.Nodes.Add(tripNode);

            bool includeFirstPoint = isFirstEdge || edgeItr.IsDisconnected;
            uint beginIndex = includeFirstPoint ? (uint)tripShape.Count : (uint)(tripShape.Count - 1);

            EdgeInfo edgeinfo = graphtile.EdgeInfo(directededge);

            // Add edge to the trip node and set its attributes
            TripEdge tripEdge = AddTripEdge(
                edge,
                edgeItr,
                mode,
                travelType,
                costing,
                directededge,
                node.DriveOnRight,
                tripNode,
                graphtile,
                startnode.Id(),
                node.NamedIntersection,
                startTile,
                edgeinfo);

            // some information regarding shape/length trimming
            float trimStartPct = isFirstEdge ? startPct : 0f;
            float trimEndPct = isLastEdge ? endPct : 1f;

            // Add the shape, clipping the first/last edge as needed.
            if (isFirstEdge || isLastEdge)
            {
                // Get edge shape and reverse it if directed edge is not forward.
                var edgeShape = new List<PointLL>(edgeinfo.Shape());
                if (!directededge.Forward)
                {
                    edgeShape.Reverse();
                }

                float total = directededge.Length;

                // Trim both ways
                if (isFirstEdge && isLastEdge)
                {
                    Util.TrimShape(startPct * total, startVrt, endPct * total, endVrt, edgeShape);
                }
                else if (isFirstEdge)
                {
                    // Trim the shape at the front for the first edge
                    Util.TrimShape(startPct * total, startVrt, total, edgeShape[edgeShape.Count - 1], edgeShape);
                }
                else
                {
                    // And at the back if it's the last edge
                    Util.TrimShape(0f, edgeShape[0], endPct * total, endVrt, edgeShape);
                }

                // Keep the shape (skip first point when redundant with the previous edge)
                AppendShape(tripShape, edgeShape, includeFirstPoint);
            }
            else
            {
                // Just get the shape in there in the right direction, no clipping needed.
                IReadOnlyList<PointLL> shape = edgeinfo.Shape();
                if (directededge.Forward)
                {
                    AppendShapeForward(tripShape, shape, includeFirstPoint);
                }
                else
                {
                    AppendShapeReverse(tripShape, shape, includeFirstPoint);
                }
            }

            // Set the portion of the edge we used
            tripEdge.SourceAlongEdge = trimStartPct;
            tripEdge.TargetAlongEdge = trimEndPct;

            // Set length (km) of the used portion of the edge.
            float km = Math.Max(directededge.Length * Constants.KmPerMeter * (trimEndPct - trimStartPct), 0f);
            tripEdge.LengthKm = km;

            // Set begin/end shape index.
            tripEdge.BeginShapeIndex = beginIndex;
            tripEdge.EndShapeIndex = (uint)(tripShape.Count - 1);

            // Set begin and end heading. Uses tripShape so must be done after the shape was added.
            SetHeadings(tripEdge, directededge, tripShape, beginIndex);

            // Add the intersecting edges at the node. Skip it if the node was an inner node
            // (excluding start node and end node) of a shortcut that was recovered.
            if (startnode.IsValid() && !edgeItr.StartNodeIsRecovered)
            {
                AddIntersectingEdges(startTile, node, directededge, prevDe, priorOppLocalIndex, graphreader, tripNode);
            }

            ////////////// Prepare for the next iteration

            // Set the endnode of this directed edge as the startnode of the next edge.
            startnode = directededge.EndNode;

            // Save the opposing edge as the previous DirectedEdge (for name consistency)
            if (!directededge.IsTransitLine)
            {
                GraphTilePtr? t2 = directededge.LeavesTile
                    ? graphreader.GetGraphTile(directededge.EndNode)
                    : graphtile;
                if (t2 is null)
                {
                    continue;
                }

                GraphId oppedge = t2.GetOpposingEdgeId(directededge);
                prevDe = t2.DirectedEdge(oppedge);
            }

            // Save the index of the opposing local directed edge at the end node
            priorOppLocalIndex = directededge.OppLocalIdx;
        }

        // Add the last node
        var lastNode = new TripNode();
        GraphTilePtr? lastTile = graphreader.GetGraphTile(startnode);
        if (lastTile is null)
        {
            throw new InvalidOperationException("TripLegBuilder.Build failed: last tile gone");
        }

        lastNode.AdminIndex = GetAdminIndex(
            lastTile.AdminInfo((int)lastTile.Node(startnode).AdminIndex),
            adminInfoMap,
            adminInfoList);
        lastNode.ElapsedCost = path[path.Count - 1].ElapsedCost;
        lastNode.TransitionCost = new Cost(0f, 0f);
        tripPath.Nodes.Add(lastNode);

        // Assign the admins
        foreach (AdminInfo ai in adminInfoList)
        {
            tripPath.Admins.Add(new TripAdmin(ai.CountryIso, ai.CountryText, ai.StateIso, ai.StateText));
        }

        // Set the bounding box of the shape
        SetBoundingBox(tripPath, tripShape);

        // Set decoded + encoded shape
        tripPath.Shape.AddRange(tripShape);
        tripPath.EncodedShape = Encoded.Encode(tripShape);

        if (osmchangeset != 0)
        {
            tripPath.OsmChangeset = osmchangeset;
        }

        // Mirror the ordered-edge convenience list (the non-null node edges, in order).
        foreach (TripNode n in tripPath.Nodes)
        {
            if (n.Edge != null)
            {
                tripPath.Edges.Add(n.Edge);
            }
        }

        // Summary
        tripPath.Summary.HasToll = hasToll;
        tripPath.Summary.HasFerry = hasFerry;
        tripPath.Summary.HasHighway = hasHighway;

        return tripPath;
    }

    // ===================================================================================
    // GetAdminIndex / admin table dedup. Faithful port of the anonymous-namespace GetAdminIndex.
    // ===================================================================================
    private static uint GetAdminIndex(AdminInfo adminInfo, Dictionary<AdminInfo, uint> adminInfoMap, List<AdminInfo> adminInfoList)
    {
        if (!adminInfoMap.TryGetValue(adminInfo, out uint adminIndex))
        {
            // Assign new admin index, add to list + map.
            adminIndex = (uint)adminInfoList.Count;
            adminInfoList.Add(adminInfo);
            adminInfoMap[adminInfo] = adminIndex;
        }

        return adminIndex;
    }

    // ===================================================================================
    // SetHeadings. Faithful port of the anonymous-namespace SetHeadings.
    // ===================================================================================
    private static void SetHeadings(TripEdge tripEdge, DirectedEdgeRec edge, List<PointLL> shape, uint beginIndex)
    {
        float offset = GraphConstants.GetOffsetForHeading(edge.Classification, edge.Use);
        tripEdge.BeginHeading = (uint)Math.Round(
            PointLL.HeadingAlongPolyline(shape, offset, beginIndex, (uint)(shape.Count - 1)));
        tripEdge.EndHeading = (uint)Math.Round(
            PointLL.HeadingAtEndOfPolyline(shape, offset, beginIndex, (uint)(shape.Count - 1)));
    }

    // ===================================================================================
    // SetBoundingBox. Faithful port of the anonymous-namespace SetBoundingBox: the AABB2<PointLL>
    // over the leg shape (min lng/lat, max lng/lat). PointLL is not a PointXY<double>, so the bbox
    // is computed directly here rather than via Aabb2T's point-list ctor.
    // ===================================================================================
    private static void SetBoundingBox(TripLeg tripPath, List<PointLL> shape)
    {
        if (shape.Count == 0)
        {
            return;
        }

        double minx = double.MaxValue, miny = double.MaxValue;
        double maxx = -double.MaxValue, maxy = -double.MaxValue;
        foreach (PointLL p in shape)
        {
            if (p.Lng < minx)
            {
                minx = p.Lng;
            }

            if (p.Lng > maxx)
            {
                maxx = p.Lng;
            }

            if (p.Lat < miny)
            {
                miny = p.Lat;
            }

            if (p.Lat > maxy)
            {
                maxy = p.Lat;
            }
        }

        tripPath.BoundingBoxMin = new PointLL(minx, miny);
        tripPath.BoundingBoxMax = new PointLL(maxx, maxy);
    }

    // ===================================================================================
    // Shape append helpers: mirror `shape.insert(shape.end(), begin + !include_first_point, end)`.
    // ===================================================================================
    private static void AppendShape(List<PointLL> tripShape, List<PointLL> edgeShape, bool includeFirstPoint)
    {
        int start = includeFirstPoint ? 0 : 1;
        for (int i = start; i < edgeShape.Count; i++)
        {
            tripShape.Add(edgeShape[i]);
        }
    }

    private static void AppendShapeForward(List<PointLL> tripShape, IReadOnlyList<PointLL> shape, bool includeFirstPoint)
    {
        int start = includeFirstPoint ? 0 : 1;
        for (int i = start; i < shape.Count; i++)
        {
            tripShape.Add(shape[i]);
        }
    }

    private static void AppendShapeReverse(List<PointLL> tripShape, IReadOnlyList<PointLL> shape, bool includeFirstPoint)
    {
        // rbegin() + !include_first_point .. rend()
        int start = (shape.Count - 1) - (includeFirstPoint ? 0 : 1);
        for (int i = start; i >= 0; i--)
        {
            tripShape.Add(shape[i]);
        }
    }

    // ===================================================================================
    // AddSignInfo. Faithful port of the anonymous-namespace AddSignInfo (pronunciation excluded).
    // ===================================================================================
    private static void AddSignInfo(IReadOnlyList<SignInfo> edgeSigns, TripSign tripSign)
    {
        foreach (SignInfo sign in edgeSigns)
        {
            var element = new TripSignElement(sign.Text, sign.IsRouteNum);
            switch (sign.Type)
            {
                case Sign.Type.ExitNumber:
                    tripSign.ExitNumbers.Add(element);
                    break;
                case Sign.Type.ExitBranch:
                    tripSign.ExitOntoStreets.Add(element);
                    break;
                case Sign.Type.ExitToward:
                    tripSign.ExitTowardLocations.Add(element);
                    break;
                case Sign.Type.ExitName:
                    tripSign.ExitNames.Add(element);
                    break;
                case Sign.Type.GuideBranch:
                    tripSign.GuideOntoStreets.Add(element);
                    break;
                case Sign.Type.GuideToward:
                    tripSign.GuideTowardLocations.Add(element);
                    break;
                default:
                    // GuidanceView* are excluded; the rest are handled elsewhere (junction names).
                    break;
            }
        }
    }

    // ===================================================================================
    // AddTripEdge. Faithful port of the anonymous-namespace AddTripEdge (default-route subset).
    // ===================================================================================
    private static TripEdge AddTripEdge(
        GraphId edge,
        PathInfo edgeItr,
        TravelMode mode,
        byte travelType,
        DynamicCost? costing,
        DirectedEdgeRec directededge,
        bool driveOnRight,
        TripNode tripNode,
        GraphTilePtr graphtile,
        uint startNodeIdx,
        bool hasJunctionName,
        GraphTilePtr startTile,
        EdgeInfo edgeinfo)
    {
        uint idx = edge.Id();

        var tripEdge = new TripEdge
        {
            EdgeId = edge,
            Forward = directededge.Forward,
            WayId = edgeinfo.WayId,
            RoadClass = directededge.Classification,
            Use = directededge.Use,
            Mode = mode,
            Roundabout = directededge.Roundabout,
            Toll = directededge.Toll,
            Tunnel = directededge.Tunnel,
            Bridge = directededge.Bridge,
            Unpaved = directededge.Unpaved,
            InternalIntersection = directededge.Internal,
            DestinationOnly = directededge.DestOnly,
            DriveOnLeft = !driveOnRight,
            Surface = directededge.Surface,
            LaneCount = directededge.LaneCount,
            SpeedLimit = edgeinfo.SpeedLimit,
            DefaultSpeed = directededge.Speed,
            TruckSpeed = directededge.TruckSpeed,
            TruckRoute = directededge.TruckRoute,
        };

        tripNode.Edge = tripEdge;

        // Add (untagged) names, and tunnel/bridge tagged names.
        List<(string Name, bool IsRouteNum, byte Type)> namesAndTypes = edgeinfo.GetNamesAndTypes(true);
        foreach ((string Name, bool IsRouteNum, byte Type) nt in namesAndTypes)
        {
            if (nt.Type == NotTagged)
            {
                tripEdge.Names.Add(nt.Name);
            }
            else if (nt.Type == (byte)TaggedValue.Tunnel || nt.Type == (byte)TaggedValue.Bridge)
            {
                tripEdge.TunnelNames.Add(nt.Name);
            }
        }

        // Set the signs (if the directed edge has sign information).
        if (directededge.Sign)
        {
            List<SignInfo> edgeSigns = graphtile.GetSigns(idx);
            if (edgeSigns.Count != 0)
            {
                var sign = new TripSign();
                AddSignInfo(edgeSigns, sign);
                if (!sign.IsEmpty)
                {
                    tripEdge.Sign = sign;
                }
            }
        }

        // Process the named junctions at nodes.
        if (hasJunctionName)
        {
            List<SignInfo> nodeSigns = startTile.GetSigns(startNodeIdx, true);
            if (nodeSigns.Count != 0)
            {
                TripSign tripSign = tripEdge.Sign ??= new TripSign();
                foreach (SignInfo sign in nodeSigns)
                {
                    if (sign.Type == Sign.Type.JunctionName)
                    {
                        tripSign.JunctionNames.Add(new TripSignElement(sign.Text, sign.IsRouteNum));
                    }
                }
            }
        }

        // Turn lanes.
        if (directededge.TurnLanes)
        {
            List<ushort> turnlanes = graphtile.TurnLanes(idx);
            tripEdge.TurnLanes.AddRange(turnlanes);
        }

        // Speed (KPH) used by the costing for this edge.
        if (costing != null && mode != TravelMode.PublicTransit)
        {
            byte flowSources = 0;
            Cost edgeCost = costing.EdgeCost(directededge, edge, graphtile, TimeInfo.Invalid(), ref flowSources);
            if (edgeCost.Secs > 0f)
            {
                tripEdge.SpeedKph = directededge.Length / edgeCost.Secs * Constants.MetersPerSecToKph;
            }
        }

        // Traversability for the travel mode. Faithful port of the forward/reverse branch.
        ushort accessMask = mode switch
        {
            TravelMode.Bicycle => GraphConstants.BicycleAccess,
            TravelMode.Drive => GraphConstants.AutoAccess,
            TravelMode.Pedestrian => GraphConstants.PedestrianAccess,
            TravelMode.PublicTransit => GraphConstants.PedestrianAccess,
            _ => 0,
        };

        bool fwd = (directededge.ForwardAccess & accessMask) != 0;
        bool rev = (directededge.ReverseAccess & accessMask) != 0;
        if (directededge.Forward)
        {
            tripEdge.Traversability =
                fwd && rev ? TripTraversability.Both :
                fwd && !rev ? TripTraversability.Forward :
                !fwd && rev ? TripTraversability.Backward :
                TripTraversability.None;
        }
        else
        {
            tripEdge.Traversability =
                fwd && rev ? TripTraversability.Both :
                !fwd && rev ? TripTraversability.Forward :
                fwd && !rev ? TripTraversability.Backward :
                TripTraversability.None;
        }

        // Time restrictions along the path.
        tripEdge.HasTimeRestrictions = edgeItr.RestrictionIndex != GraphConstants.InvalidRestriction;

        // Travel mode is captured in tripEdge.Mode above (the C++ set_travel_mode mapping collapses
        // to the same mode for the auto/truck subset; bicycle dismount/steps handling is out of scope).
        return tripEdge;
    }

    // ===================================================================================
    // AddTripIntersectingEdge. Faithful port of the anonymous-namespace AddTripIntersectingEdge.
    // ===================================================================================
    private static void AddTripIntersectingEdge(
        GraphTilePtr graphtile,
        DirectedEdgeRec directededge,
        DirectedEdgeRec? prevDe,
        uint localEdgeIndex,
        NodeInfo nodeinfo,
        TripNode tripNode,
        DirectedEdgeRec intersectingDe)
    {
        var intersectingEdge = new TripIntersectingEdge
        {
            BeginHeading = nodeinfo.Heading(localEdgeIndex),
        };

        // Walkability
        intersectingEdge.Walkability = DetermineTraversability(intersectingDe, GraphConstants.PedestrianAccess);

        // Cyclability
        intersectingEdge.Cyclability = DetermineTraversability(intersectingDe, GraphConstants.BicycleAccess);

        // Driveability (from node local driveability)
        intersectingEdge.Driveability = (TripTraversability)(byte)nodeinfo.LocalDriveability(localEdgeIndex);

        // Name consistency (prev / current path edge vs intersecting edge)
        intersectingEdge.PrevNameConsistency = prevDe?.NameConsistencyAt(localEdgeIndex) ?? false;
        intersectingEdge.CurrNameConsistency = directededge.NameConsistencyAt(localEdgeIndex);

        // Names
        EdgeInfo edgeinfo = graphtile.EdgeInfo(intersectingDe);
        List<(string Name, bool IsRouteNum, byte Type)> namesAndTypes = edgeinfo.GetNamesAndTypes(true);
        foreach ((string Name, bool IsRouteNum, byte Type) nt in namesAndTypes)
        {
            if (nt.Type == NotTagged)
            {
                intersectingEdge.Names.Add(nt.Name);
            }
        }

        intersectingEdge.Use = intersectingDe.Use;
        intersectingEdge.RoadClass = intersectingDe.Classification;
        intersectingEdge.LaneCount = intersectingDe.LaneCount;

        tripNode.IntersectingEdges.Add(intersectingEdge);
    }

    private static TripTraversability DetermineTraversability(DirectedEdgeRec de, ushort accessMask)
    {
        bool fwd = (de.ForwardAccess & accessMask) != 0;
        bool rev = (de.ReverseAccess & accessMask) != 0;
        if (fwd)
        {
            return rev ? TripTraversability.Both : TripTraversability.Forward;
        }

        return rev ? TripTraversability.Backward : TripTraversability.None;
    }

    // ===================================================================================
    // AddIntersectingEdges. Faithful port of the anonymous-namespace AddIntersectingEdges
    // (same-level edges + NodeTransition edges; shortcut/opposing/path/construction skipping).
    // ===================================================================================
    private static void AddIntersectingEdges(
        GraphTilePtr startTile,
        NodeInfo node,
        DirectedEdgeRec directededge,
        DirectedEdgeRec? prevDe,
        uint priorOppLocalIndex,
        GraphReader graphreader,
        TripNode tripNode)
    {
        // Iterate through edges on this level to find any intersecting edges.
        uint edgeIndex = node.EdgeIndex;
        for (uint idx1 = 0; idx1 < node.EdgeCount; ++idx1)
        {
            DirectedEdgeRec intersectingEdge = startTile.DirectedEdge((int)(edgeIndex + idx1));

            // Skip shortcut edges AND the opposing edge of the previous edge in the path AND
            // the current edge in the path AND the superseded edge of the current edge in the path
            // (if the current edge in the path is a shortcut) AND construction edges.
            if (intersectingEdge.IsShortcut ||
                intersectingEdge.LocalEdgeIdx == priorOppLocalIndex ||
                intersectingEdge.LocalEdgeIdx == directededge.LocalEdgeIdx ||
                (directededge.IsShortcut && (directededge.Shortcut & intersectingEdge.Superseded) != 0) ||
                intersectingEdge.Use == Use.Construction)
            {
                continue;
            }

            AddTripIntersectingEdge(startTile, directededge, prevDe, intersectingEdge.LocalEdgeIdx, node, tripNode, intersectingEdge);
        }

        // Add intersecting edges on different levels (follow NodeTransitions).
        if (node.TransitionCount > 0)
        {
            uint transIndex = node.TransitionIndex;
            for (uint i = 0; i < node.TransitionCount; ++i)
            {
                NodeTransition trans = startTile.GetNodeTransitions(node)[(int)i];
                GraphId endnode = trans.EndNode();
                GraphTilePtr? endtile = graphreader.GetGraphTile(endnode);
                if (endtile is null)
                {
                    continue;
                }

                NodeInfo nodeinfo2 = endtile.Node(endnode);
                uint edgeIndex2 = nodeinfo2.EdgeIndex;
                for (uint idx2 = 0; idx2 < nodeinfo2.EdgeCount; ++idx2)
                {
                    DirectedEdgeRec intersectingEdge2 = endtile.DirectedEdge((int)(edgeIndex2 + idx2));

                    if (intersectingEdge2.IsShortcut ||
                        intersectingEdge2.LocalEdgeIdx == priorOppLocalIndex ||
                        intersectingEdge2.LocalEdgeIdx == directededge.LocalEdgeIdx ||
                        intersectingEdge2.Use == Use.Construction)
                    {
                        continue;
                    }

                    AddTripIntersectingEdge(endtile, directededge, prevDe, intersectingEdge2.LocalEdgeIdx, nodeinfo2, tripNode, intersectingEdge2);
                }

                _ = transIndex;
            }
        }
    }
}
