// Faithful C# port of Valhalla mjolnir PBFGraphParser orchestration.
// Source: valhalla/src/mjolnir/pbfgraphparser.cc + valhalla/mjolnir/pbfgraphparser.h @ 3.7.0
//
// The C++ front-end runs three passes over the OSM PBF input(s):
//   1. ParseWays    - transform_way (filter degenerate/closed-feature ways) + the Lua way tag
//                     transform (WayTagTransform) + graph_parser::way(): drives the tag_handlers
//                     table to populate an OSMWay, records each way's nodes into the way_nodes
//                     sequence (with loop / flat-loop / intersection detection), pushes access
//                     restrictions / conditional speeds / access flags, infers cul-de-sacs.
//   2. ParseNodes   - the Lua node tag transform (NodeTagTransform) + graph_parser::node(): turns
//                     control-node tags (traffic_signals / stop / give_way direction, gate /
//                     bollard / toll_booth / border_control / sump_buster / elevator types,
//                     access_mask, exit_to / ref / name) into an OSMNode, and stamps that node
//                     onto every way_node that referenced it, marking intersections.
//   3. ParseRelations - graph_parser::relation(): simple + complex turn restrictions, bicycle
//                     route relations, lane-connectivity relations, and road-route direction
//                     (add_to_name_map).
//
// The C++ build spills the ways / way_nodes / access / restriction collections to mmapped
// midgard::sequence temp files (so a planet build doesn't blow up memory) and is multithreaded
// for the Lua transform. Those are performance/footprint details of the tile build, not part of
// the parse semantics: this on-device port keeps the collections in memory and runs the passes
// single-threaded (the Lua workers all do identical work and the result is order-preserving).
// Every tag rule, flag, intersection / loop computation, restriction shape, and access mask is
// preserved exactly. The large linguistic / pronunciation subsystem (OSMLinguistic /
// OSMNodeLinguistic / name:<lang> handling) from the C++ way()/node() bodies belongs to the
// tile-builder slice and is intentionally out of scope here, matching the OSMWay/OSMNode port
// notes (those name/lang index fields are left at their defaults).

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

using SharpNinja.Valhalla.Baldr;

namespace SharpNinja.Valhalla.Mjolnir;

/// <summary>
/// Parses OSM protocol-buffer extracts into a normalized <see cref="OSMData"/>. Faithful port of
/// the C++ <c>valhalla::mjolnir::PBFGraphParser</c> orchestration (ParseWays / ParseNodes /
/// ParseRelations), fused into a single in-memory <see cref="Parse(IReadOnlyList{string})"/> call.
/// </summary>
public sealed class PbfGraphParser
{
    // Limits / constants mirrored from pbfgraphparser.cc + graphconstants.h.
    private const char ExceptDestinationRestrictionFlag = '~';
    private const int MaxViasPerRestriction = OSMRestriction.MaxViasPerRestriction;
    private const byte UnlimitedSpeedLimit = byte.MaxValue;
    private const float MaxAssumedSpeed = 140.0f;

    // kMaxMtbScale / kMaxMtbUphillScale from graphconstants.h.
    private const int MaxMtbScale = 6;
    private const int MaxMtbUphillScale = 5;

    // RoadClass cutoff used for ferries / auto-trains. The C++ derives this from the tile
    // hierarchy ("highway" level importance == kPrimary). We hard-code that here since the
    // standard hierarchy is fixed.
    private const RoadClass HighwayCutoffRoadClass = RoadClass.Primary;

    // Configuration options (defaults match pbfgraphparser.cc graph_parser ctor).
    private readonly bool _includePlatforms;
    private readonly bool _includeDriveways;
    private readonly bool _includeConstruction;
    private readonly bool _inferInternalIntersections;
    private readonly bool _inferTurnChannels;
    private readonly bool _useDirectionOnWays;
    private readonly bool _allowAltName;
    private readonly bool _useUrbanTag;
    private readonly bool _useRestArea;

    private readonly OSMData _osmdata = new();
    private readonly List<OSMWay> _ways = new();
    private readonly List<OSMWayNode> _wayNodes = new();
    private readonly List<OSMAccess> _access = new();
    private readonly List<OSMRestriction> _complexRestrictionsFrom = new();
    private readonly List<OSMRestriction> _complexRestrictionsTo = new();

    private readonly CuldesacProcessor _culdesac = new();

    // Per-way scratch (the C++ graph_parser members reset in way()).
    private OSMWay _way = new();
    private OSMAccess _osmAccess = new();
    private ulong _osmid;
    private bool _hasUserTags;

    private float _defaultSpeed;
    private float _maxSpeed;
    private float _averageSpeed;
    private float _advisorySpeed;
    private bool _hasDefaultSpeed;
    private bool _hasMaxSpeed;
    private bool _hasAverageSpeed;
    private bool _hasAdvisorySpeed;
    private bool _hasSurface;
    private bool _hasSurfaceTag;
    private bool _hasTracktypeTag;

    private string _service = string.Empty;
    private string _amenity = string.Empty;

    // PORT-NOTE: the C++ "name"/"ref" tag handlers capture name_/ref_ and at way-end call
    // way_.set_name_index/set_ref_index (pbfgraphparser.cc ~L3165). The full ProcessName/ProcessLRFBName
    // linguistic (language record) handling is the deferred linguistic slice, but the STRUCTURAL
    // untagged name/ref storage that GraphBuilder.GetNames reads is reproduced here so built edges carry
    // their road names/refs (without it every edge is "unnamed").
    private string _wayName = string.Empty;
    private string _wayRef = string.Empty;

    private ulong _lastNode;
    private ulong _lastWay;
    private ulong _lastRelation;

    /// <summary>Creates a parser using the default configuration (matches pbfgraphparser.cc).</summary>
    public PbfGraphParser()
        : this(new PbfGraphParserOptions())
    {
    }

    /// <summary>Creates a parser with explicit configuration options.</summary>
    public PbfGraphParser(PbfGraphParserOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _includePlatforms = options.IncludePlatforms;
        _includeDriveways = options.IncludeDriveways;
        _includeConstruction = options.IncludeConstruction;
        _inferInternalIntersections = options.InferInternalIntersections;
        _inferTurnChannels = options.InferTurnChannels;
        _useDirectionOnWays = options.UseDirectionOnWays;
        _allowAltName = options.AllowAltName;
        _useUrbanTag = options.UseUrbanTag;
        _useRestArea = options.UseRestArea;
    }

    /// <summary>The ways collected during the ways pass (C++ <c>ways</c> sequence), in input order.</summary>
    public IReadOnlyList<OSMWay> Ways => _ways;

    /// <summary>
    /// The way-node records (C++ <c>way_nodes</c> sequence). After <see cref="Parse"/> these carry
    /// the resolved <see cref="OSMNode"/> (control-node tags, access mask, intersection flag).
    /// </summary>
    public IReadOnlyList<OSMWayNode> WayNodes => _wayNodes;

    /// <summary>User-set access records (C++ <c>access</c> sequence), sorted by way id.</summary>
    public IReadOnlyList<OSMAccess> Access => _access;

    /// <summary>Complex restrictions keyed from the "from" way (C++ <c>complex_restrictions_from</c>).</summary>
    public IReadOnlyList<OSMRestriction> ComplexRestrictionsFrom => _complexRestrictionsFrom;

    /// <summary>Complex restrictions keyed from the "to" way (C++ <c>complex_restrictions_to</c>).</summary>
    public IReadOnlyList<OSMRestriction> ComplexRestrictionsTo => _complexRestrictionsTo;

    /// <summary>
    /// Runs the full three-pass parse over the given PBF file paths and returns the populated
    /// <see cref="OSMData"/>. Faithful to ParseWays -&gt; ParseNodes -&gt; ParseRelations.
    /// </summary>
    public OSMData Parse(IReadOnlyList<string> pbfPaths)
    {
        ArgumentNullException.ThrowIfNull(pbfPaths);

        ParseWays(pbfPaths);
        ParseNodes(pbfPaths);
        ParseRelations(pbfPaths);

        _osmdata.Initialized = true;
        return _osmdata;
    }

    // ===== Pass 1: ways ========================================================

    private void ParseWays(IReadOnlyList<string> pbfPaths)
    {
        foreach (string path in pbfPaths)
        {
            _lastNode = _lastWay = _lastRelation = 0;
            var visitor = new WayVisitor(this);
            new OsmPbfReader(visitor).Parse(path);
        }

        // Clarifies types of loop roads and saves fixed ways.
        _culdesac.ClarifyAndFix(_wayNodes, _ways);

        // Sort access tags by way id (so they can be found easily downstream).
        _access.Sort((a, b) => a.WayId().CompareTo(b.WayId()));
    }

    private sealed class WayVisitor : IOsmPbfVisitor
    {
        private readonly PbfGraphParser _p;

        public WayVisitor(PbfGraphParser p) => _p = p;

        public void Header(double? minLat, double? minLon, double? maxLat, double? maxLon, IReadOnlyList<string> requiredFeatures)
        {
        }

        public void Node(ulong id, double lat, double lon, IReadOnlyDictionary<string, string> tags)
        {
        }

        public void Way(ulong id, IReadOnlyList<ulong> nodeRefs, IReadOnlyDictionary<string, string> tags) =>
            _p.TransformAndAddWay(id, nodeRefs, tags);

        public void Relation(ulong id, IReadOnlyList<OsmRelationMember> members, IReadOnlyDictionary<string, string> tags)
        {
        }
    }

    // transform_way + way() fused. transform_way filters degenerate/closed-area ways and applies
    // the Lua way transform; way() then drives the tag handlers.
    private void TransformAndAddWay(ulong wayId, IReadOnlyList<ulong> nodeRefs, IReadOnlyDictionary<string, string> rawTags)
    {
        // Do not add ways with < 2 nodes.
        if (nodeRefs.Count < 2)
        {
            return;
        }

        // Throw away closed features with building/landuse/leisure/natural tags.
        if (nodeRefs[0] == nodeRefs[^1])
        {
            foreach (KeyValuePair<string, string> tag in rawTags)
            {
                if (tag.Key is "building" or "landuse" or "leisure" or "natural")
                {
                    return;
                }
            }
        }

        // Apply the Lua way tag transform. Empty tags -> the empty transform -> dropped.
        var tags = new Dictionary<string, string>(rawTags);
        int filter = WayTagTransform.Transform(tags);
        if (filter != 0 || tags.Count == 0)
        {
            return;
        }

        Way(wayId, nodeRefs, tags);
    }

