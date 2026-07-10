# Technical Requirements (MCP Server)

## TR-VALHALLA-BUILD-002

**Pin Microsoft.NET.Test.Sdk to 18.7.0 in Directory.Packages.props** — Microsoft.NET.Test.Sdk moves from 17.14.1 to 18.7.0 (highest published stable version, confirmed same vstest-based dependency shape, no conflicting floor from xunit.runner.visualstudio for net8.0+). Byrd gate: 100 percent pass, zero failed, zero skipped on the full test suite post-upgrade, both dotnet test and the Nuke Test target.
Scope: layer-1+

## TR-VALHALLA-TEST-003

**Replace xunit 2.9.3 with xunit.v3 3.2.2** — xunit (2.9.3) replaced by xunit.v3 (3.2.2) in Directory.Packages.props; test project OutputType set to Exe; execution stays on VSTest/dotnet test/.trx (no MTP opt-in via TestingPlatformDotnetTestSupport/UseMicrosoftTestingPlatformRunner/global.json runner override). Byrd gate: 100 percent pass, zero failed, zero skipped on the full suite.
Scope: layer-1+

