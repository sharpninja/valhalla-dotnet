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
| `Sif` | `valhalla/sif` | Costing models: `DynamicCost`, `AutoCost`, `TruckCost`, edge labels | `src/SharpNinja.Valhalla/Sif/` (8 files) | 4 files |
| `Thor` | `valhalla/thor` | Path algorithms: unidirectional and bidirectional A* (including alternate-route recost/viability filters), trip-leg building | `src/SharpNinja.Valhalla/Thor/` (10 files) | 10 files |
| `Odin` | `valhalla/odin` | Maneuver building, directions-leg assembly, and en-US narrative prose (`NarrativeBuilder` + embedded locale dictionaries; see [Known gaps](#known-gaps) for remaining parity depth) | `src/SharpNinja.Valhalla/Odin/` | tests under `Odin/` |
| `Mjolnir` | `valhalla/mjolnir` | Tile builder: OSM PBF parsing, graph construction, enhancement, shortcuts, restrictions | `src/SharpNinja.Valhalla/Mjolnir/` (33 files) | 14 files |
| `Osm` | - | This package's own on-device provisioning seam (tile-set building, extract retrieval abstractions) | `src/SharpNinja.Valhalla/Osm/` (4 files) | 2 files |
| `Traffic` | - | Exact feed transport, TomTom/HERE normalization, conflict resolution, traffic policy, edge matching, graph traffic controls, lane friction, and deterministic route selection | `src/SharpNinja.Valhalla/Traffic/` | tests under `Traffic/` and `Nashville/` |

On top of these, the package root exposes the public, provider-neutral surface: `IOsmRoutingClient`, `EmbeddedValhallaRoutingClient`, `EmbeddedValhallaGraphReaderFactory`, `GeoCoordinate`, `IEncodedPolylineDecoder`, `ValhallaPolylineDecoder`, and `OsmRoutingErrorCodes`. Traffic APIs live under `SharpNinja.Valhalla.Traffic` and its `Providers`, `Routing`, and `Tiles` subnamespaces.

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
- Set `OsmRouteRequest.ComputeAlternativeRoutes` (with no `Via` points) to get up to 2 distinct routes back in `OsmRouteResult.Routes`, primary first then by ascending cost; see [Known gaps](#known-gaps) for the viability caveat on small maps.

### Decoding an encoded shape

Route candidates already include decoded `RoutePoints`, but the raw encoded polyline6 string is also available (`OsmRouteCandidate.EncodedPolyline`) if you need to decode it yourself, e.g. for a cached/serialized route:

```csharp
IEncodedPolylineDecoder decoder = new ValhallaPolylineDecoder();
IReadOnlyList<GeoCoordinate> points = decoder.Decode(route.EncodedPolyline);
```

## Traffic and closure feeds

Version 1.3 provides a UI-agnostic traffic DATA and route-selection pipeline for exact TomTom, HERE, proxy, fixture, and future-provider feeds. The library fetches host-configured URLs, normalizes flow/incidents/closures/restrictions, resolves provider conflicts, projects route modifiers, matches Valhalla directed edges, composes graph-derived traffic controls and lane friction, ranks route candidates, optionally writes through an engine-owned traffic-tile boundary, and returns immutable data contracts. It has no Avalonia, Mapsui, TruckMate, or gateway dependency.

### Exact URLs and a credential-injecting gateway

Configure each flow, incident, closure, restriction, or composite endpoint independently. `TrafficFeedCredentialMode.None` means that SharpNinja.Valhalla sends the configured URL without looking up, appending, or transmitting an API key. This is the mode for a central gateway or a host-owned `DelegatingHandler` that authenticates the caller and injects the provider credential outside this library.

```csharp
using SharpNinja.Valhalla.Traffic;
using SharpNinja.Valhalla.Traffic.Providers;
using SharpNinja.Valhalla.Traffic.Providers.Here;
using SharpNinja.Valhalla.Traffic.Providers.TomTom;
using SharpNinja.Valhalla.Traffic.Routing;

var endpoints = new[]
{
    new TrafficFeedEndpoint(
        "tomtom",
        TrafficFeedKind.Flow,
        new Uri("https://api.tomtom.com/traffic/services/4/flowSegmentData/absolute/10/json"),
        TrafficFeedCredentialMode.None),
    new TrafficFeedEndpoint(
        "tomtom",
        TrafficFeedKind.Incident,
        new Uri("https://api.tomtom.com/traffic/services/5/incidentDetails"),
        TrafficFeedCredentialMode.None),
};

// The host owns this pipeline. It may rewrite the original provider URL to
// /vendor/{vendorId}/..., attach an entitlement token, and let the gateway inject
// the provider key. SharpNinja.Valhalla neither references nor reimplements it.
HttpMessageInvoker hostTransport = CreateHostOwnedTrafficTransport();

var tomTomClient = new ConfiguredTrafficFeedClient(
    "tomtom",
    hostTransport,
    endpoints);

var sources = new[]
{
    new TrafficDataSourceRegistration(
        tomTomClient,
        TrafficSourceKind.Proxy,
        new[] { TrafficFeedKind.Flow, TrafficFeedKind.Incident }),
};

var factory = new TrafficDataFactory(
    sources,
    TrafficFeedAdapterRegistry.CreateDefault(),
    new TrafficConflictResolver(new[] { "tomtom", "here" }),
    new TrafficDataFactoryOptions
    {
        TrafficPolicy = TrafficPolicy.Enabled,
    });

NormalizedTrafficSnapshot snapshot =
    await factory.CreateSnapshotAsync(new TrafficDataRequest());
```

The host pipeline may instead receive exact gateway URLs directly. It may also use its own original-host-to-vendor mapping to rewrite the exact TomTom/HERE URL while preserving the provider path and query. In either shape, the provider registration id remains `tomtom` or `here`; transport provenance is reported separately through `TrafficSourceKind.Proxy`. `snapshot.SourceStatuses` exposes configured versus effective source status so UI layers can label the result truthfully as proxy, direct provider, fixture, custom, or unavailable.

Direct-provider hosts can select `QueryParameter`, `Header`, or `CustomRequestMutator`. Query/header modes resolve credentials only through the host-supplied `ITrafficProviderCredentialProvider`; the custom mode delegates request mutation to host code. Diagnostics redact URL query strings and never persist API keys or credential headers. Hosts should keep secrets in their secret store or gateway configuration, not in endpoint URLs or application configuration committed to source.

`TrafficPolicy.Disabled` excludes dynamic delay from ETA and friction while retaining verified closures as hard route constraints. `TrafficPolicy.Enabled` includes dynamic delay in ETA and friction. Traffic tile mutation is opt-in and requires an `IValhallaTrafficTileWriter`; requesting tile output without a writer returns edge updates plus a diagnostic rather than silently claiming success.

### Route evaluation and selection

`RouteSelectionCoordinator` is the DATA-layer composition boundary for presentation hosts. Given engine candidates, exact-edge traffic evidence, graph-derived traffic-control counts, lane-topology projections, a traffic policy, and the user's route-preference weights, it evaluates each candidate once and returns deterministic Fastest, Shortest, and Easiest rankings. The selected ranking uses the requested goal; its near-tie rules use the other preference weights as tie breakers rather than presenting three unrelated winners.

Each `RouteSelectionCandidateResult` includes base metrics, traffic-adjusted ETA, structural friction, exact route identity, and source provenance. Each `RouteSelectionDecision` states whether the candidate was selected, offered as an alternative, deprioritized, or excluded, with a stable reason such as a direction-safe closure, canonical overlay mismatch, infeasible lane changes, unverified lane topology, duplicate canonical route, or the configured alternative limit. This lets a UI explain why a normally competitive route disappeared after live modifiers were applied without recreating ranking logic.

Canonical freeway friction comes from lane topology and route-specific transitions—not a blanket highway penalty. `GraphTileLaneFrictionProjection`, `LaneFrictionGraphBuilder`, and `LaneFrictionAnalyzer` compose mandatory lane changes, merges, exits, and graph traffic controls. Hosts may supply a versioned `LaneTopologyOverlay` where graph data cannot express verified lane continuity; mismatched graph signatures are surfaced as evidence rather than silently applied.

### Matching normalized geometry to Valhalla directed edges

`GraphTileTrafficSpatialIndex` is the concrete, UI-agnostic `IValhallaTrafficSpatialIndex` implementation for graph-tile matching. It reads the canonical directed-edge id and shape from the query's intersecting `.gph` or `.gph.gz` tiles, keeps opposite or nearby parallel carriageways separate with a narrow configurable tolerance, and performs graph reads, index construction, and matching away from the caller thread.

```csharp
using SharpNinja.Valhalla.Traffic;
using SharpNinja.Valhalla.Traffic.Tiles;

using var spatialIndex = new GraphTileTrafficSpatialIndex(matchToleranceMeters: 8);

var context = new ValhallaGraphTrafficContext(
    GraphSignature: ComputeStableGraphSignature(graphTileDirectory),
    GraphTileDirectory: graphTileDirectory);

var geometry = new TrafficGeometry(
    TrafficGeometryKind.LineString,
    providerCoordinates,
    TrafficGeometryDirection.AlongCoordinates);

IReadOnlyList<TrafficEdgeMatchCandidate> candidates =
    await spatialIndex.MatchAsync(geometry, context, cancellationToken);
```

Direction is an explicit data contract. The two-argument `TrafficGeometry` constructor defaults to `TrafficGeometryDirection.Unknown`, which allows proximity matching but leaves closures direction-ambiguous. Use `AlongCoordinates` only when the source guarantees that coordinate order is travel direction. Use `BothDirections` only when the event explicitly applies to both directions. A line candidate must cover at least half of the shorter provider/edge span, and even explicit direction remains non-closing until at least 20 meters of provider-span overlap is matched. Those independent coverage gates reject most short or shallow tolerance-halo matches. If multiple unequal-distance candidates still survive for the same provider segment, every candidate remains direction-ambiguous and non-closing; a host needs graph-topology or provider-link identity to disambiguate that case. This prevents unknown provider coordinate order from closing the opposite carriageway.

Snapshots are keyed by the exact graph signature plus the intersecting query tiles and bounded to 32 entries per index. Call `Invalidate(graphSignature)` after replacing that graph, `Clear()` to drop every cached snapshot, and `Dispose()` when the index lifetime ends. Cancellation is checked before and between synchronous graph-tile reads and throughout index construction; an in-progress `GraphTile.Create` read/decompression completes before cancellation is observed. Once the last waiter cancels, the shared build is cancelled at those boundaries. Cancelling one waiter does not poison another request sharing the same build.

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

Both original behavior gaps versus a full Valhalla HTTP service are now closed for the surfaced routing behavior:

- **Maneuver narrative text.** `OsmRouteManeuver.Instruction` carries en-US written turn-by-turn prose produced by the ported Odin `NarrativeBuilder` (all driving maneuver families). Remaining upstream-parity depth that the current DTO does not surface - spoken/verbal strings, localized length/time, additional-locale grammar, and the transit/pedestrian/bike-share/indoor maneuver families - is being ported in later slices.
- **Alternate routes.** When `OsmRouteRequest.ComputeAlternativeRoutes` is set and no via/through points are supplied, the engine computes multiple distinct routes (bidirectional A* with the ported `alternates.h` sharing/stretch viability filters and the `recost.h` forward recost pass) and `OsmRouteResult.Routes` carries them primary-first, then by ascending cost. Via routes stay on the single-leg axis. Small maps may still yield a single route when no viable alternate exists.

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

The test suite uses xUnit v3 3.2.2 and contains 147 C# test files across the engine modules plus the provider, traffic-policy, edge-matching, graph-control, lane-friction, route-selection, hostile-behavior, and Nashville integration slices. The current 1.3.0 release gate is 1,385 passing tests with zero failures and zero skipped tests.

```powershell
dotnet test tests/SharpNinja.Valhalla.Tests/SharpNinja.Valhalla.Tests.csproj
```

or via the Nuke build (`.\build.ps1 Test`), which also drops `.trx` results under `artifacts/test-results` for CI publishing.

## Repository layout

```
src/SharpNinja.Valhalla/     the engine (packable library)
  Baldr/    Midgard/  Loki/  Sif/  Thor/  Odin/  Mjolnir/  Osm/   ported modules (see table above)
  Traffic/                                                        traffic providers, normalization, routing policy, edge/tile data, lane friction, and route selection
  *.cs                                                            public routing surface (client, factory, coordinate, polyline decoder, error codes)
tests/SharpNinja.Valhalla.Tests/   xUnit test suite, same module layout
build/                             Nuke build project (Build.cs)
SharpNinja.Valhalla.slnx           solution file
build.ps1 / build.sh                bootstrap scripts that invoke the Nuke build
```

## License

MIT, matching upstream [Valhalla](https://github.com/valhalla/valhalla)'s license. See [LICENSE](LICENSE) and [ACKNOWLEDGMENTS.md](ACKNOWLEDGMENTS.md) for upstream and third-party attribution.
