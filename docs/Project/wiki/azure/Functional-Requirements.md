# Functional Requirements (MCP Server)

## FR-VALHALLA-002 Test SDK stays on a supported, currency-checked version

The test build pipeline uses a current, compatible Microsoft.NET.Test.Sdk version, verified against the live NuGet registry rather than assumed.
Scope: layer-1+

## FR-VALHALLA-003 Test suite runs on xUnit v3

The test framework is xUnit v3 (package id xunit.v3), not the frozen xUnit v2 line (package id xunit, stopped at 2.9.3).
Scope: layer-1+

