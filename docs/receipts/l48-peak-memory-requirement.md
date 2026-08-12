# Formal L48 peak memory is required (operator correction 2026-08-12)

## Operator direction
"No, this is THE WORK that must be completed. I need to know the memory peak for running full 48 state tile build so we can provide accurate Google Cloud Platform cost projections."

## Consequence
AMD-MJOLNIRFRONTIER-001-L48-DEFER does NOT remove the need for full us-lower48 peak-memory measurement for GCP cost inputs. Phase administrative done under amendment is separate from this measurement requirement.

## Measurement approach (in progress)
- Script: build/Run-Lower48PeakMemoryProbe.ps1 + build/L48PeakMemoryProgram.cs
- Input: E:\valhalla-qual\pbf\us-lower48.osm.pbf (Geofabrik us-latest, ~11.2 GiB)
- Pipeline: ManagedRoadGraphPipeline.PooledFrontier, MemoryMapped intermediates
- Captures: Process PeakWorkingSet64, Private bytes, GC heap, PeakIntermediateMemoryBytes, ResourceMetrics phase peaks
- Host: PAYTON-LEGION2 (16 vCPU / 23.37 GiB) - below formal 32/64 bar; run is labeled lab-capacity-measurement (not formal-pass)
- Work root: E:\valhalla-qual\lower48-peak (E: free ~1.2 TiB)

## GCP sizing note
Peaks measured on 16-core/23 GiB with MaxDop=4 may understate concurrent peak vs a 32-vCPU worker. Intermediate peaks and success/failure still bound GCP machine-class selection; re-measure on larger lab host when available for formal parity.

## Status
Full us-lower48 peak probe started on LEGION2.
