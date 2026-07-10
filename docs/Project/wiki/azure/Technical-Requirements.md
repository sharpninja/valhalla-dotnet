# Technical Requirements (MCP Server)

## TR-VALHALLA-BUILD-002

**Pin Microsoft.NET.Test.Sdk to 18.7.0 in Directory.Packages.props** — Microsoft.NET.Test.Sdk moves from 17.14.1 to 18.7.0 (highest published stable version, confirmed same vstest-based dependency shape, no conflicting floor from xunit.runner.visualstudio for net8.0+). Byrd gate: 100 percent pass, zero failed, zero skipped on the full test suite post-upgrade, both dotnet test and the Nuke Test target.
Scope: layer-1+

## TR-VALHALLA-BUILD-004

**Add Directory.Build.props with TreatWarningsAsErrors, remediate all 21 pre-existing warnings** — New Directory.Build.props sets TreatWarningsAsErrors=true for all projects. Remediated: 10 nullable/CS warnings in src (Loki/BinHandler.cs Finalize rename + oppTile null-forgive, Mjolnir/GraphBuilder.cs + HierarchyBuilder.cs nullable PointLL, Thor/BidirectionalAStar.cs oppEdge/oppTile null-forgive, Odin/NarrativeBuilder.cs CS0162 pragma-scoped to the intentionally-dead option_roundabout_exits=false branch), 6 test warnings (xUnit2000 arg swap, CA2014 stackalloc hoisted out of loop, 4x xUnit1051 TestContext.Current.CancellationToken), 2 CS warnings in build/Build.cs (nullable NugetApiKey/PackageVersion fields + null-forgiving at guarded use sites), and 3 NU1901/NU1903 NuGet advisories on Nuke.Common transitive deps (NuGet.Packaging, System.Security.Cryptography.Xml) resolved via explicit higher-version PackageReference/PackageVersion overrides.
Scope: layer-1+

## TR-VALHALLA-TEST-003

**Replace xunit 2.9.3 with xunit.v3 3.2.2** — xunit (2.9.3) replaced by xunit.v3 (3.2.2) in Directory.Packages.props; test project OutputType set to Exe; execution stays on VSTest/dotnet test/.trx (no MTP opt-in via TestingPlatformDotnetTestSupport/UseMicrosoftTestingPlatformRunner/global.json runner override). Byrd gate: 100 percent pass, zero failed, zero skipped on the full suite.
Scope: layer-1+