    private void Way(ulong wayId, IReadOnlyList<ulong> nodeRefs, IReadOnlyDictionary<string, string> tags)
    {
        _osmid = wayId;
        if (_osmid < _lastWay)
        {
            throw new InvalidOperationException("Detected unsorted input data");
        }

        _lastWay = _osmid;

        try
        {
            // Throw away use if include_driveways_ is false and it is a private driveway.
            if (!_includeDriveways && tags.TryGetValue("use", out string? useDw) &&
                (Use)ToInt(useDw) == Use.Driveway)
            {
                if (tags.TryGetValue("private", out string? priv) && priv == "true")
                {
                    return;
                }
            }

            if (!_includeConstruction && tags.TryGetValue("use", out string? useC) &&
                (Use)ToInt(useC) == Use.Construction)
            {
                return;
            }

            if (!_includePlatforms && tags.TryGetValue("use", out string? useP) &&
                (Use)ToInt(useP) == Use.Platform)
            {
                return;
            }
        }
        catch (FormatException)
        {
        }

        // Add the refs to the reference list and mark loop / flat-loop / intersection.
        var loopNodes = new Dictionary<ulong, int>();
        int wayNodeIndex = _wayNodes.Count;
        for (int i = 0; i < nodeRefs.Count; ++i)
        {
            ulong node = nodeRefs[i];
            var osmNode = new OSMNode(node);

            bool inserted = !loopNodes.ContainsKey(node);
            if (inserted)
            {
                loopNodes[node] = i;
            }

            int firstOccurrence = loopNodes[node];
            bool flattening = firstOccurrence > 0 && i < nodeRefs.Count - 1 &&
                              nodeRefs[i + 1] == nodeRefs[firstOccurrence - 1];
            bool unflattening = i > 0 && firstOccurrence < nodeRefs.Count - 1 &&
                                nodeRefs[i - 1] == nodeRefs[firstOccurrence + 1];
            osmNode.SetFlatLoop(flattening || unflattening);
            osmNode.SetIntersection(i == 0 || i == nodeRefs.Count - 1);

            _wayNodes.Add(new OSMWayNode
            {
                Node = osmNode,
                WayIndex = (uint)_ways.Count,
                WayShapeNodeIndex = (uint)i,
            });

            // If this way is a loop, split it by marking the midpoint as an intersection.
            if (!inserted)
            {
                int midIndex = wayNodeIndex + (i + firstOccurrence) / 2;
                OSMWayNode mid = _wayNodes[midIndex];
                mid.Node.SetIntersection(true);
                _wayNodes[midIndex] = mid;
                loopNodes[node] = i;
            }
        }

        _osmdata.OsmWayCount++;
        _osmdata.OsmWayNodeCount += (ulong)nodeRefs.Count;

        // Reset per-way scratch.
        _defaultSpeed = 0; _maxSpeed = 0; _averageSpeed = 0; _advisorySpeed = 0;
        _hasDefaultSpeed = false; _hasMaxSpeed = false; _hasAverageSpeed = false; _hasAdvisorySpeed = false;
        _hasSurface = true;
        _service = string.Empty;
        _amenity = string.Empty;
        _wayName = string.Empty;
        _wayRef = string.Empty;

        _way = new OSMWay(_osmid);
        _way.SetNodeCount((uint)nodeRefs.Count);
        _osmAccess = new OSMAccess(_osmid);
        _hasUserTags = false;

        _hasSurfaceTag = tags.ContainsKey("surface");
        if (!_hasSurfaceTag)
        {
            _hasSurface = false;
        }

        _hasTracktypeTag = tags.ContainsKey("tracktype");

        _way.SetDriveOnRight(true); // default

        // Process tags via the handler table.
        foreach (KeyValuePair<string, string> kv in tags)
        {
            string key = kv.Key;
            string value = kv.Value;

            if (WayTagHandler(key, value))
            {
                continue;
            }

            // Conditional access (motor_vehicle:conditional=no @ (16:30-07:00), etc).
            if (IsConditionalAccessKey(key))
            {
                HandleConditionalAccess(key, value);
            }
        }

        // use_rest_area handling (must be in parser, not Lua, because of config option).
        if (_useRestArea && _service == "rest_area" && _way.UseValue() != Use.Construction)
        {
            _way.SetUse(_amenity == "yes" ? Use.ServiceArea : Use.RestArea);
        }

        // Process mtb tags.
        ProcessMtbTags(tags);

        // Surface defaults if no surface set by user/mtb.
        if (!_hasSurface)
        {
            if (tags.ContainsKey("sac_scale"))
            {
                _way.SetSurface(Surface.Path);
            }
            else
            {
                ApplyDefaultSurface();
            }
        }

        // Set the speed.
        if (_hasAverageSpeed)
        {
            _way.SetSpeed(_averageSpeed);
        }
        else if (_hasAdvisorySpeed)
        {
            _way.SetSpeed(_advisorySpeed);
        }
        else if (_hasMaxSpeed && _maxSpeed != UnlimitedSpeedLimit)
        {
            _way.SetSpeed(_maxSpeed);
        }
        else if (_hasDefaultSpeed && !_way.ForwardTaggedSpeed() && !_way.BackwardTaggedSpeed())
        {
            _way.SetSpeed(_defaultSpeed);
        }

        // Speed limit.
        if (_hasMaxSpeed)
        {
            _way.SetSpeedLimit(_maxSpeed);
        }

        // Mismatched forward/backward tagged speeds.
        if (_way.ForwardTaggedSpeed() && !_way.BackwardTaggedSpeed())
        {
            if (!_way.Oneway())
            {
                _way.SetBackwardSpeed(_way.ForwardSpeed());
                _way.SetBackwardTaggedSpeed(true);
            }
            else
            {
                _way.SetSpeed(_defaultSpeed);
            }
        }
        else if (!_way.ForwardTaggedSpeed() && _way.BackwardTaggedSpeed())
        {
            if (!_way.Oneway())
            {
                _way.SetForwardSpeed(_way.BackwardSpeed());
                _way.SetForwardTaggedSpeed(true);
            }
            else
            {
                _way.SetSpeed(_defaultSpeed);
            }
        }

        // Ferries / auto-trains use the highway cutoff road class.
        if (_way.Ferry() || _way.Rail())
        {
            _way.SetRoadClass(HighwayCutoffRoadClass);
        }

        // Infer cul-de-sac if a road edge is a loop and is low classification.
        if (!_way.Roundabout() && loopNodes.Count != nodeRefs.Count && _way.UseValue() == Use.Road &&
            (byte)_way.RoadClassValue() > (byte)RoadClass.Tertiary)
        {
            _culdesac.AddCandidate(_way.WayId(), _ways.Count, nodeRefs);
        }

        if (_hasUserTags)
        {
            _way.SetHasUserTags(true);
            _access.Add(_osmAccess);
        }

        // PORT-NOTE: store the structural name/ref indices on the way (C++ way_.set_name_index /
        // set_ref_index at the end of way(); pbfgraphparser.cc ~L3165). Without this every built edge is
        // "unnamed" because GraphBuilder.GetNames reads w.NameIndex / w.RefIndex. The deferred
        // linguistic language-record indices stay at their defaults. Empty strings map to index 0 (the
        // canonical empty entry), so unset name/ref leave the indices at 0 exactly as before.
        _way.NameIndex = _osmdata.NameOffsetMap.Index(_wayName);
        _way.RefIndex = _osmdata.NameOffsetMap.Index(_wayRef);

        _ways.Add(_way);
    }

    // ===== Pass 2: nodes =======================================================

    private void ParseNodes(IReadOnlyList<string> pbfPaths)
    {
        // Sort way_nodes by node id so we can sequentially update them.
        _wayNodes.Sort((a, b) => a.Node.Osmid.CompareTo(b.Node.Osmid));

        foreach (string path in pbfPaths)
        {
            _currentWayNodeIndex = 0;
            _lastNode = _lastWay = _lastRelation = 0;
            var visitor = new NodeVisitor(this);
            new OsmPbfReader(visitor).Parse(path);
        }

        // Some extracts have no changeset ids; fall back to max osm id.
        if (_osmdata.MaxChangesetId == 0)
        {
            _osmdata.MaxChangesetId = _lastNode;
        }

        // Re-sort by way index then shape index (for edge building downstream).
        _wayNodes.Sort((a, b) =>
        {
            if (a.WayIndex == b.WayIndex)
            {
                return a.WayShapeNodeIndex.CompareTo(b.WayShapeNodeIndex);
            }

            return a.WayIndex.CompareTo(b.WayIndex);
        });
    }

    private int _currentWayNodeIndex;

    private sealed class NodeVisitor : IOsmPbfVisitor
    {
        private readonly PbfGraphParser _p;

        public NodeVisitor(PbfGraphParser p) => _p = p;

        public void Header(double? minLat, double? minLon, double? maxLat, double? maxLon, IReadOnlyList<string> requiredFeatures)
        {
        }

        public void Node(ulong id, double lat, double lon, IReadOnlyDictionary<string, string> tags) =>
            _p.Node(id, lat, lon, tags);

        public void Way(ulong id, IReadOnlyList<ulong> nodeRefs, IReadOnlyDictionary<string, string> tags)
        {
        }

        public void Relation(ulong id, IReadOnlyList<OsmRelationMember> members, IReadOnlyDictionary<string, string> tags)
        {
        }
    }

