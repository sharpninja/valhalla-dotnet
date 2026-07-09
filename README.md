# SharpNinja.Valhalla

Embedded, in-process C# port of the [Valhalla](https://github.com/valhalla/valhalla) OSM routing engine. Consumes local Valhalla tiles directly, no external routing server or process to run.

Ported modules:

- **Baldr** - graph tile reader (tiles, edges, nodes, admin areas, traffic, restrictions)
- **Midgard** - geometry primitives (points, polylines, tiling, distance approximation)
- **Loki** - location search / correlation (snapping input coordinates onto the graph)
- **Sif** - costing models (auto, truck, dynamic cost interfaces)
- **Thor** - route path algorithms (unidirectional/bidirectional A*)
- **Odin** - maneuver/directions building
- **Mjolnir** - tile builder (OSM PBF parsing, graph construction, enhancement, restrictions)
- **Osm** - tile provisioning/on-device build abstractions

## Requirements

- .NET 10 SDK

## Install

```
dotnet add package SharpNinja.Valhalla
```

## Build

Run from the repo root; both scripts self-locate so they work regardless of caller cwd.

```powershell
.\build.ps1 Pack
```

```bash
./build.sh Pack
```

The build is a [Nuke](https://nuke.build/) target chain: `Restore -> Compile -> Test -> Pack -> Publish`.

- Local default target: `Pack` (produces `artifacts/nuget/*.nupkg`)
- CI (Azure Pipelines) runs `Publish`, which chains through the rest and pushes to nuget.org using the `NUGET_API_KEY` pipeline secret

Common targets:

```powershell
.\build.ps1 Test               # run the test suite (trx output under artifacts/test-results)
.\build.ps1 Pack -Configuration Release
```

## Repository layout

- `src/SharpNinja.Valhalla/` - the engine (packable library)
- `tests/SharpNinja.Valhalla.Tests/` - xUnit test suite, mirrors the module layout above
- `build/` - Nuke build project (`Build.cs`)
- `SharpNinja.Valhalla.slnx` - solution file

## License

See repository metadata on [nuget.org](https://www.nuget.org/packages/SharpNinja.Valhalla) / [GitHub](https://github.com/sharpninja/valhalla-dotnet).
