# PLAN AMENDMENT (ACTIVE)

**Id:** AMD-MJOLNIRFRONTIER-001-L48-DEFER
**Status:** ACTIVE
**ApprovedUtc:** 2026-08-12T21:58:00Z
**Operator approval:** `APPROVE AMD-MJOLNIRFRONTIER-001-L48-DEFER`
**UpdatedUtc:** 2026-08-12T21:58:00Z
**Target plans:**
- docs/PLAN-FINISH-PHASE-MJOLNIRFRONTIER-001.md
- docs/PLAN-PHASE-MJOLNIRFRONTIER-001-Pooled-Value-Type-Node-Frontier.md section 14
- MCP TODO PHASE-MJOLNIRFRONTIER-001

## Lab policy
Azure is not used. Deploy hosts are PAYTON-LEGION2 and PAYTON-DESKTOP with Octopus. Origin is GitHub (no GitHub Actions).

## Why
Formal Lower-48 (32 vCPU / 64 GiB / >=1 TiB free disk) cannot run on PAYTON-LEGION2 (16/23). PAYTON-DESKTOP is not agent-remotable from LEGION2 without WinRM/SSH credentials. us-lower48 PBF is staged on LEGION2 E:.

## Proposed amendment (operator must approve verbatim)
1. F5 formal L48 pass and product-plan section 14 bullets "Lower-48 qualification passes" and "Actual GCP resource cost is measured and reported" are deferred out of PHASE-MJOLNIRFRONTIER-001 DoD into follow-on work.
2. F6 items that depend on formal L48 (7 consecutive daily L48 builds, CLI default flip to PooledFrontier) are deferred.
3. PHASE-MJOLNIRFRONTIER-001 may be marked done:true when F0-F4 + merge + harness readiness hold and CLI remains Legacy, with hostile AGREE on the amended claim set.
4. Official Valhalla remains production. No early CLI production cutover.
5. No fabricated formal-pass L48 report is permitted.

## Operator approval
**Received and active.** Operator replied exactly `APPROVE AMD-MJOLNIRFRONTIER-001-L48-DEFER`.
Receipt: `docs/APPROVE-AMD-MJOLNIRFRONTIER-001-L48-DEFER.md`.