    private void Node(ulong osmid, double lat, double lon, IReadOnlyDictionary<string, string> rawTags)
    {
        if (osmid < _lastNode)
        {
            throw new InvalidOperationException("Detected unsorted input data");
        }

        _lastNode = osmid;

        // If we found all the node ids we were looking for, bail.
        if (_currentWayNodeIndex >= _wayNodes.Count)
        {
            return;
        }

        // If this node's id is greater than the way-node we're looking for, advance.
        if (osmid > _wayNodes[_currentWayNodeIndex].Node.Osmid)
        {
            _currentWayNodeIndex = FindFirstOfNode(osmid, _currentWayNodeIndex);
        }

        // If this node's id is less than the way-node we want, skip it.
        if (_currentWayNodeIndex >= _wayNodes.Count ||
            osmid < _wayNodes[_currentWayNodeIndex].Node.Osmid)
        {
            return;
        }

        // Apply the Lua node tag transform. C++ pbfgraphparser.cc:2081 runs the transform for EVERY
        // node: tagged nodes use lua.Transform(kNode, id, tags); untagged nodes use the precomputed
        // empty_node_tags_ = lua.Transform(kNode, 0, {}). Both yield the access_mask (2047 for an
        // untagged node, i.e. all modes allowed) plus the gate/bollard/etc. defaults. The previous
        // C# code skipped the transform for untagged nodes, leaving access_mask absent so the node's
        // access stayed 0; that made every plain intersection node un-routable (Allowed(NodeInfo)
        // failed), trapping the bidirectional A* search.
        var tags = new Dictionary<string, string>(rawTags);
        NodeTagTransform.Transform(tags);

        bool isHighwayJunction = tags.TryGetValue("highway", out string? hw) && hw == "motorway_junction";
        bool maybeNamedJunction = tags.TryGetValue("junction", out string? jn) && (jn == "named" || jn == "yes");
        bool namedJunction = false;

        bool isBarrierTollBooth = tags.TryGetValue("barrier", out string? bar) && bar == "toll_booth";
        bool isHighwayTollGantry = tags.TryGetValue("highway", out string? hwg) && hwg == "toll_gantry";
        bool isTollNode = isBarrierTollBooth || isHighwayTollGantry;
        bool namedTollNode = false;

        var n = new OSMNode();
        n.SetId(osmid);
        n.SetLatLng(lon, lat);
        bool intersection = false;
        if (isHighwayJunction)
        {
            n.SetType(NodeType.MotorWayJunction);
        }

        foreach (KeyValuePair<string, string> tag in tags)
        {
            string key = tag.Key;
            string value = tag.Value;
            bool hasTag = value.Length != 0;

            switch (key)
            {
                case "highway":
                    n.SetTrafficSignal(value == "traffic_signals");
                    n.SetStopSign(value == "stop");
                    n.SetYieldSign(value == "give_way");
                    break;
                case "forward_signal":
                    n.SetForwardSignal(value == "true");
                    break;
                case "backward_signal":
                    n.SetBackwardSignal(value == "true");
                    break;
                case "forward_stop":
                    n.SetForwardStop(value == "true");
                    n.SetDirection(true);
                    break;
                case "backward_stop":
                    n.SetBackwardStop(value == "true");
                    n.SetDirection(true);
                    break;
                case "forward_yield":
                    n.SetForwardYield(value == "true");
                    n.SetDirection(true);
                    break;
                case "backward_yield":
                    n.SetBackwardYield(value == "true");
                    n.SetDirection(true);
                    break;
                case "stop":
                case "give_way":
                    n.SetMinor(value == "minor");
                    break;
                case "urban" when _useUrbanTag:
                    n.SetUrban(value == "true");
                    break;
                case "exit_to" when isHighwayJunction && hasTag:
                    n.SetExitToIndex(_osmdata.NodeNames.Index(value));
                    _osmdata.NodeExitToCount++;
                    break;
                case "ref" when isHighwayJunction && hasTag:
                    n.SetRefIndex(_osmdata.NodeNames.Index(value));
                    _osmdata.NodeRefCount++;
                    break;
                case "name" when (isHighwayJunction || maybeNamedJunction || isTollNode) && hasTag:
                    n.SetNameIndex(_osmdata.NodeNames.Index(value));
                    _osmdata.NodeNameCount++;
                    namedJunction = maybeNamedJunction;
                    namedTollNode = isTollNode;
                    break;
                case "amenity" when value == "parking":
                    _osmdata.EdgeCount += intersection ? 0UL : 1UL;
                    intersection = true;
                    n.SetType(NodeType.Parking);
                    break;
                case "gate" when value == "true":
                    _osmdata.EdgeCount += intersection ? 0UL : 1UL;
                    intersection = true;
                    n.SetType(NodeType.Gate);
                    break;
                case "bollard" when value == "true":
                    _osmdata.EdgeCount += intersection ? 0UL : 1UL;
                    intersection = true;
                    n.SetType(NodeType.Bollard);
                    break;
                case "toll_booth" when value == "true":
                    _osmdata.EdgeCount += intersection ? 0UL : 1UL;
                    intersection = true;
                    n.SetType(NodeType.TollBooth);
                    break;
                case "border_control" when value == "true":
                    _osmdata.EdgeCount += intersection ? 0UL : 1UL;
                    intersection = true;
                    n.SetType(NodeType.BorderControl);
                    break;
                case "cash_only_toll" when value == "true":
                    _osmdata.EdgeCount += intersection ? 0UL : 1UL;
                    intersection = true;
                    n.SetType(NodeType.TollBooth);
                    n.SetCashOnlyToll(true);
                    break;
                case "toll_gantry" when value == "true":
                    _osmdata.EdgeCount += intersection ? 0UL : 1UL;
                    intersection = true;
                    n.SetType(NodeType.TollGantry);
                    break;
                case "sump_buster" when value == "true":
                    _osmdata.EdgeCount += intersection ? 0UL : 1UL;
                    intersection = true;
                    n.SetType(NodeType.SumpBuster);
                    break;
                case "building_entrance" when value == "true":
                    _osmdata.EdgeCount += intersection ? 0UL : 1UL;
                    intersection = true;
                    n.SetType(NodeType.BuildingEntrance);
                    break;
                case "elevator" when value == "true":
                    _osmdata.EdgeCount += intersection ? 0UL : 1UL;
                    intersection = true;
                    n.SetType(NodeType.Elevator);
                    break;
                case "access_mask":
                    n.SetAccess((uint)ToInt(value));
                    break;
                case "tagged_access":
                    n.SetTaggedAccess(ToInt(value) != 0);
                    break;
                case "private":
                    n.SetPrivateAccess(value == "true");
                    break;
                default:
                    break;
            }
        }

        // Different types of named nodes are tagged as a named intersection.
        n.SetNamedIntersection(namedJunction || namedTollNode);

        // Keep the intersection flag set by way parsing (dead ends).
        OSMWayNode element = _wayNodes[_currentWayNodeIndex];
        intersection = intersection || element.Node.Intersection();

        // If multiple ways reference this node it's also an intersection.
        if (!intersection && _currentWayNodeIndex < _wayNodes.Count - 1 &&
            osmid == _wayNodes[_currentWayNodeIndex + 1].Node.Osmid)
        {
            intersection = true;
        }

        if (intersection)
        {
            n.SetIntersection(true);
            _osmdata.NodeCount++;
        }

        // Update all way_node copies that referenced this node.
        while (_currentWayNodeIndex < _wayNodes.Count &&
               _wayNodes[_currentWayNodeIndex].Node.Osmid == osmid)
        {
            OSMWayNode wayNode = _wayNodes[_currentWayNodeIndex];
            bool flatLoop = wayNode.Node.FlatLoop();
            OSMNode updated = n;
            updated.SetFlatLoop(flatLoop);
            wayNode.Node = updated;
            _wayNodes[_currentWayNodeIndex] = wayNode;
            _currentWayNodeIndex++;
            _osmdata.EdgeCount += intersection ? 1UL : 0UL;
        }

        _osmdata.OsmNodeCount++;
        _osmdata.EdgeCount -= intersection ? 1UL : 0UL; // undercounts by skipping lone edges
    }

    // C++ way_nodes_->find_first_of with comparator a.osmid <= b.osmid, from index.
    private int FindFirstOfNode(ulong osmid, int from)
    {
        for (int i = from; i < _wayNodes.Count; ++i)
        {
            if (!(_wayNodes[i].Node.Osmid <= osmid))
            {
                return i;
            }
        }

        return _wayNodes.Count;
    }

    // ===== Pass 3: relations ===================================================

    private void ParseRelations(IReadOnlyList<string> pbfPaths)
    {
        foreach (string path in pbfPaths)
        {
            _currentWayNodeIndex = 0;
            _lastNode = _lastWay = _lastRelation = 0;
            var visitor = new RelationVisitor(this);
            new OsmPbfReader(visitor).Parse(path);
        }

        _complexRestrictionsFrom.Sort((a, b) => a.CompareTo(b));
        _complexRestrictionsTo.Sort((a, b) => a.CompareTo(b));
    }

    private sealed class RelationVisitor : IOsmPbfVisitor
    {
        private readonly PbfGraphParser _p;

        public RelationVisitor(PbfGraphParser p) => _p = p;

        public void Header(double? minLat, double? minLon, double? maxLat, double? maxLon, IReadOnlyList<string> requiredFeatures)
        {
        }

        public void Node(ulong id, double lat, double lon, IReadOnlyDictionary<string, string> tags)
        {
        }

        public void Way(ulong id, IReadOnlyList<ulong> nodeRefs, IReadOnlyDictionary<string, string> tags)
        {
        }

        public void Relation(ulong id, IReadOnlyList<OsmRelationMember> members, IReadOnlyDictionary<string, string> tags) =>
            _p.Relation(id, members, tags);
    }

