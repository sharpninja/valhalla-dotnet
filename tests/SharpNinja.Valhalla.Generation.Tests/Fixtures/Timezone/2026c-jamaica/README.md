# Timezone 2026c Jamaica parity fixture

This fixture is record 86 (`America/Jamaica`) extracted without coordinate changes from:

- Upstream project: evansiroky/timezone-boundary-builder
- Release: 2026c
- Asset: `timezones-with-oceans-1970.shapefile.zip`
- Source URL: https://github.com/evansiroky/timezone-boundary-builder/releases/download/2026c/timezones-with-oceans-1970.shapefile.zip
- Source ZIP SHA-256: `E68090F0C7B1F3574287098BAEEF7554AC73E21C52C4D548D7EDF304EFB417F2`
- Official Valhalla importer: 3.8.3 commit `a60c7cbfc83e073f50887cd27e0109d02e6b64e5`
- Official container digest: `sha256:70b45295d81035e3562e1bbf996a28d5fc55e1ccc5d7e3fff9f297d3b1a1359f`

The full official database stores this boundary as a SRID 4326 MultiPolygon. Its uncompressed little-endian WKB is 918 bytes and has SHA-256 `141D9C3EC6D1CE32A665011B2D3E80C43B80C2D2E7C58ADADE417C47161E05EC`.

The subset preserves the original SHP record bytes, DBF timezone identifier, and projection. SHP/SHX headers and the DBF record count were rewritten only to make the single-record fixture independently readable.
