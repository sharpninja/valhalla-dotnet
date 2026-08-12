# PLAN AMENDMENT DRAFT (NOT ACTIVE until operator approves)

**Id:** AMD-MJOLNIRFRONTIER-001-L48-DEFER
**TimestampUtc:** 2026-08-12T20:37:00Z
**Target plans:**
- docs/PLAN-FINISH-PHASE-MJOLNIRFRONTIER-001.md
- docs/PLAN-PHASE-MJOLNIRFRONTIER-001-Pooled-Value-Type-Node-Frontier.md section 14
- MCP TODO PHASE-MJOLNIRFRONTIER-001

## Why
Formal Lower-48 (32 vCPU / 64 GiB / >=1 TiB free disk) cannot run in this lab right now:
- Azure subscription pending dues (enable NotAllowed; writes ReadOnlyDisabled)
- Local PAYTON-LEGION2 is 16 vCPU / ~23 GiB
- GCP unauthenticated
- us-lower48 PBF is staged and ready

## Proposed amendment (operator must approve verbatim)
1. F5 formal L48 pass and product-plan section 14 bullets "Lower-48 qualification passes" and "Actual GCP resource cost is measured and reported" are **deferred** out of PHASE-MJOLNIRFRONTIER-001 DoD into follow-on TODO `PHASE-MJOLNIRFRONTIER-002` (or equivalent).
2. F6 items that depend on formal L48 (7 consecutive daily L48 builds, CLI default flip to PooledFrontier, device cohort against L48 tiles) are likewise deferred to that follow-on.
3. PHASE-MJOLNIRFRONTIER-001 may be marked `done: true` when all of the following hold:
   - F0-F3 exits evidenced (clean tip; residual gates; adaptive; determinism matrix; Generation.Tests Debug+Release zero fail/skip)
   - F4 Nashville report exists (experimental allowed if memory gate fail-closed per plan)
   - PR merged to main with PooledFrontier path present
   - CLI default remains Legacy
   - Formal L48 harness + promotion scripts + host-bar fail-closed receipts exist
   - Hostile validator AGREE on the amended (non-L48) claim set
4. Official Valhalla remains production. No early CLI production cutover.
5. No fabricated formal-pass L48 report is permitted under this amendment.

## Explicit non-goals of amendment
- Does not claim FR-VALHALLA-020 full production readiness
- Does not flip CLI to PooledFrontier
- Does not remove need for formal L48 before production replacement (plan section 12 steps 4-11 remain required later)

## Operator approval
To activate: reply exactly `APPROVE AMD-MJOLNIRFRONTIER-001-L48-DEFER` (or edit and restate).
Until that string is received, this draft has **zero** effect on DoD or MCP done.