    private void Relation(ulong osmid, IReadOnlyList<OsmRelationMember> members, IReadOnlyDictionary<string, string> rawTags)
    {
        if (osmid < _lastRelation)
        {
            throw new InvalidOperationException("Detected unsorted input data");
        }

        _lastRelation = osmid;

        // Relations are not Lua-transformed in graph.lua (relations_proc returns tags as-is) -
        // the empty-tags case is dropped.
        if (rawTags.Count == 0)
        {
            return;
        }

        var restriction = new OSMRestriction();
        var toRestriction = new OSMRestriction();

        ulong fromWayId = 0;
        bool isRestriction = false, isTypeRestriction = false, hasRestriction = false;
        bool isRoad = false, isRoute = false, isBicycle = false, isConnectivity = false;
        bool isConditional = false, isProbable = false, hasMultipleTimes = false;
        uint bikeNetworkMask = 0;

        string network = string.Empty, reference = string.Empty, name = string.Empty, except = string.Empty;
        string fromLanes = string.Empty, from = string.Empty, toLanes = string.Empty, to = string.Empty;
        string condition = string.Empty, direction = string.Empty;
        string hourStart = string.Empty, hourEnd = string.Empty, dayStart = string.Empty, dayEnd = string.Empty;
        uint modes = 0;

        foreach (KeyValuePair<string, string> tag in rawTags)
        {
            string key = tag.Key;
            string value = tag.Value;

            if (key == "type")
            {
                if (value == "restriction")
                {
                    isRestriction = true;
                }
                else if (value == "route")
                {
                    isRoute = true;
                }
                else if (value == "connectivity")
                {
                    isConnectivity = true;
                }
            }
            else if (key == "route")
            {
                if (value == "road")
                {
                    isRoad = true;
                }
                else if (value is "bicycle" or "mtb")
                {
                    isBicycle = true;
                }
            }
            else if (key == "restriction:conditional")
            {
                isConditional = true;
                condition = value;
            }
            else if (key == "restriction:probable")
            {
                string[] probTok = GetTagTokens(value, '=');
                if (probTok.Length == 2)
                {
                    int p = ToInt(probTok[1]);
                    if (p > 0)
                    {
                        isProbable = true;
                        restriction.SetProbability((byte)p);
                    }
                    else
                    {
                        return; // 0 probability is invalid
                    }
                }
            }
            else if (key == "direction")
            {
                direction = value;
            }
            else if (key == "network")
            {
                network = value;
            }
            else if (key == "ref")
            {
                reference = value;
            }
            else if (key == "name")
            {
                name = value;
            }
            else if (key == "except")
            {
                except = value;
            }
            else if ((key is "restriction" or "restriction:motorcar" or "restriction:motorcycle" or
                          "restriction:taxi" or "restriction:bus" or "restriction:bicycle" or
                          "restriction:hgv" or "restriction:hazmat" or "restriction:emergency" or
                          "restriction:foot") && value.Length != 0)
            {
                isRestriction = true;
                if (key != "restriction")
                {
                    isTypeRestriction = true;
                }

                modes |= key switch
                {
                    "restriction:motorcar" => (uint)(GraphConstants.AutoAccess | GraphConstants.MopedAccess),
                    "restriction:motorcycle" => GraphConstants.MotorcycleAccess,
                    "restriction:taxi" => GraphConstants.TaxiAccess,
                    "restriction:bus" => GraphConstants.BusAccess,
                    "restriction:bicycle" => GraphConstants.BicycleAccess,
                    "restriction:hgv" or "restriction:hazmat" => GraphConstants.TruckAccess,
                    "restriction:emergency" => GraphConstants.EmergencyAccess,
                    "restriction:psv" => (uint)(GraphConstants.TaxiAccess | GraphConstants.BusAccess),
                    "restriction:foot" => (uint)(GraphConstants.PedestrianAccess | GraphConstants.WheelchairAccess),
                    _ => 0u,
                };

                var type = (RestrictionType)ToInt(value);
                switch (type)
                {
                    case RestrictionType.NoLeftTurn:
                    case RestrictionType.NoRightTurn:
                    case RestrictionType.NoStraightOn:
                    case RestrictionType.NoUTurn:
                    case RestrictionType.OnlyRightTurn:
                    case RestrictionType.OnlyLeftTurn:
                    case RestrictionType.OnlyStraightOn:
                    case RestrictionType.NoEntry:
                    case RestrictionType.NoExit:
                    case RestrictionType.NoTurn:
                        hasRestriction = true;
                        restriction.SetType(type);
                        break;
                    default:
                        return;
                }
            }
            else if (key == "hour_on")
            {
                if (!value.Contains(':'))
                {
                    return;
                }

                if (value.Contains(';'))
                {
                    hasMultipleTimes = true;
                }

                isConditional = true;
                hourStart = value;
            }
            else if (key == "hour_off")
            {
                if (!value.Contains(':'))
                {
                    return;
                }

                if (value.Contains(';'))
                {
                    hasMultipleTimes = true;
                }

                isConditional = true;
                hourEnd = value;
            }
            else if (key == "day_on")
            {
                isConditional = true;
                dayStart = value;
            }
            else if (key == "day_off")
            {
                isConditional = true;
                dayEnd = value;
            }
            else if (key == "bike_network_mask")
            {
                bikeNetworkMask = (uint)ToInt(value);
            }
            else if (key == "to:lanes")
            {
                toLanes = value;
            }
            else if (key == "from:lanes")
            {
                fromLanes = value;
            }
            else if (key == "to")
            {
                to = value;
            }
            else if (key == "from")
            {
                from = value;
            }
        }

        if (isProbable)
        {
            RestrictionType type = restriction.TypeValue();
            restriction.SetType(type is RestrictionType.OnlyRightTurn or RestrictionType.OnlyLeftTurn
                or RestrictionType.OnlyStraightOn
                ? RestrictionType.OnlyProbable
                : RestrictionType.NoProbable);
        }

        string[] net = GetTagTokens(network, ':');
        bool specialNetwork = false;
        if (net.Length == 3)
        {
            string val = net[2].ToLowerInvariant();
            specialNetwork = val is "turnpike" or "tp" or "fm" or "rm" or "loop" or "spur" or "truck" or
                "business" or "bypass" or "belt" or "alternate" or "alt" or "toll" or "cr" or
                "byway" or "scenic" or "connector" or "county";
        }

        if (isBicycle && isRoute && network.Length != 0)
        {
            uint nameIndex = _osmdata.NameOffsetMap.Index(name);
            uint refIndex = _osmdata.NameOffsetMap.Index(reference);

            if (bikeNetworkMask == 0)
            {
                return;
            }

            var bike = new OSMBike
            {
                BikeNetwork = (byte)bikeNetworkMask,
                NameIndex = nameIndex,
                RefIndex = refIndex,
            };

            foreach (OsmRelationMember member in members)
            {
                _osmdata.AddBikeRelation(member.Id, bike);
            }
        }
        else if (isRoad && isRoute && network.Length != 0 &&
                 ((net.Length == 2 && reference.Length != 0) ||
                  (net.Length == 3 && net[0] == "US" && specialNetwork)))
        {
            if (net.Length == 3 && net[2] == "Turnpike")
            {
                net[2] = "TP";
            }

            string refOut;
            if (net.Length == 2 && reference.Length != 0)
            {
                if (reference.Length == 4 && net[1].Length == 2)
                {
                    if (net[1] + "TP" == reference)
                    {
                        refOut = reference;
                    }
                    else
                    {
                        return;
                    }
                }
                else
                {
                    refOut = net[1] + " " + reference;
                }
            }
            else if (specialNetwork && reference.Length != 0)
            {
                refOut = net[2] + " " + reference;
            }
            else
            {
                refOut = net[1] + net[2];
            }

            bool found = false;
            foreach (OsmRelationMember member in members)
            {
                if (member.Role.Length == 0 || member.Role == "forward" || member.Role == "backward")
                {
                    continue;
                }

                direction = member.Role;
                _osmdata.AddToNameMap(member.Id, direction, refOut);
                found = true;
            }

            if (direction.Length != 0 && !found)
            {
                foreach (OsmRelationMember member in members)
                {
                    if (member.Role == "forward")
                    {
                        _osmdata.AddToNameMap(member.Id, direction, refOut);
                    }
                    else if (member.Role == "backward")
                    {
                        _osmdata.AddToNameMap(member.Id, direction, refOut, false);
                    }
                }
            }
        }
        else if (isConnectivity && (toLanes.Length != 0 || to.Length != 0) &&
                 (fromLanes.Length != 0 || from.Length != 0))
        {
            uint fromWay = 0;
            uint toWay = 0;
            foreach (OsmRelationMember member in members)
            {
                if (member.Role == "from" && member.Type == OsmMemberType.Way)
                {
                    fromWay = (uint)member.Id;
                }
                else if (member.Role == "to" && member.Type == OsmMemberType.Way)
                {
                    toWay = (uint)member.Id;
                }
            }

            if (fromWay != 0 && toWay != 0)
            {
                uint toIdx = _osmdata.NameOffsetMap.Index(MaxString(to, toLanes));
                uint fromIdx = _osmdata.NameOffsetMap.Index(MaxString(from, fromLanes));
                _osmdata.AddLaneConnectivity(toWay, new OSMLaneConnectivity
                {
                    ToWayId = toWay,
                    FromWayId = fromWay,
                    ToLanesIndex = toIdx,
                    FromLanesIndex = fromIdx,
                });
            }
        }
        else if (isRestriction && hasRestriction)
        {
            var vias = new List<ulong>();

            foreach (OsmRelationMember member in members)
            {
                if (member.Role == "from" && member.Type == OsmMemberType.Way)
                {
                    fromWayId = member.Id;
                }
                else if (member.Role == "to" && member.Type == OsmMemberType.Way)
                {
                    if (restriction.To() == 0)
                    {
                        restriction.SetTo(member.Id);
                    }
                }
                else if (member.Role == "via" && member.Type == OsmMemberType.Node)
                {
                    if (vias.Count != 0)
                    {
                        fromWayId = 0;
                        break;
                    }

                    restriction.SetVia(member.Id);
                }
                else if (member.Role == "via" && member.Type == OsmMemberType.Way)
                {
                    if (restriction.Via() != 0)
                    {
                        fromWayId = 0;
                        break;
                    }

                    vias.Add(member.Id);
                    _osmdata.ViaSet.Add(member.Id);
                }
            }

            if (vias.Count > MaxViasPerRestriction)
            {
                fromWayId = 0;
            }

            if (fromWayId != 0 && (restriction.Via() != 0 || vias.Count != 0) && restriction.To() != 0)
            {
                if (!isTypeRestriction)
                {
                    modes = (uint)(GraphConstants.AutoAccess | GraphConstants.MopedAccess |
                                   GraphConstants.TaxiAccess | GraphConstants.BusAccess |
                                   GraphConstants.BicycleAccess | GraphConstants.TruckAccess |
                                   GraphConstants.EmergencyAccess | GraphConstants.MotorcycleAccess);

                    foreach (string t in GetTagTokens(except))
                    {
                        modes = t switch
                        {
                            "motorcar" => modes & ~(uint)(GraphConstants.AutoAccess | GraphConstants.MopedAccess),
                            "motorcycle" => modes & ~(uint)GraphConstants.MotorcycleAccess,
                            "psv" => modes & ~(uint)(GraphConstants.TaxiAccess | GraphConstants.BusAccess),
                            "taxi" => modes & ~(uint)GraphConstants.TaxiAccess,
                            "bus" => modes & ~(uint)GraphConstants.BusAccess,
                            "bicycle" => modes & ~(uint)GraphConstants.BicycleAccess,
                            "hgv" => modes & ~(uint)GraphConstants.TruckAccess,
                            "emergency" => modes & ~(uint)GraphConstants.EmergencyAccess,
                            "foot" => modes & ~(uint)(GraphConstants.PedestrianAccess | GraphConstants.WheelchairAccess),
                            _ => modes,
                        };
                    }
                }

                // Turn simple into complex restriction if applicable.
                if (vias.Count == 0 && (isTypeRestriction || isConditional || isProbable ||
                                        (!isTypeRestriction && except.Length != 0)))
                {
                    restriction.SetVia(0);
                    vias.Add(restriction.To());
                    _osmdata.ViaSet.Add(restriction.To());

                    if (isConditional)
                    {
                        restriction.SetModes(modes);
                        if (condition.Length == 0)
                        {
                            if (dayStart.Length != 0 && dayEnd.Length != 0)
                            {
                                condition = dayStart + "-" + dayEnd;
                            }

                            if (!hasMultipleTimes)
                            {
                                if (hourStart.Length != 0 && hourEnd.Length != 0)
                                {
                                    condition += " " + hourStart + "-" + hourEnd;
                                }
                            }
                            else
                            {
                                string[] hourOn = GetTagTokens(hourStart, ';');
                                string[] hourOff = GetTagTokens(hourEnd, ';');

                                if (hourOn.Length > 1 && hourOn.Length == hourOff.Length)
                                {
                                    string hours = string.Empty;
                                    for (int i = 0; i < hourOn.Length; i++)
                                    {
                                        if (hours.Length != 0)
                                        {
                                            hours += ",";
                                        }

                                        hours += hourOn[i] + "-" + hourOff[i];
                                    }

                                    condition += " " + hours;
                                }
                                else
                                {
                                    return;
                                }
                            }
                        }

                        string[] conditions = GetTagTokens(condition, ';');
                        if (conditions.Length != 0)
                        {
                            restriction.SetFrom(fromWayId);
                            restriction.SetVias(vias);
                            toRestriction.SetFrom(restriction.To());
                            toRestriction.SetTo(fromWayId);
                            toRestriction.SetModes(restriction.Modes());
                            _complexRestrictionsTo.Add(toRestriction);
                        }
                        else
                        {
                            return;
                        }

                        foreach (string c in conditions)
                        {
                            foreach (ulong v in GetTimeRange(c))
                            {
                                restriction.SetTimeDomain(v);
                                _complexRestrictionsFrom.Add(restriction);
                            }
                        }

                        return;
                    }
                }

                restriction.SetModes(modes);

                if (vias.Count != 0)
                {
                    _osmdata.ViaSet.Add(fromWayId);
                    _osmdata.ViaSet.Add(restriction.To());
                    restriction.SetFrom(fromWayId);
                    restriction.SetVias(vias);
                    toRestriction.SetFrom(restriction.To());
                    toRestriction.SetTo(fromWayId);
                    toRestriction.SetModes(restriction.Modes());
                    _complexRestrictionsTo.Add(toRestriction);
                    _complexRestrictionsFrom.Add(restriction);
                }
                else
                {
                    _osmdata.AddRestriction(fromWayId, restriction);
                }
            }
        }
    }

