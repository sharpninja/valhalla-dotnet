# Testing Requirements (MCP Server)

## TEST-VALHALLA

### TEST-VALHALLA-002

Full SharpNinja.Valhalla.Tests suite (976 tests as of 2026-07-10) passes 100 percent, 0 skipped, under Microsoft.NET.Test.Sdk 18.7.0, via dotnet test and via the Nuke Test/Pack targets (validates artifacts/test-results trx output still matches the azure-pipelines.yml PublishTestResults@2 VSTest format).


### TEST-VALHALLA-003

Full SharpNinja.Valhalla.Tests suite (976-test baseline) compiles and passes 100 percent, 0 skipped, under xunit.v3 3.2.2, via both dotnet test and the Nuke Test/Pack targets; trx output still consumable by azure-pipelines.yml's PublishTestResults@2.


### TEST-VALHALLA-004

Clean rebuild of the whole solution (Debug and Release) reports 0 Warning(s) 0 Error(s); dotnet test and the Nuke Test+Pack targets report 976/976 passed, 0 skipped; Pack produces SharpNinja.Valhalla.1.1.0.nupkg successfully.
