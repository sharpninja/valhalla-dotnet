# Technical Requirements (MCP Server)

## TR-VALHALLA-ASYNC-040

**Asynchronous UI-agnostic traffic pipeline** — Feed fetch, provider normalization, Valhalla edge matching, graph traffic-control extraction, and tile writing must be asynchronous and UI-agnostic. ITrafficFeedAdapter.NormalizeAsync and ITrafficEdgeMatcher.MatchAsync must expose cancellable ValueTask/Task contracts; synchronous APIs are not an acceptable implementation of this TR.
**Covered by:** FR: FR-VALHALLA-007, FR-VALHALLA-008, FR-VALHALLA-009, FR-VALHALLA-010, FR-VALHALLA-011; TEST: TEST-VALHALLA-007, TEST-VALHALLA-008, TEST-VALHALLA-012, TEST-VALHALLA-009, TEST-VALHALLA-010, TEST-VALHALLA-011
**Status:** completed
Scope: layer-1+

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

## TR-VALHALLA-COMPAT-047

**Traffic API compatibility and ownership** — Runtime traffic additions shall remain source-compatible, expose no Avalonia or Mapsui types, and preserve existing positional construction while adding new DATA interfaces and init-only metadata.
**Covered by:** FR: FR-VALHALLA-012; TEST: TEST-VALHALLA-013
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
**Covered by:** FR: FR-VALHALLA-007; TEST: TEST-VALHALLA-007, TEST-VALHALLA-008, TEST-VALHALLA-012
**Status:** completed
Scope: layer-1+

## TR-VALHALLA-FRICTION-034

**Static friction option belongs to engine costing** — Static/comfort friction is controlled by SharpNinja.Valhalla costing through enable_static_friction; UI/package route ranking remains outside engine costing.
**Covered by:** FR: FR-VALHALLA-006; TEST: TEST-VALHALLA-005, TEST-VALHALLA-006, TEST-VALHALLA-007
**Status:** completed
Scope: layer-1+

## TR-VALHALLA-IDENTITY-043

**Directed-edge route and control identity** — Route comparison and traffic-control matching must use canonical ordered Valhalla directed-edge identities or documented edge-overlap signatures, not labels, candidate indexes, or geometry proximity.
**Covered by:** FR: FR-VALHALLA-007, FR-VALHALLA-010, FR-VALHALLA-011; TEST: TEST-VALHALLA-007, TEST-VALHALLA-008, TEST-VALHALLA-012, TEST-VALHALLA-010, TEST-VALHALLA-011
**Status:** completed
Scope: layer-1+

## TR-VALHALLA-INTEGRATION-041

**Host-supplied traffic HTTP pipeline** — Traffic feed clients must use host-supplied HttpClient or HttpMessageInvoker pipelines so hosts can attach routing, authorization, resilience, and proxy handlers without SharpNinja.Valhalla depending on TruckMate.Gateway.Client or any host application.
**Covered by:** FR: FR-VALHALLA-008; TEST: TEST-VALHALLA-009
**Status:** completed
Scope: layer-1+

## TR-VALHALLA-LEFTTURN-033

**Finite unprotected-left detection and penalty** — Engine transition costing detects unprotected left turns and applies a finite distance-sized penalty so the turn is avoided unless the detour exceeds the configured threshold.
**Covered by:** FR: FR-VALHALLA-006, FR-VALHALLA-007; TEST: TEST-VALHALLA-005, TEST-VALHALLA-006, TEST-VALHALLA-007, TEST-VALHALLA-008, TEST-VALHALLA-012
**Status:** completed
Scope: layer-1+

## TR-VALHALLA-LIFECYCLE-046

**Traffic refresh and route-set lifecycle** — Traffic refresh, last-known retention, closure-only policy, two-pass route planning, thresholded rerouting, cancellation, and publication shall be deterministic, asynchronous, single-flight, and UI-agnostic.
**Covered by:** FR: FR-VALHALLA-012; TEST: TEST-VALHALLA-013
**Status:** completed
Scope: layer-1+

## TR-VALHALLA-PROVENANCE-042