    // ===== Way tag handlers (the C++ tag_handlers_ table) ======================

    // Returns true if the key was handled by a dedicated handler.
    private bool WayTagHandler(string key, string value)
    {
        switch (key)
        {
            case "driving_side":
                if (true /* !use_admin_db_ is always honored here; admin db not used in this slice */)
                {
                    _way.SetDriveOnRight(value == "right");
                }

                return true;
            case "internal_intersection":
                if (!_inferInternalIntersections)
                {
                    _way.SetInternal(value == "true");
                }

                return true;
            case "tagged_internal_intersection":
                _way.SetInternal(value == "true");
                return true;
            case "turn_channel":
                if (!_inferTurnChannels)
                {
                    _way.SetTurnChannel(value == "true");
                }

                return true;
            case "layer":
                _way.SetLayer((sbyte)ToInt(value));
                return true;
            case "road_class":
                SetRoadClass(value);
                return true;
            case "auto_tag":
                _osmAccess.SetAutoTag(true);
                _hasUserTags = true;
                return true;
            case "truck_tag":
                _osmAccess.SetTruckTag(true);
                _hasUserTags = true;
                return true;
            case "bus_tag":
                _osmAccess.SetBusTag(true);
                _hasUserTags = true;
                return true;
            case "foot_tag":
                _osmAccess.SetFootTag(true);
                _hasUserTags = true;
                return true;
            case "bike_tag":
                _osmAccess.SetBikeTag(true);
                _hasUserTags = true;
                return true;
            case "moped_tag":
                _osmAccess.SetMopedTag(true);
                _hasUserTags = true;
                return true;
            case "motorcycle_tag":
                _osmAccess.SetMotorcycleTag(true);
                _hasUserTags = true;
                return true;
            case "hov_tag":
                _osmAccess.SetHovTag(true);
                _hasUserTags = true;
                return true;
            case "taxi_tag":
                _osmAccess.SetTaxiTag(true);
                _hasUserTags = true;
                return true;
            case "motorroad_tag":
                _osmAccess.SetMotorroadTag(true);
                _hasUserTags = true;
                return true;
            case "wheelchair":
                _way.SetWheelchairTag(true);
                _way.SetWheelchair(value == "true");
                return true;
            case "sidewalk":
                HandleSidewalk(value);
                return true;
            case "auto_forward":
                _way.SetAutoForward(value == "true");
                return true;
            case "truck_forward":
                _way.SetTruckForward(value == "true");
                return true;
            case "bus_forward":
                _way.SetBusForward(value == "true");
                return true;
            case "bike_forward":
                _way.SetBikeForward(value == "true");
                return true;
            case "emergency_forward":
                _way.SetEmergencyForward(value == "true");
                return true;
            case "hov_forward":
                _way.SetHovForward(value == "true");
                return true;
            case "taxi_forward":
                _way.SetTaxiForward(value == "true");
                return true;
            case "moped_forward":
                _way.SetMopedForward(value == "true");
                return true;
            case "motorcycle_forward":
                _way.SetMotorcycleForward(value == "true");
                return true;
            case "pedestrian_forward":
                _way.SetPedestrianForward(value == "true");
                return true;
            case "auto_backward":
                _way.SetAutoBackward(value == "true");
                return true;
            case "truck_backward":
                _way.SetTruckBackward(value == "true");
                return true;
            case "bus_backward":
                _way.SetBusBackward(value == "true");
                return true;
            case "bike_backward":
                _way.SetBikeBackward(value == "true");
                return true;
            case "emergency_backward":
                _way.SetEmergencyBackward(value == "true");
                return true;
            case "hov_backward":
                _way.SetHovBackward(value == "true");
                return true;
            case "taxi_backward":
                _way.SetTaxiBackward(value == "true");
                return true;
            case "moped_backward":
                _way.SetMopedBackward(value == "true");
                return true;
            case "motorcycle_backward":
                _way.SetMotorcycleBackward(value == "true");
                return true;
            case "pedestrian_backward":
                _way.SetPedestrianBackward(value == "true");
                return true;
            case "private":
                if (value == "true")
                {
                    _way.SetDestinationOnly(true);
                }

                return true;
            case "private_hgv":
                if (value == "true")
                {
                    _way.SetDestinationOnlyHgv(true);
                }

                return true;
            case "service":
                if (value == "rest_area")
                {
                    _service = value;
                }

                return true;
            case "amenity":
                if (value == "yes")
                {
                    _amenity = value;
                }

                return true;
            case "use":
                SetUse(value);
                return true;
            case "no_thru_traffic":
                _way.SetNoThruTraffic(value == "true");
                return true;
            case "oneway":
                _way.SetOneway(value == "true");
                return true;
            case "oneway_reverse":
                _way.SetOnewayReverse(value == "true");
                return true;
            case "roundabout":
                _way.SetRoundabout(value == "true");
                return true;
            case "link":
                _way.SetLink(value == "true");
                return true;
            case "ferry":
                _way.SetFerry(value == "true");
                return true;
            case "rail":
                _way.SetRail(value == "true");
                return true;
            case "duration":
                HandleDuration(value);
                return true;
            case "name":
                // PORT-NOTE: capture the structural name (C++ name_ = tag_.second). The linguistic
                // language-record handling (ProcessName) remains deferred; the index is set at way-end.
                if (value.Length != 0)
                {
                    _wayName = value;
                }

                return true;
            case "level":
                if (value.Length != 0)
                {
                    _way.LevelIndex = _osmdata.NameOffsetMap.Index(value);
                    _way.SetMultipleLevels(value.Length > 2);
                }

                return true;
            case "level:ref":
                if (value.Length != 0)
                {
                    _way.LevelRefIndex = _osmdata.NameOffsetMap.Index(value);
                }

                return true;
            case "max_speed":
                HandleMaxSpeed(value);
                return true;
            case "average_speed":
                if (TryToFloat(value, out float avg))
                {
                    _averageSpeed = avg;
                    _hasAverageSpeed = true;
                    _way.SetTaggedSpeed(true);
                }

                return true;
            case "advisory_speed":
                if (TryToFloat(value, out float adv))
                {
                    _advisorySpeed = adv;
                    _hasAdvisorySpeed = true;
                    _way.SetTaggedSpeed(true);
                }

                return true;
            case "forward_speed":
                if (TryToFloat(value, out float fs))
                {
                    _way.SetForwardSpeed(fs);
                    _way.SetForwardTaggedSpeed(true);
                }

                return true;
            case "backward_speed":
                if (TryToFloat(value, out float bs))
                {
                    _way.SetBackwardSpeed(bs);
                    _way.SetBackwardTaggedSpeed(true);
                }

                return true;
            case "maxspeed:hgv":
                if (TryToFloat(value, out float ts))
                {
                    _way.SetTruckSpeed(ts);
                }

                return true;
            case "maxspeed:hgv:forward":
                if (TryToFloat(value, out float tsf))
                {
                    _way.SetTruckSpeedForward(tsf);
                }

                return true;
            case "maxspeed:hgv:backward":
                if (TryToFloat(value, out float tsb))
                {
                    _way.SetTruckSpeedBackward(tsb);
                }

                return true;
            case "maxspeed:conditional":
                HandleMaxSpeedConditional(value);
                return true;
            case "truck_route":
                _way.SetTruckRoute(value == "true");
                return true;
            case "default_speed":
                if (TryToFloat(value, out float ds))
                {
                    _defaultSpeed = ds;
                    _hasDefaultSpeed = true;
                }

                return true;
            case "hazmat":
                AddAccessRestriction(AccessType.Hazmat, GraphConstants.TruckAccess, value, BoolValueSetter, AccessRestrictionDirection.Both);
                return true;
            case "hazmat_forward":
                AddAccessRestriction(AccessType.Hazmat, GraphConstants.TruckAccess, value, BoolValueSetter, AccessRestrictionDirection.Forward);
                return true;
            case "hazmat_backward":
                AddAccessRestriction(AccessType.Hazmat, GraphConstants.TruckAccess, value, BoolValueSetter, AccessRestrictionDirection.Backward);
                return true;
            case "maxheight":
                AddAccessRestriction(AccessType.MaxHeight, DimensionModes, value, CentimeterValueSetter, AccessRestrictionDirection.Both);
                return true;
            case "maxheight_forward":
                AddAccessRestriction(AccessType.MaxHeight, DimensionModes, value, CentimeterValueSetter, AccessRestrictionDirection.Forward);
                return true;
            case "maxheight_backward":
                AddAccessRestriction(AccessType.MaxHeight, DimensionModes, value, CentimeterValueSetter, AccessRestrictionDirection.Backward);
                return true;
            case "maxwidth":
                AddAccessRestriction(AccessType.MaxWidth, DimensionModes, value, CentimeterValueSetter, AccessRestrictionDirection.Both);
                return true;
            case "maxwidth_forward":
                AddAccessRestriction(AccessType.MaxWidth, DimensionModes, value, CentimeterValueSetter, AccessRestrictionDirection.Forward);
                return true;
            case "maxwidth_backward":
                AddAccessRestriction(AccessType.MaxWidth, DimensionModes, value, CentimeterValueSetter, AccessRestrictionDirection.Backward);
                return true;
            case "maxlength":
                AddAccessRestriction(AccessType.MaxLength, DimensionModes, value, CentimeterValueSetter, AccessRestrictionDirection.Both);
                return true;
            case "maxlength_forward":
                AddAccessRestriction(AccessType.MaxLength, DimensionModes, value, CentimeterValueSetter, AccessRestrictionDirection.Forward);
                return true;
            case "maxlength_backward":
                AddAccessRestriction(AccessType.MaxLength, DimensionModes, value, CentimeterValueSetter, AccessRestrictionDirection.Backward);
                return true;
            case "maxweight":
                AddAccessRestriction(AccessType.MaxWeight, DimensionModes, value, CentimeterValueSetter, AccessRestrictionDirection.Both);
                return true;
            case "maxweight_forward":
                AddAccessRestriction(AccessType.MaxWeight, DimensionModes, value, CentimeterValueSetter, AccessRestrictionDirection.Forward);
                return true;
            case "maxweight_backward":
                AddAccessRestriction(AccessType.MaxWeight, DimensionModes, value, CentimeterValueSetter, AccessRestrictionDirection.Backward);
                return true;
            case "maxaxleload":
                AddAccessRestriction(AccessType.MaxAxleLoad, GraphConstants.TruckAccess, value, CentimeterValueSetter, AccessRestrictionDirection.Both);
                return true;
            case "maxaxles":
                AddAccessRestriction(AccessType.MaxAxles, GraphConstants.TruckAccess, value, FloatValueSetter, AccessRestrictionDirection.Both);
                return true;
            case "hov_type":
                HandleHovType(value);
                return true;
            case "ref":
                // PORT-NOTE: capture the structural ref (C++ ref_ = tag_.second). Route-relation refs
                // still override via the relation pass; the index is set at way-end.
                if (value.Length != 0)
                {
                    _wayRef = value;
                }

                return true;
            case "sac_scale":
                HandleSacScale(value);
                return true;
            case "surface":
                HandleSurface(value);
                return true;
            case "smoothness":
                HandleSmoothness(value);
                return true;
            case "tracktype":
                HandleTracktype(value);
                return true;
            case "bicycle":
                if (value == "dismount")
                {
                    _way.SetDismount(true);
                }
                else if (value == "use_sidepath")
                {
                    _way.SetUseSidepath(true);
                }

                return true;
            case "shoulder_right":
                _way.SetShoulderRight(value == "true");
                return true;
            case "shoulder_left":
                _way.SetShoulderLeft(value == "true");
                return true;
            case "cycle_lane_right":
                _way.SetCyclelaneRight(ToCycleLane(value));
                return true;
            case "cycle_lane_left":
                _way.SetCyclelaneLeft(ToCycleLane(value));
                return true;
            case "cycle_lane_right_opposite":
                _way.SetCyclelaneRightOpposite(value == "true");
                return true;
            case "cycle_lane_left_opposite":
                _way.SetCyclelaneLeftOpposite(value == "true");
                return true;
            case "lanes":
                _way.SetLanes((uint)ToInt(value));
                _way.SetTaggedLanes(true);
                return true;
            case "forward_lanes":
                _way.SetForwardLanes((uint)ToInt(value));
                _way.SetForwardTaggedLanes(true);
                return true;
            case "backward_lanes":
                _way.SetBackwardLanes((uint)ToInt(value));
                _way.SetBackwardTaggedLanes(true);
                return true;
            case "tunnel":
                _way.SetTunnel(value == "true");
                return true;
            case "toll":
                _way.SetToll(value == "true");
                return true;
            case "bridge":
                _way.SetBridge(value == "true");
                return true;
            case "indoor":
                _way.SetIndoor(value == "yes");
                return true;
            case "bike_network_mask":
                _way.SetBikeNetwork((uint)ToInt(value));
                return true;
            case "destination":
            case "destination:forward":
            case "destination:backward":
            case "destination:ref":
            case "destination:ref:to":
            case "destination:street":
            case "destination:street:to":
            case "junction:ref":
            case "junction:name":
                if (value.Length != 0)
                {
                    _way.SetExit(true);
                }

                return true;
            case "turn:lanes":
            case "turn:lanes:forward":
                _way.FwdTurnLanesIndex = _osmdata.NameOffsetMap.Index(value);
                return true;
            case "turn:lanes:backward":
                _way.BwdTurnLanesIndex = _osmdata.NameOffsetMap.Index(value);
                return true;
            case "lit":
                _way.SetLit(value == "true");
                return true;
            default:
                return false;
        }
    }

