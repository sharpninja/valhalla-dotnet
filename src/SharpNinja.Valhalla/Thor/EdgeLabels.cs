// Thor edge-label aliases (valhalla @ 3.7.0).
//
// The task brief lists "thor's edge labels (edgelabel.h - BDEdgeLabel/EdgeLabel as used by
// bidirectional A*)" under the Thor foundation. In Valhalla there is NO thor/edgelabel.h: the edge
// labels used by the thor path algorithms live in valhalla/sif/edgelabel.h (sif::EdgeLabel,
// sif::BDEdgeLabel, sif::PathEdgeLabel, sif::MMEdgeLabel). The bidirectional A* (thor) uses
// sif::BDEdgeLabel; the base sif::EdgeLabel is used by map-matching and the time-distance matrices.
//
// Those labels are ALREADY PORTED (SharpNinja.Valhalla.Sif.EdgeLabel /
// BDEdgeLabel) and MUST NOT be re-ported. This file simply re-exports them under the Thor namespace
// via `using` aliases so thor code (and the thor tests) can refer to "the thor edge labels" without
// importing the Sif namespace explicitly, mirroring how thor's .cc files `using namespace sif;`.

// EdgeLabel / BDEdgeLabel as consumed by the thor A* path algorithms (defined in sif/edgelabel.h).
global using ThorEdgeLabel = SharpNinja.Valhalla.Sif.EdgeLabel;
global using ThorBdEdgeLabel = SharpNinja.Valhalla.Sif.BDEdgeLabel;
global using ThorPathEdgeLabel = SharpNinja.Valhalla.Sif.PathEdgeLabel;