**Traffic provenance and duration application** — Normalized traffic data must retain observation, fetch, and update provenance; snapshots must report explicit per-feed source/availability status; ETA/friction application must record whether base duration already includes traffic so delay is never double-counted.
**Covered by:** FR: FR-VALHALLA-009, FR-VALHALLA-010; TEST: TEST-VALHALLA-010, TEST-VALHALLA-011
**Status:** completed
Scope: layer-1+

## TR-VALHALLA-PROVIDER-037

**Provider adapter registration** — Provider support must be adapter and endpoint driven; future providers must not require factory switch statements.
**Covered by:** FR: FR-VALHALLA-008, FR-VALHALLA-009, FR-VALHALLA-010; TEST: TEST-VALHALLA-009, TEST-VALHALLA-010, TEST-VALHALLA-011
**Status:** completed
Scope: layer-1+

## TR-VALHALLA-ROUTING-002

**Embedded Valhalla routing client and route response contract** — SharpNinja.Valhalla owns embedded routing result shaping for /route-compatible distance, duration, geometry, maneuvers, alternates, multi-leg shape-index offset handling, fractional durations, and close-point de-duplication.
**Covered by:** FR: FR-VALHALLA-005; TEST: TEST-VALHALLA-001, TEST-VALHALLA-005
**Status:** completed
Scope: layer-1+

## TR-VALHALLA-ROUTING-045

**Pinned traffic-aware embedded routing** — Embedded routing shall acquire one asynchronous graph/traffic reader lease per route, use invariant request time for live traffic, preserve engine duration provenance, and expose typed snapshot failures without double-counting delay.
**Covered by:** FR: FR-VALHALLA-012; TEST: TEST-VALHALLA-013
**Status:** completed
Scope: layer-1+

## TR-VALHALLA-SECURITY-038

**Traffic credential secrecy** — API keys, credential query parameters, URI user-info, fragments, credential headers, Authorization values, bearer tokens, and raw secret-bearing exception text must never be logged or persisted in raw payload, normalized event, snapshot, diagnostic, or provenance output. CredentialMode.None must perform no vendor credential lookup or injection.
**Covered by:** FR: FR-VALHALLA-008, FR-VALHALLA-010; TEST: TEST-VALHALLA-009, TEST-VALHALLA-010, TEST-VALHALLA-011
**Status:** completed
Scope: layer-1+

## TR-VALHALLA-TEST-003

**Replace xunit 2.9.3 with xunit.v3 3.2.2** — xunit (2.9.3) replaced by xunit.v3 (3.2.2) in Directory.Packages.props; test project OutputType set to Exe; execution stays on VSTest/dotnet test/.trx (no MTP opt-in via TestingPlatformDotnetTestSupport/UseMicrosoftTestingPlatformRunner/global.json runner override). Byrd gate: 100 percent pass, zero failed, zero skipped on the full suite.
**Covered by:** FR: FR-VALHALLA-003; TEST: TEST-VALHALLA-003
**Status:** completed
Scope: layer-1+

## TR-VALHALLA-TILE-039

**Engine-owned traffic tile mutation** — Concrete traffic tile mutation must live in engine-owned code and be covered by tests before Avalonia consumes it.
**Covered by:** FR: FR-VALHALLA-011; TEST: TEST-VALHALLA-012
**Status:** completed
Scope: layer-1+

## TR-VALHALLA-TRAFFIC-036

**Traffic component separation** — Traffic feed endpoint, client, adapter, normalization, conflict resolution, route modifier projection, edge matching, and tile writing must be separate components.
**Covered by:** FR: FR-VALHALLA-007, FR-VALHALLA-008, FR-VALHALLA-009, FR-VALHALLA-010, FR-VALHALLA-011; TEST: TEST-VALHALLA-007, TEST-VALHALLA-008, TEST-VALHALLA-012, TEST-VALHALLA-009, TEST-VALHALLA-010, TEST-VALHALLA-011
**Status:** completed
Scope: layer-1+

## TR-VALHALLA-TRAFFICRUNTIME-044

**Native-compatible immutable traffic generations** — Traffic tile writing and storage shall be DATA-owned, exact-layout, graph-fingerprint-bound, content-addressed, atomically promoted, lease-pinned, and bounded to three retained completed generations per graph.
**Covered by:** FR: FR-VALHALLA-012; TEST: TEST-VALHALLA-013
**Status:** completed
Scope: layer-1+

