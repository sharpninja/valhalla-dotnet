# Functional Requirements (MCP Server)

## FR-VALHALLA-002 Test SDK stays on a supported, currency-checked version

The test build pipeline uses a current, compatible Microsoft.NET.Test.Sdk version, verified against the live NuGet registry rather than assumed.
Scope: layer-1+

## FR-VALHALLA-003 Test suite runs on xUnit v3

The test framework is xUnit v3 (package id xunit.v3), not the frozen xUnit v2 line (package id xunit, stopped at 2.9.3).
Scope: layer-1+

## FR-VALHALLA-004 Zero-warning build enforced workspace-wide

All three projects (src, tests, build) treat compiler and analyzer warnings as build errors, and the workspace builds with zero warnings.
Scope: layer-1+

## FR-VALHALLA-005 Engine routing primitives and route response parsing

SharpNinja.Valhalla must own the reusable Valhalla engine routing primitives that TruckMate previously carried, including precision-6 route shape decoding, embedded/HTTP-compatible route result shaping, maneuvers, alternates, multi-leg shape-index handling, fractional duration parsing, and route-point de-duplication.
Scope: layer-1+
**Acceptance Criteria:**
- [x] Precision-6 Valhalla encoded polylines decode to deterministic latitude/longitude coordinates, null or empty input returns an empty sequence, and truncated malformed input fails deterministically. (evidence: tests/SharpNinja.Valhalla.Tests/ValhallaPolylineDecoderTests.cs; TestResults/valhalla-polyline-decoder-migration-green.trx)
- [x] Embedded Valhalla route results expose route distance, duration, encoded geometry, maneuvers/instructions, and alternates through package-neutral engine contracts. (evidence: tests/SharpNinja.Valhalla.Tests/Osm/EmbeddedValhallaRoutingClientAlternatesTests.cs; tests/SharpNinja.Valhalla.Tests/Osm/EmbeddedValhallaRoutingClientInstructionTests.cs)
- [x] Multi-leg route shapes, maneuver shape indices, fractional durations, and close-point de-duplication are covered in engine tests rather than TruckMate tests. (evidence: tests/SharpNinja.Valhalla.Tests/Thor/TripLegBuilderTests.cs; tests/SharpNinja.Valhalla.Tests/Thor/RouteAlternatesTests.cs; tests/SharpNinja.Valhalla.Tests/Odin/DirectionsBuilderTests.cs)

## FR-VALHALLA-006 Custom Valhalla costing and unprotected-left avoidance

SharpNinja.Valhalla must own custom engine costing options for unprotected-left avoidance and static-friction control. The engine must parse and apply unprotected_left_avoidance_meters and enable_static_friction without depending on TruckMate or Avalonia code.
Scope: layer-1+
**Acceptance Criteria:**
- [x] Truck and auto costing parse and preserve unprotected_left_avoidance_meters with deterministic default/range behavior. (evidence: tests/SharpNinja.Valhalla.Tests/Sif/TruckCostTests.cs; tests/SharpNinja.Valhalla.Tests/Sif/AutoCostTests.cs)
- [x] Truck costing parses enable_static_friction and uses it to disable or enable static comfort-friction penalties independently from hard unprotected-left avoidance. (evidence: tests/SharpNinja.Valhalla.Tests/Sif/TruckCostTests.cs)
- [x] Unprotected-left avoidance is implemented as a finite distance-sized penalty, not an absolute ban, and applies through engine transition costing. (evidence: tests/SharpNinja.Valhalla.Tests/Sif/TruckCostTests.cs; tests/SharpNinja.Valhalla.Tests/Sif/AutoCostTests.cs; tests/SharpNinja.Valhalla.Tests/Nashville/NashvilleEngineRouteTests.cs)
- [x] Route request serialization emits the custom costing fields only through SharpNinja.Valhalla engine/client contracts, not through TruckMate-owned engine code. (evidence: src/SharpNinja.Valhalla/EmbeddedValhallaRoutingClient.cs; tests/SharpNinja.Valhalla.Tests/Osm/EmbeddedValhallaRoutingClientAlternatesTests.cs)

## FR-VALHALLA-007 Control-device data fidelity for left-turn costing

SharpNinja.Valhalla must own the OSM/tile data fidelity needed by engine left-turn costing, including signal/stop/yield node interpretation, conservative fallback when control-device data is incomplete, and real-route validation for the Nashville unprotected-left scenario.
Scope: layer-1+
**Acceptance Criteria:**
- [x] OSM node/tag transform tests cover traffic-signal and control-device tags used by left-turn protection decisions. (evidence: tests/SharpNinja.Valhalla.Tests/Mjolnir/NodeTagTransformTests.cs; tests/SharpNinja.Valhalla.Tests/Mjolnir/PbfGraphParserTests.cs)
- [x] The costing implementation treats untagged or uncertain conflicting approaches conservatively, so missing control-device data does not wrongly permit a dangerous unprotected left. (evidence: src/SharpNinja.Valhalla/Sif/TruckCost.cs; src/SharpNinja.Valhalla/Sif/AutoCost.cs; tests/SharpNinja.Valhalla.Tests/Sif/TruckCostTests.cs)
- [x] Real Nashville route validation records the protected/signalized left-turn analysis and the data-fidelity finding for the West End/Lyle scenario. (evidence: tests/SharpNinja.Valhalla.Tests/Nashville/NashvilleEngineRouteTests.cs; tests/SharpNinja.Valhalla.Tests/TestResults/nashville-migration-postcommit-candidate.trx)