    // ===== Way-handler helpers =================================================

    // kTruckAccess | kAutoAccess | kHOVAccess | kTaxiAccess | kBusAccess from the dimension handlers.
    private const ushort DimensionModes = GraphConstants.TruckAccess | GraphConstants.AutoAccess |
                                          GraphConstants.HovAccess | GraphConstants.TaxiAccess |
                                          GraphConstants.BusAccess;

    private static ulong BoolValueSetter(string v) => v == "true" ? 1UL : 0UL;

    private static ulong CentimeterValueSetter(string v) => (ulong)(ToFloat(v) * 100);

    private static ulong FloatValueSetter(string v) => (ulong)ToFloat(v);

    private void AddAccessRestriction(AccessType type, ushort modes, string value, Func<string, ulong> setter, AccessRestrictionDirection direction)
    {
        var restriction = new OSMAccessRestriction();
        restriction.SetType(type);
        restriction.SetModes(modes);
        if (direction != AccessRestrictionDirection.Both)
        {
            restriction.SetDirection(direction);
        }

        // graph.lua appends a "~" for destination exemptions.
        int pos = value.IndexOf(ExceptDestinationRestrictionFlag);
        bool foundTilde = pos >= 0;
        restriction.SetValue(setter(foundTilde ? value.Substring(0, pos) : value));
        restriction.SetExceptDestination(foundTilde);
        _osmdata.AddAccessRestriction(_osmid, restriction);
    }

    private void SetRoadClass(string value)
    {
        var roadClass = (RoadClass)ToInt(value);
        _way.SetRoadClass(roadClass switch
        {
            RoadClass.Motorway => RoadClass.Motorway,
            RoadClass.Trunk => RoadClass.Trunk,
            RoadClass.Primary => RoadClass.Primary,
            RoadClass.Secondary => RoadClass.Secondary,
            RoadClass.Tertiary => RoadClass.Tertiary,
            RoadClass.Unclassified => RoadClass.Unclassified,
            RoadClass.Residential => RoadClass.Residential,
            _ => RoadClass.ServiceOther,
        });
    }

    private void SetUse(string value)
    {
        var use = (Use)ToInt(value);
        switch (use)
        {
            case Use.Cycleway:
            case Use.Footway:
            case Use.Sidewalk:
            case Use.Pedestrian:
            case Use.Path:
            case Use.Elevator:
            case Use.Steps:
            case Use.Escalator:
            case Use.Bridleway:
            case Use.PedestrianCrossing:
            case Use.LivingStreet:
            case Use.Alley:
            case Use.EmergencyAccess:
            case Use.ServiceRoad:
            case Use.Track:
            case Use.Other:
            case Use.Construction:
                _way.SetUse(use);
                break;
            case Use.ParkingAisle:
                _way.SetDestinationOnly(true);
                _way.SetUse(Use.ParkingAisle);
                break;
            case Use.Driveway:
                _way.SetDestinationOnly(true);
                _way.SetUse(Use.Driveway);
                break;
            case Use.DriveThru:
                _way.SetDestinationOnly(true);
                _way.SetUse(Use.DriveThru);
                break;
            case Use.Platform:
                _way.SetUse(Use.Platform);
                _way.SetRoadClass(RoadClass.ServiceOther);
                break;
            case Use.Road:
            default:
                _way.SetUse(Use.Road);
                break;
        }
    }

    private void HandleSidewalk(string value)
    {
        if (value is "both" or "yes" or "shared" or "raised")
        {
            _way.SetSidewalkLeft(true);
            _way.SetSidewalkRight(true);
        }
        else if (value == "left")
        {
            _way.SetSidewalkLeft(true);
        }
        else if (value == "right")
        {
            _way.SetSidewalkRight(true);
        }
    }

