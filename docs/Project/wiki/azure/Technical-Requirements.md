# Technical Requirements (MCP Server)

## TR-VALHALLA-BUILD-002

**Pin Microsoft.NET.Test.Sdk to 18.7.0 in Directory.Packages.props** — Microsoft.NET.Test.Sdk moves from 17.14.1 to 18.7.0 (highest published stable version, confirmed same vstest-based dependency shape, no conflicting floor from xunit.runner.visualstudio for net8.0+). Byrd gate: 100 percent pass, zero failed, zero skipped on the full test suite post-upgrade, both dotnet test and the Nuke Test target.
**Covered by:** FR: FR-VALHALLA-002; TEST: TEST-VALHALLA-002
**Status:** completed
Scope: layer-1+

## TR-VALHALLA-BUILD-004

**Add Directory.Build.props with TreatWarningsAsErrors, remediate all 21 pre-existing warnings** — New Directory.Build.props sets TreatWarningsAsErrors=true for all projects. Remediated: 10 nullable/CS warnings in src (Loki/BinHandler.cs Finalize rename + oppTile null-forgive, Mjolnir/GraphBuilder.cs + HierarchyBuilder.cs nullable PointLL, Thor/BidirectionalAStar.cs oppEdge/oppTile null-forgive, Odin/NarrativeBuilder.cs CS0162 pragma-scoped to the intentionally-dead option_roundabout_exits=false branch), 6 test warnings (xUnit2000 arg swap, CA2014 stackalloc hoisted out of loop, 4x xUnit1051 TestContext.Current.CancellationToken), 2 CS warnings in build/Build.cs (nullable NugetApiKey/PackageVersion fields + null-forgiving at guarded use sites), and 3 NU1901/NU1903 NuGet advisories on Nuke.Common transitive deps (NuGet.Packaging, System.Security.Cryptography.Xml) resolved via explicit higher-version PackageReference/PackageVersion overrides.
**Covered by:** FR: FR-VALHALLA-004; TEST: TEST-VALHALLA-004
**Status:** completed
Scope: layer-1+

## TR-VALHALLA-CORE-001

**Precision-6 polyline decoder** — SharpNinja.Valhalla owns the precision-6 Valhalla encoded-polyline decoder and engine route-shape coordinate contract formerly tracked in TruckMate TR-OSMNAV-CORE-001.
**Covered by:** FR: FR-VALHALLA-005; TEST: TEST-VALHALLA-001, TEST-VALHALLA-005
**Status:** completed
Scope: layer-1+

## TR-VALHALLA-COSTING-032

**Custom costing option fields and serialization** — Define, parse, and serialize unprotected_left_avoidance_meters and enable_static_friction in SharpNinja.Valhalla costing/client contracts.
**Covered by:** FR: FR-VALHALLA-006; TEST: TEST-VALHALLA-005, TEST-VALHALLA-006, TEST-VALHALLA-007
**Status:** completed
Scope: layer-1+

## TR-VALHALLA-DATAFIDELITY-035

**Control-device fidelity and conservative fallback** — Engine tile/tag parsing and costing preserve or conservatively handle signal/stop/yield data needed for unprotected-left protection decisions.
**Covered by:** FR: FR-VALHALLA-007; TEST: TEST-VALHALLA-007, TEST-VALHALLA-008
**Status:** completed
Scope: layer-1+

## TR-VALHALLA-FRICTION-034

**Static friction option belongs to engine costing** — Static/comfort friction is controlled by SharpNinja.Valhalla costing through enable_static_friction; UI/package route ranking remains outside engine costing.
**Covered by:** FR: FR-VALHALLA-006; TEST: TEST-VALHALLA-005, TEST-VALHALLA-006, TEST-VALHALLA-007
**Status:** completed
Scope: layer-1+

## TR-VALHALLA-LEFTTURN-033

**Finite unprotected-left detection and penalty** — Engine transition costing detects unprotected left turns and applies a finite distance-sized penalty so the turn is avoided unless the detour exceeds the configured threshold.
**Covered by:** FR: FR-VALHALLA-006, FR-VALHALLA-007; TEST: TEST-VALHALLA-005, TEST-VALHALLA-006, TEST-VALHALLA-007, TEST-VALHALLA-008
**Status:** completed
Scope: layer-1+

## TR-VALHALLA-ROUTING-002

**Embedded Valhalla routing client and route response contract** — SharpNinja.Valhalla owns embedded routing result shaping for /route-compatible distance, duration, geometry, maneuvers, alternates, multi-leg shape-index offset handling, fractional durations, and close-point de-duplication.
**Covered by:** FR: FR-VALHALLA-005; TEST: TEST-VALHALLA-001, TEST-VALHALLA-005
**Status:** completed
Scope: layer-1+

## TR-VALHALLA-TEST-003

**Replace xunit 2.9.3 with xunit.v3 3.2.2** — xunit (2.9.3) replaced by xunit.v3 (3.2.2) in Directory.Packages.props; test project OutputType set to Exe; execution stays on VSTest/dotnet test/.trx (no MTP opt-in via TestingPlatformDotnetTestSupport/UseMicrosoftTestingPlatformRunner/global.json runner override). Byrd gate: 100 percent pass, zero failed, zero skipped on the full suite.
**Covered by:** FR: FR-VALHALLA-003; TEST: TEST-VALHALLA-003
**Status:** completed
Scope: layer-1+

