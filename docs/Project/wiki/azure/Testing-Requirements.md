# Testing Requirements (MCP Server)

## TEST-VALHALLA

### TEST-VALHALLA-001

xUnit v3 tests prove known precision-6 decoding, null/empty handling, and truncated malformed input behavior.


### TEST-VALHALLA-002

Full SharpNinja.Valhalla.Tests suite (976 tests as of 2026-07-10) passes 100 percent, 0 skipped, under Microsoft.NET.Test.Sdk 18.7.0, via dotnet test and via the Nuke Test/Pack targets (validates artifacts/test-results trx output still matches the azure-pipelines.yml PublishTestResults@2 VSTest format).


### TEST-VALHALLA-003

Full SharpNinja.Valhalla.Tests suite (976-test baseline) compiles and passes 100 percent, 0 skipped, under xunit.v3 3.2.2, via both dotnet test and the Nuke Test/Pack targets; trx output still consumable by azure-pipelines.yml's PublishTestResults@2.


### TEST-VALHALLA-004

Clean rebuild of the whole solution (Debug and Release) reports 0 Warning(s) 0 Error(s); dotnet test and the Nuke Test+Pack targets report 976/976 passed, 0 skipped; Pack produces SharpNinja.Valhalla.1.1.0.nupkg successfully.


### TEST-VALHALLA-005

xUnit v3 tests cover embedded routing alternates, instructions/maneuvers, route shape handling, multi-leg shape indices, fractional durations, and route-point de-duplication.


### TEST-VALHALLA-006

xUnit v3 tests cover unprotected_left_avoidance_meters and enable_static_friction parsing/default/range behavior for engine costers.


### TEST-VALHALLA-007

xUnit v3 tests and Nashville validation cover finite unprotected-left penalties, signal-protected alternatives, and static-friction gating.


### TEST-VALHALLA-008

xUnit v3 tests cover OSM control-device transform/parsing and the route-validation evidence needed by left-turn costing.
