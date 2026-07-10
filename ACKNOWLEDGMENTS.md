# Acknowledgments

`SharpNinja.Valhalla` is a C# port of [Valhalla](https://github.com/valhalla/valhalla), an open source routing engine and accompanying libraries for use with OpenStreetMap data. This package would not exist without that project, and this document credits it and the other third-party sources this repository builds on or ships test fixtures from.

## Valhalla

- **Project:** [github.com/valhalla/valhalla](https://github.com/valhalla/valhalla)
- **License:** MIT (see [LICENSE](LICENSE), reproduced from Valhalla's [COPYING](https://github.com/valhalla/valhalla/blob/master/COPYING))
- **Copyright:** (c) 2018 Valhalla contributors; (c) 2015-2017 Mapillary AB, Mapzen

The `Baldr`, `Midgard`, `Loki`, `Sif`, `Thor`, `Odin`, and `Mjolnir` modules in `src/SharpNinja.Valhalla/` are direct ports of the corresponding modules in the upstream Valhalla C++ codebase, adapted to idiomatic C#. Behavior, naming, and module boundaries follow the original engine; see the [README](README.md#modules) for the module-to-module mapping.

The 34 locale dictionaries embedded under `src/SharpNinja.Valhalla/Odin/Locales/*.json` (consumed by the ported `NarrativeBuilder` for maneuver narrative prose) are verbatim copies of upstream Valhalla's `locales/*.json`, covered by the same Valhalla MIT license above.

## OpenStreetMap test fixture data

The `Baldr`/`Mjolnir` parity tests build and read a real tile set from a small OpenStreetMap extract:

- `artifacts/monaco.osm.pbf` and the generated tiles under `artifacts/valhalla-monaco-tiles/`
- OpenStreetMap data is (c) [OpenStreetMap contributors](https://www.openstreetmap.org/copyright) and licensed under the [Open Database License (ODbL)](https://opendatacommons.org/licenses/odbl/)

## Third-party packages

- [Microsoft.Extensions.Logging.Abstractions](https://www.nuget.org/packages/Microsoft.Extensions.Logging.Abstractions) - logging abstraction consumed by `EmbeddedValhallaRoutingClient`
- [Nuke.Common](https://nuke.build/) - build automation
- [xunit](https://xunit.net/) / [xunit.runner.visualstudio](https://www.nuget.org/packages/xunit.runner.visualstudio) - test framework