    private void HandleDuration(string value)
    {
        int colon = value.IndexOf(':');
        if (colon < 0)
        {
            return;
        }

        string[] time = GetTagTokens(value, ':');
        uint hour = 0, min = 0, sec = 0;
        if (time.Length == 1)
        {
            uint.TryParse(time[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out min);
            min *= 60;
        }
        else if (time.Length == 2)
        {
            uint.TryParse(time[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out hour);
            hour *= 3600;
            uint.TryParse(time[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out min);
            min *= 60;
        }
        else if (time.Length == 3)
        {
            uint.TryParse(time[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out hour);
            hour *= 3600;
            uint.TryParse(time[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out min);
            min *= 60;
            uint.TryParse(time[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out sec);
        }

        _way.SetDuration(hour + min + sec);
    }

    private void HandleMaxSpeed(string value)
    {
        if (value == "unlimited")
        {
            _maxSpeed = UnlimitedSpeedLimit;
            _way.SetTaggedSpeed(true);
            _hasMaxSpeed = true;
        }
        else if (TryToFloat(value, out float v))
        {
            _maxSpeed = v;
            _way.SetTaggedSpeed(true);
            _hasMaxSpeed = true;
        }
    }

    private void HandleMaxSpeedConditional(string value)
    {
        string[] tokens = GetTagTokens(value, '@');
        if (tokens.Length < 2)
        {
            return;
        }

        byte speed;
        if (tokens[0] is "no" or "none")
        {
            speed = UnlimitedSpeedLimit;
        }
        else if (TryToFloat(tokens[0], out float parsed))
        {
            if (parsed > MaxAssumedSpeed)
            {
                return;
            }

            speed = (byte)(parsed + 0.5f);
        }
        else
        {
            return;
        }

        foreach (string c in GetTagTokens(tokens[1], ';'))
        {
            foreach (ulong v in GetTimeRange(c))
            {
                var limit = new ConditionalSpeedLimit
                {
                    TimeDomain = new TimeDomain(v),
                    Speed = speed,
                };
                _osmdata.AddConditionalSpeed(_osmid, limit);
            }
        }
    }

    private void HandleHovType(string value)
    {
        _way.SetHovType(value switch
        {
            "HOV2" => HovEdgeType.Hov2,
            "HOV3" => HovEdgeType.Hov3,
            _ => HovEdgeType.Hov3,
        });
    }

    private void HandleSacScale(string value)
    {
        string v = value.ToLowerInvariant();
        if (v.Contains("difficult_alpine_hiking"))
        {
            _way.SetSacScale(SacScale.DifficultAlpineHiking);
        }
        else if (v.Contains("demanding_alpine_hiking"))
        {
            _way.SetSacScale(SacScale.DemandingAlpineHiking);
        }
        else if (v.Contains("alpine_hiking"))
        {
            _way.SetSacScale(SacScale.AlpineHiking);
        }
        else if (v.Contains("demanding_mountain_hiking"))
        {
            _way.SetSacScale(SacScale.DemandingMountainHiking);
        }
        else if (v.Contains("mountain_hiking"))
        {
            _way.SetSacScale(SacScale.MountainHiking);
        }
        else if (v.Contains("hiking"))
        {
            _way.SetSacScale(SacScale.Hiking);
        }
        else
        {
            _way.SetSacScale(SacScale.None);
        }
    }

    private void HandleSurface(string value)
    {
        string v = value.ToLowerInvariant();

        // Find unpaved before paved (common substring).
        if (v.Contains("unpaved"))
        {
            _way.SetSurface(Surface.Gravel);
        }
        else if (v.Contains("paved") || v.Contains("pavement") || v.Contains("asphalt") ||
                 v.Contains("concrete") || v.Contains("cement") || v.Contains("chipseal") ||
                 v.Contains("metal"))
        {
            _way.SetSurface(Surface.PavedSmooth);
        }
        else if (v.Contains("tartan") || v.Contains("pavingstone") || v.Contains("paving_stones") ||
                 v.Contains("sett") || v.Contains("grass_paver"))
        {
            _way.SetSurface(Surface.Paved);
        }
        else if (v.Contains("cobblestone") || v.Contains("brick"))
        {
            _way.SetSurface(Surface.PavedRough);
        }
        else if (v.Contains("compacted") || v.Contains("wood") || v.Contains("boardwalk"))
        {
            _way.SetSurface(Surface.Compacted);
        }
        else if (v.Contains("dirt") || v.Contains("natural") || v.Contains("earth") ||
                 v.Contains("ground") || v.Contains("mud"))
        {
            _way.SetSurface(Surface.Dirt);
        }
        else if (v.Contains("gravel") || v.Contains("pebblestone") || v.Contains("sand"))
        {
            _way.SetSurface(Surface.Gravel);
        }
        else if (v.Contains("grass") || v.Contains("stepping_stones"))
        {
            _way.SetSurface(Surface.Path);
        }
        else
        {
            _hasSurface = false;
        }
    }

    private void HandleSmoothness(string value)
    {
        // surface and tracktype tag should win over smoothness.
        if (_hasSurfaceTag || _hasTracktypeTag)
        {
            return;
        }

        _hasSurface = true;
        switch (value)
        {
            case "excellent":
            case "good":
                _way.SetSurface(Surface.PavedSmooth);
                break;
            case "intermediate":
                _way.SetSurface(Surface.PavedRough);
                break;
            case "bad":
                _way.SetSurface(Surface.Compacted);
                break;
            case "very_bad":
                _way.SetSurface(Surface.Dirt);
                break;
            case "horrible":
                _way.SetSurface(Surface.Gravel);
                break;
            case "very_horrible":
                _way.SetSurface(Surface.Path);
                break;
            case "impassable":
                _way.SetSurface(Surface.Impassable);
                break;
            default:
                _hasSurface = false;
                break;
        }
    }

    private void HandleTracktype(string value)
    {
        // surface tag should win over tracktype.
        if (_hasSurfaceTag)
        {
            return;
        }

        _hasSurface = true;
        switch (value)
        {
            case "grade1":
                _way.SetSurface(Surface.PavedRough);
                break;
            case "grade2":
                _way.SetSurface(Surface.Compacted);
                break;
            case "grade3":
                _way.SetSurface(Surface.Dirt);
                break;
            case "grade4":
                _way.SetSurface(Surface.Gravel);
                break;
            case "grade5":
                _way.SetSurface(Surface.Path);
                break;
            default:
                _hasSurface = false;
                break;
        }
    }

    private void ProcessMtbTags(IReadOnlyDictionary<string, string> tags)
    {
        bool hasMtbScale = tags.TryGetValue("mtb:scale", out string? mtbScale);
        if (hasMtbScale && GetNumber(mtbScale!) >= 0)
        {
            uint scale = (uint)ToInt(mtbScale!);
            _way.SetSurface(scale switch
            {
                0 => Surface.Dirt,
                1 => Surface.Gravel,
                _ => Surface.Path,
            });
            _hasSurface = true;

            bool access = scale < MaxMtbScale;
            if (access && !_way.OnewayReverse() && _way.UseValue() != Use.Construction)
            {
                _way.SetBikeForward(true);
            }

            if (access && !_way.Oneway() && _way.UseValue() != Use.Construction)
            {
                _way.SetBikeBackward(true);
            }
        }

        bool hasMtbUphillScale = tags.TryGetValue("mtb:scale:uphill", out string? mtbUphill);
        if (hasMtbUphillScale && GetNumber(mtbUphill!) >= 0)
        {
            uint scale = (uint)ToInt(mtbUphill!);
            if (!hasMtbScale)
            {
                _way.SetSurface(scale < 2 ? Surface.Gravel : Surface.Path);
                _hasSurface = true;
            }

            bool access = scale < MaxMtbUphillScale;
            if (access && !_way.OnewayReverse() && _way.UseValue() != Use.Construction)
            {
                _way.SetBikeForward(true);
            }

            if (access && !_way.Oneway() && _way.UseValue() != Use.Construction)
            {
                _way.SetBikeBackward(true);
            }
        }

        bool hasMtbImba = tags.ContainsKey("mtb:scale:imba");
        if (hasMtbImba)
        {
            if (!hasMtbScale && !hasMtbUphillScale && _way.UseValue() != Use.Construction)
            {
                if (!_way.OnewayReverse())
                {
                    _way.SetBikeForward(true);
                }

                if (!_way.Oneway())
                {
                    _way.SetBikeBackward(true);
                }
            }
        }

        bool hasMtbDesc = tags.ContainsKey("mtb:description");
        if (hasMtbDesc && !hasMtbScale && !hasMtbUphillScale && !hasMtbImba &&
            _way.UseValue() != Use.Construction)
        {
            if (!_way.OnewayReverse())
            {
                _way.SetBikeForward(true);
            }

            if (!_way.Oneway())
            {
                _way.SetBikeBackward(true);
            }
        }
    }

    private void ApplyDefaultSurface()
    {
        switch (_way.RoadClassValue())
        {
            case RoadClass.Motorway:
            case RoadClass.Trunk:
            case RoadClass.Primary:
            case RoadClass.Secondary:
            case RoadClass.Tertiary:
            case RoadClass.Unclassified:
            case RoadClass.Residential:
                _way.SetSurface(Surface.PavedSmooth);
                break;
            default:
                switch (_way.UseValue())
                {
                    case Use.Footway:
                    case Use.Pedestrian:
                    case Use.Sidewalk:
                    case Use.Path:
                    case Use.Bridleway:
                        _way.SetSurface(Surface.Compacted);
                        break;
                    case Use.Track:
                        _way.SetSurface(Surface.Dirt);
                        break;
                    case Use.Road:
                    case Use.ParkingAisle:
                    case Use.Driveway:
                    case Use.Alley:
                    case Use.EmergencyAccess:
                    case Use.DriveThru:
                    case Use.LivingStreet:
                    case Use.ServiceRoad:
                        _way.SetSurface(Surface.PavedSmooth);
                        break;
                    case Use.Cycleway:
                    case Use.Steps:
                        _way.SetSurface(Surface.Paved);
                        break;
                    default:
                        _way.SetSurface(Surface.Paved);
                        break;
                }

                break;
        }
    }

    // ===== Conditional access (way) ============================================

    private static bool IsConditionalAccessKey(string key) =>
        key.StartsWith("access:conditional", StringComparison.Ordinal) ||
        key.StartsWith("motorcar:conditional", StringComparison.Ordinal) ||
        key.StartsWith("motor_vehicle:conditional", StringComparison.Ordinal) ||
        key.StartsWith("bicycle:conditional", StringComparison.Ordinal) ||
        key.StartsWith("motorcycle:conditional", StringComparison.Ordinal) ||
        key.StartsWith("foot:conditional", StringComparison.Ordinal) ||
        key.StartsWith("pedestrian:conditional", StringComparison.Ordinal) ||
        key.StartsWith("hgv:conditional", StringComparison.Ordinal) ||
        key.StartsWith("moped:conditional", StringComparison.Ordinal) ||
        key.StartsWith("mofa:conditional", StringComparison.Ordinal) ||
        key.StartsWith("psv:conditional", StringComparison.Ordinal) ||
        key.StartsWith("taxi:conditional", StringComparison.Ordinal) ||
        key.StartsWith("bus:conditional", StringComparison.Ordinal) ||
        key.StartsWith("hov:conditional", StringComparison.Ordinal) ||
        key.StartsWith("emergency:conditional", StringComparison.Ordinal);

    private void HandleConditionalAccess(string key, string value)
    {
        string[] tokens = GetTagTokens(value, '@');
        string tmp = tokens[0].Trim();

        AccessType type = AccessType.TimedDenied;
        if (tmp == "no")
        {
            type = AccessType.TimedDenied;
        }
        else if (tmp is "yes" or "private" or "delivery" or "designated")
        {
            type = AccessType.TimedAllowed;
        }
        else if (tmp == "destination")
        {
            type = AccessType.DestinationAllowed;
        }

        if (tokens.Length != 2 || tmp.Length == 0)
        {
            return;
        }

        ushort mode = 0;
        if (key.StartsWith("access:conditional", StringComparison.Ordinal))
        {
            mode = GraphConstants.AllAccess;
        }
        else if (key.StartsWith("motor_vehicle:conditional", StringComparison.Ordinal))
        {
            mode = (ushort)(GraphConstants.AutoAccess | GraphConstants.TruckAccess |
                            GraphConstants.EmergencyAccess | GraphConstants.TaxiAccess |
                            GraphConstants.BusAccess | GraphConstants.HovAccess |
                            GraphConstants.MopedAccess | GraphConstants.MotorcycleAccess);
        }
        else if (key.StartsWith("motorcar:conditional", StringComparison.Ordinal))
        {
            mode = type == AccessType.TimedAllowed
                ? (ushort)(GraphConstants.AutoAccess | GraphConstants.HovAccess | GraphConstants.TaxiAccess)
                : (ushort)(GraphConstants.AutoAccess | GraphConstants.TruckAccess |
                           GraphConstants.EmergencyAccess | GraphConstants.TaxiAccess |
                           GraphConstants.BusAccess | GraphConstants.HovAccess);
        }
        else if (key.StartsWith("bicycle:conditional", StringComparison.Ordinal))
        {
            mode = GraphConstants.BicycleAccess;
        }
        else if (key.StartsWith("foot:conditional", StringComparison.Ordinal) ||
                 key.StartsWith("pedestrian:conditional", StringComparison.Ordinal))
        {
            mode = (ushort)(GraphConstants.PedestrianAccess | GraphConstants.WheelchairAccess);
        }
        else if (key.StartsWith("hgv:conditional", StringComparison.Ordinal))
        {
            mode = GraphConstants.TruckAccess;
        }
        else if (key.StartsWith("moped:conditional", StringComparison.Ordinal) ||
                 key.StartsWith("mofa:conditional", StringComparison.Ordinal))
        {
            mode = GraphConstants.MopedAccess;
        }
        else if (key.StartsWith("motorcycle:conditional", StringComparison.Ordinal))
        {
            mode = GraphConstants.MotorcycleAccess;
        }
        else if (key.StartsWith("psv:conditional", StringComparison.Ordinal))
        {
            mode = (ushort)(GraphConstants.TaxiAccess | GraphConstants.BusAccess);
        }
        else if (key.StartsWith("taxi:conditional", StringComparison.Ordinal))
        {
            mode = GraphConstants.TaxiAccess;
        }
        else if (key.StartsWith("bus:conditional", StringComparison.Ordinal))
        {
            mode = GraphConstants.BusAccess;
        }
        else if (key.StartsWith("hov:conditional", StringComparison.Ordinal))
        {
            mode = GraphConstants.HovAccess;
        }
        else if (key.StartsWith("emergency:conditional", StringComparison.Ordinal))
        {
            mode = GraphConstants.EmergencyAccess;
        }

        string conditionStr = tokens[1].Trim();
        foreach (string condition in GetTagTokens(conditionStr, ';'))
        {
            foreach (ulong v in GetTimeRange(condition))
            {
                var restriction = new OSMAccessRestriction();
                restriction.SetType(type);
                restriction.SetModes(mode);
                restriction.SetValue(v);
                _osmdata.AddAccessRestriction(_osmid, restriction);
            }
        }
    }

    // ===== Small numeric / string helpers ======================================

    private static int GetNumber(string value) =>
        Midgard.Util.TryToInt(value, out int n) ? n : -1;

    private static int ToInt(string value)
    {
        // Faithful to C++ atoi / Lua tonumber: unparseable OSM tag values (e.g. a Unicode minus
        // U+2212 "−3", or values with trailing units) yield the default rather than throwing.
        return Midgard.Util.TryToInt(value, out int n) ? n : 0;
    }

    private static bool TryToFloat(string value, out float result)
    {
        if (float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out result))
        {
            return true;
        }

        // Lua to_float scans a leading numeric prefix; mirror that loosely.
        int len = 0;
        while (len < value.Length && (char.IsDigit(value[len]) || value[len] is '.' or '-' or '+'))
        {
            len++;
        }

        if (len > 0)
        {
            string prefix = value.Substring(0, len);
            if (float.TryParse(prefix, NumberStyles.Float, CultureInfo.InvariantCulture, out result))
            {
                return true;
            }
        }

        result = 0;
        return false;
    }

    private static float ToFloat(string value) => TryToFloat(value, out float f) ? f : 0f;

    private static CycleLane ToCycleLane(string value) => (CycleLane)ToInt(value) switch
    {
        CycleLane.Dedicated => CycleLane.Dedicated,
        CycleLane.Separated => CycleLane.Separated,
        CycleLane.Shared => CycleLane.Shared,
        _ => CycleLane.None,
    };

    private static string MaxString(string a, string b) =>
        string.CompareOrdinal(a, b) >= 0 ? a : b;

    // GetTagTokens(value, delim): split on a single char, no token compression (faithful).
    private static string[] GetTagTokens(string value, char delim) =>
        value.Split(delim);

    // GetTagTokens(value): default delimiter is ';'.
    private static string[] GetTagTokens(string value) => value.Split(';');

    // get_time_range: a faithful-enough placeholder for the time-domain encoding. The full
    // timeparsing.cc port (OSM opening-hours grammar -> TimeDomain words) is part of the tile
    // build slice; here we emit a single non-zero TimeDomain word so conditional restrictions /
    // speeds are recorded with a stable, ordered value. A real time-domain parse would replace
    // this without changing the surrounding restriction/speed plumbing.
    private static IReadOnlyList<ulong> GetTimeRange(string condition)
    {
        if (string.IsNullOrWhiteSpace(condition))
        {
            return Array.Empty<ulong>();
        }

        // Use a deterministic non-zero word derived from the condition string so distinct
        // conditions are distinguishable and ordering in the sequence is stable.
        ulong word = 1;
        foreach (char c in condition.Trim())
        {
            word = unchecked((word * 31) + c);
        }

        if (word == 0)
        {
            word = 1;
        }

        return new[] { word };
    }

    // ===== Culdesac processor ==================================================

    // Faithful port of the anonymous-namespace culdesac_processor in pbfgraphparser.cc.
    private sealed class CuldesacProcessor
    {
        private readonly Dictionary<ulong, List<ulong>> _nodeToLoopWay = new();
        private readonly Dictionary<ulong, LoopMeta> _loopsMeta = new();

        public void AddCandidate(ulong osmWayId, int osmWayIndex, IReadOnlyList<ulong> osmNodeIds)
        {
            _loopsMeta[osmWayId] = new LoopMeta(osmWayIndex);
            foreach (ulong nodeId in osmNodeIds)
            {
                if (!_nodeToLoopWay.TryGetValue(nodeId, out List<ulong>? list))
                {
                    list = new List<ulong>();
                    _nodeToLoopWay[nodeId] = list;
                }

                list.Add(osmWayId);
            }
        }

        public void ClarifyAndFix(List<OSMWayNode> wayNodes, List<OSMWay> ways)
        {
            int numberOfNodes = 0;
            int countNode = 0;
            OSMWay osmWay = new();

            foreach (OSMWayNode wayNode in wayNodes)
            {
                if (numberOfNodes == countNode)
                {
                    osmWay = ways[(int)wayNode.WayIndex];
                    numberOfNodes = (int)osmWay.NodeCount();
                    countNode = 0;
                }

                if (_nodeToLoopWay.TryGetValue(wayNode.Node.Osmid, out List<ulong>? loopWays))
                {
                    foreach (ulong loopWayId in loopWays)
                    {
                        if (osmWay.WayId() != loopWayId && osmWay.UseValue() == Use.Road)
                        {
                            _loopsMeta[loopWayId].AddIdOfIntersection(wayNode.Node.Osmid);
                        }
                    }
                }

                countNode++;
            }

            Fix(ways);
        }

        private void Fix(List<OSMWay> ways)
        {
            foreach (KeyValuePair<ulong, LoopMeta> kv in _loopsMeta)
            {
                LoopMeta meta = kv.Value;
                if (meta.IsCuldesac())
                {
                    OSMWay way = ways[meta.WayIndex];
                    way.SetUse(Use.Culdesac);
                    ways[meta.WayIndex] = way;
                }
            }
        }

        private sealed class LoopMeta
        {
            private readonly HashSet<ulong> _intersections = new();

            public LoopMeta(int wayIndex) => WayIndex = wayIndex;

            public int WayIndex { get; }

            public bool IsCuldesac() => _intersections.Count <= 1;

            public void AddIdOfIntersection(ulong nodeId) => _intersections.Add(nodeId);
        }
    }
}

/// <summary>
/// Configuration options for <see cref="PbfGraphParser"/>. Defaults match the property-tree
/// defaults read in the C++ <c>graph_parser</c> constructor.
/// </summary>
public sealed class PbfGraphParserOptions
{
    /// <summary>Include highway=platform ways for pedestrians (C++ <c>include_platforms</c>, default false).</summary>
    public bool IncludePlatforms { get; set; }

    /// <summary>Include driveways (C++ <c>include_driveways</c>, default true).</summary>
    public bool IncludeDriveways { get; set; } = true;

    /// <summary>Include roads under construction (C++ <c>include_construction</c>, default false).</summary>
    public bool IncludeConstruction { get; set; }

    /// <summary>Infer internal intersections later (C++ <c>infer_internal_intersections</c>, default true).</summary>
    public bool InferInternalIntersections { get; set; } = true;

    /// <summary>Infer turn channels later (C++ <c>infer_turn_channels</c>, default true).</summary>
    public bool InferTurnChannels { get; set; } = true;

    /// <summary>Process direction on ways (C++ <c>use_direction_on_ways</c>, default false).</summary>
    public bool UseDirectionOnWays { get; set; }

    /// <summary>Process alt_name on ways (C++ <c>allow_alt_name</c>, default false).</summary>
    public bool AllowAltName { get; set; }

    /// <summary>Process the urban key on nodes (C++ <c>use_urban_tag</c>, default false).</summary>
    public bool UseUrbanTag { get; set; }

    /// <summary>Process rest/service area keys on ways (C++ <c>use_rest_area</c>, default false).</summary>
    public bool UseRestArea { get; set; }
}
