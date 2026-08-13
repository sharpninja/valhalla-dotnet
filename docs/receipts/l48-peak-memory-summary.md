# L48 peak memory measurement (lab run 2026-08-12T23:11Z → 2026-08-13T05:51Z)

## Result
- **success:** false (did not finish tiles)
- **duration:** 23975.545 s (~6.66 hours)
- **host:** PAYTON-LEGION2, 16 vCPU, 23.37 GiB RAM
- **config:** PooledFrontier, MemoryMapped, MaxDop=4, MemoryBudgetGiB=16, ScratchBudgetGiB=10000
- **PBF:** E:\valhalla-qual\pbf\us-lower48.osm.pbf (Geofabrik us-latest)

## Peaks measured (process samples)
| Metric | Bytes | GiB |
| --- | ---: | ---: |
| Peak working set | 15,353,397,248 | **14.299** |
| Peak private | 10,383,212,544 | **9.667** |
| Peak GC heap | 9,908,713,704 | **9.228** |
| Heartbeat max disk (work) | — | **~375.4** |

## How far it got
- Heartbeats progressed: pbf-ingestion → … → **restrictions** (tile-write never started; tilesGiB stayed 0)
- Failure: `ValhallaGenerationResourceLimitException` during `ComplexRestrictionMarkerIndex.EmitWayEndpoints` / `BoundedRoadTileWriter`
- Message: Intermediate scratch budget of **715,827,882 bytes (~0.67 GiB)** would be exceeded

## GCP sizing note (honest)
- This is a **lab** peak on a 16-core / 23 GiB box with MaxDop 4, not a formal 32/64 run.
- Process RSS high water **~14.3 GiB** before failure in restriction-marker path.
- Full successful build (including tile write) may peak higher; this is a **lower bound for a complete run** only if later stages do not exceed 14.3 GiB, which is **not proven** yet.
- Scratch disk footprint approached **~375 GiB** of intermediate data on E:.
- For GCP: plan at least **>14.3 GiB RAM** headroom (recommend 32–64 GiB class until a complete run is measured), and **≥400–500 GiB** local/scratch SSD for intermediates on full US extract with current pipeline.

## Receipts
- docs/receipts/l48-peak-memory-report-20260812T231118Z.json
- docs/receipts/l48-peak-memory-report-latest.json
- implementer log: l48-peak-memory-run.log (~2340 heartbeats)
