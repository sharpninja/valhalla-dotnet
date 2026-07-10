# SharpNinja.Valhalla

[![NuGet](https://img.shields.io/nuget/v/SharpNinja.Valhalla.svg)](https://www.nuget.org/packages/SharpNinja.Valhalla)

Embedded, in-process C# port of the [Valhalla](https://github.com/valhalla/valhalla) OSM routing engine: Baldr graph reader, Sif costing, Thor route engine, Odin directions, and a Mjolnir tile builder, exposed behind a provider-neutral routing client.

There is no server process and no HTTP hop. The library reads Valhalla tiles from a local directory and computes routes entirely in-process, which makes it a fit for embedding routing directly into a desktop or mobile app.

## Why

Valhalla is normally run as a standalone service (`valhalla_service` + a tile directory built by `valhalla_build_tiles`) that clients call over HTTP. `SharpNinja.Valhalla` ports the parts of that pipeline needed to go from "a `.osm.pbf` extract" to "a route between two points" as plain, dependency-light C#, so a .NET app can:

- Read tiles built by stock Valhalla, or build its own tiles on-device from an OSM extract.
- Compute a route (auto or truck costing) without a network call.
- Get maneuver/shape output usable for turn-by-turn UI.

## Modules

Each module is a fairly direct port of the corresponding Valhalla C++ module, with the same responsibilities and name:

| Module | Ported from | Responsibility | Source | Tests |
|---|---|---|---|---|
| `Baldr` | `valhalla/baldr` | Graph tile reader: tiles, directed edges, nodes, admin areas, traffic, restrictions, sign/street name info | `src/SharpNinja.Valhalla/Baldr/` (39 files) | 32 files |
| `Midgard` | `valhalla/midgard` | Geometry primitives: points, polylines, tiling math, distance approximation, encoded-shape helpers | `src/SharpNinja.Valhalla/Midgard/` (18 files) | 10 files |
| `Loki` | `valhalla/loki` | Location correlation: snapping input coordinates onto the graph, closest-edge search | `src/SharpNinja.Valhalla/Loki/` (4 files) | 3 files |
| `Sif` | `valhalla/sif` | Costing models: `DynamicCost`, `AutoCost`, `TruckCost`, edge labels | `src/SharpNinja.Valhalla/Sif/` (8 files) | 3 files |
| `Thor` | `valhalla/thor` | Path algorithms: unidirectional and bidirectional A*, trip-leg building | `src/SharpNinja.Valhalla/Thor/` (10 files) | 8 files |
| `Odin` | `valhalla/odin` | Maneuver building and directions-leg assembly (turn-by-turn structure; narrative prose text is not ported, see [Known gaps](#known-gaps)) | `src/SharpNinja.Valhalla/Odin/` (8 files) | 6 files |
| `Mjolnir` | `valhalla/mjolnir` | Tile builder: OSM PBF parsing, graph construction, enhancement, shortcuts, restrictions | `src/SharpNinja.Valhalla/Mjolnir/` (33 files) | 14 files |
| `Osm` | - | This package's own on-device provisioning seam (tile-set building, extract retrieval abstractions) | `src/SharpNinja.Valhalla/Osm/` (4 files) | - |

On top of these, the package root exposes the public, provider-neutral surface: `IOsmRoutingClient`, `EmbeddedValhallaRoutingClient`, `EmbeddedValhallaGraphReaderFactory`, `GeoCoordinate`, `IEncodedPolylineDecoder`, `ValhallaPolylineDecoder`, and `OsmRoutingErrorCodes`.

## Requirements

- .NET 10 SDK
- A local Valhalla tile directory (built by stock Valhalla's `valhalla_build_tiles`, or built on-device from a `.osm.pbf` extract with this package's `Mjolnir`/`Osm` types)

## Install

```
dotnet add package SharpNinja.Valhalla
```

## Quickstart

The routing client is provider-neutral by design (`IOsmRoutingClient`) so callers do not depend on Valhalla-specific DTOs. `EmbeddedValhallaRoutingClient` is the in-process implementation that drives the ported engine directly against a local tile directory.

```csharp
using Microsoft.Extensions.DependencyInjection;
using SharpNinja.Valhalla;

var services = new ServiceCollection();
services.AddLogging();
services.AddSingleton<EmbeddedValhallaGraphReaderFactory>();
services.AddSingleton<IOsmTileDirectoryProvider>(new FixedTileDirectoryProvider("/path/to/valhalla_tiles"));
services.AddSingleton<IOsmRoutingClient, EmbeddedValhallaRoutingClient>();

using var provider = services.BuildServiceProvider();
var client = provider.GetRequiredService<IOsmRoutingClient>();

var request = new OsmRouteRequest(
    Endpoint: null,
    Origin: new GeoCoordinate(47.6062, -122.3321),      // Seattle
    Destination: new GeoCoordinate(45.5152, -122.6784),  // Portland
    Costing: OsmRouteCostings.Auto);

var result = await client.CalculateRouteAsync(request);

if (result.Error is not null)
{
    Console.WriteLine($"Routing failed: {result.Error}");
}
else
{
    var route = result.Routes[0];
    Console.WriteLine($"{route.DistanceMeters / 1000:F1} km, {route.DurationSeconds / 60} min, {route.Maneuvers.Count} maneuvers");
}

// A minimal IOsmTileDirectoryProvider for a directory that never changes.
sealed class FixedTileDirectoryProvider(string path) : IOsmTileDirectoryProvider
{
    public Task<string?> GetTileDirectoryAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<string?>(path);
}
```

Notes on the shapes above:

- `EmbeddedValhallaGraphReaderFactory` caches one `GraphReader` per tile directory (tile loading is expensive) and hands out a `Lease` with a lock gate, because the ported tile cache is not thread-safe. `EmbeddedValhallaRoutingClient` already serializes on that gate internally, so callers just await `CalculateRouteAsync` as normal.
- `IOsmTileDirectoryProvider` is host-supplied on purpose: the package does not care whether the tile directory comes from settings, is downloaded on demand, or is bundled with the app.
- Truck routing is available via `OsmRouteCostings.Truck` and `OsmRouteRequest.TruckOptions` (height/width/length/weight/axle count); omitting `TruckOptions` falls back to stock Valhalla's default truck profile.
- `OsmRouteRequest.ComputeAlternativeRoutes` is currently a no-op (see [Known gaps](#known-gaps)).

### Decoding an encoded shape

Route candidates already include decoded `RoutePoints`, but the raw encoded polyline6 string is also available (`OsmRouteCandidate.EncodedPolyline`) if you need to decode it yourself, e.g. for a cached/serialized route:

```csharp
IEncodedPolylineDecoder decoder = new ValhallaPolylineDecoder();
IReadOnlyList<GeoCoordinate> points = decoder.Decode(route.EncodedPolyline);
```

## Building tiles on-device

If you don't already have a Valhalla tile directory, the `Osm`/`Mjolnir` types can build one on-device from an `.osm.pbf` extract:

- `IOsmExtractSource` - retrieves the extract (the only network step; entirely opt-in)
- `ITileSetBuilder` / `MjolnirTileSetBuilder` - builds tiles from the extract via the ported `Mjolnir.TileBuilder`
- `IOnDeviceTileProvisioner` - orchestrates the two: returns the existing tile directory if tiles are already present, otherwise retrieves the extract and builds tiles, propagating the extract source's error verbatim on failure

```csharp
var result = await provisioner.EnsureTilesAsync();
if (result.Success)
{
    // result.TileDirectory now has usable tiles
}
else
{
    // result.Error is one of OsmRoutingErrorCodes
}
```

## Error codes

`IOsmRoutingClient` implementations report failures as one of the canonical `OsmRoutingErrorCodes` string constants:

| Code | Meaning |
|---|---|
| `not_configured` | No tile directory configured, or it's missing/empty on disk |
| `auth_error` | Authentication failure (reserved for HTTP-backed clients) |
| `rate_limit` | Rate limited (reserved for HTTP-backed clients) |
| `transport` | Tile I/O or access failure reading the local tile directory |
| `parse` | No route found (no snap, no path, or an engine-internal failure building directions) |
| `http_error` | Generic HTTP failure (reserved for HTTP-backed clients) |
| `invalid_source` | The configured OSM extract source is present but invalid (e.g. non-HTTPS URL) |

## Known gaps

The embedded client intentionally has two behavior gaps versus a full Valhalla HTTP service; neither affects distance/duration/shape/friction-input accuracy:

- **No maneuver narrative text.** `OsmRouteManeuver.Instruction` is always empty - the Odin narrative/prose generation pass is not ported. Maneuver type, distance, duration, and shape indices are all populated.
- **No alternate routes.** The ported route engine returns a single `TripLeg`, so `OsmRouteResult.Routes` always has exactly one candidate and `OsmRouteRequest.ComputeAlternativeRoutes` is a no-op.

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
.\build.ps1 Test                        # run the test suite (trx output under artifacts/test-results)
.\build.ps1 Pack -Configuration Release
.\build.ps1 Clean                       # delete bin/obj and artifacts output
```

## Testing

The test suite is xUnit and mirrors the `src/` module layout 1:1 (76 test files across `Baldr`, `Loki`, `Midgard`, `Mjolnir`, `Odin`, `Sif`, and `Thor`), plus a `BaldrMonacoParityTests` suite that checks the ported `Baldr` reader against reference behavior.

```powershell
dotnet test tests/SharpNinja.Valhalla.Tests/SharpNinja.Valhalla.Tests.csproj
```

or via the Nuke build (`.\build.ps1 Test`), which also drops `.trx` results under `artifacts/test-results` for CI publishing.

## Repository layout

```
src/SharpNinja.Valhalla/     the engine (packable library)
  Baldr/    Midgard/  Loki/  Sif/  Thor/  Odin/  Mjolnir/  Osm/   ported modules (see table above)
  *.cs                                                            public routing surface (client, factory, coordinate, polyline decoder, error codes)
tests/SharpNinja.Valhalla.Tests/   xUnit test suite, same module layout
build/                             Nuke build project (Build.cs)
SharpNinja.Valhalla.slnx           solution file
build.ps1 / build.sh                bootstrap scripts that invoke the Nuke build
```

## License

MIT, matching upstream [Valhalla](https://github.com/valhalla/valhalla)'s license. See [LICENSE](LICENSE) and [ACKNOWLEDGMENTS.md](ACKNOWLEDGMENTS.md) for upstream and third-party attribution.
