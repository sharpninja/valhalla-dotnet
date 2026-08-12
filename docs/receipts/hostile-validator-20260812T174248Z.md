# Hostile Validator Receipt

- TimestampUtc: 2026-08-12T17:42:48Z
- ValidatorIdentity: GrokSubagentHostile
- Workspace: F:\GitHub\TruckMate
- CodeUnderTest: F:\GitHub\valhalla-dotnet\.worktrees\pooled-node-frontier
- Branch: codex/pooled-node-frontier
- HEAD: e74dcb23f3c8d2a373e5247b5eb2a6c7a05af39b
- Scratch: C:\Users\kingd\AppData\Local\Temp\grok-goal-845873d7e251\implementer
- OverallVerdict: DISAGREE

## Claims

### C-F0: F0 exit holds (clean tip + Generation.Tests D/R green + catalog filter green)
- Verdict: FAIL
- Evidence:
  - LIVE git: branch `codex/pooled-node-frontier` tracking `origin/codex/pooled-node-frontier`; HEAD `e74dcb23f3c8d2a373e5247b5eb2a6c7a05af39b` (`e74dcb2 feat(generation): F1-F3 enhance stage...`).
  - LIVE tip is NOT clean: `git status --porcelain=v1` shows `?? artifacts/qualification/`. Implementer `f0-tip.txt` itself records `STATUS=?? artifacts/qualification/`.
  - Generation.Tests Debug green: `f0-f3-full-suite-Debug.log` -> Passed 376 / Failed 0.
  - Generation.Tests Release green: `f0-f3-full-suite-Release.log` -> Passed 376 / Failed 0.
  - Catalog filter green: `codex-tdd-filter-Debug.log` and `codex-tdd-filter-Release.log` -> Passed 157 / Failed 0 each.
  - Ancillary inconsistency: `f0-test-exits2.txt` records `valhalla=1` while `f0-valhalla-tests-Release.log` shows Passed 1485; not required for this claim, noted only.
- Attack note: partial suite green does not salvage F0 when clean-tip is an explicit exit condition.

### C-F1: Stage G enhance + source guards + residual matrix/enhancement tests (38 passed)
- Verdict: PASS
- Evidence:
  - Source: `src/SharpNinja.Valhalla.Generation/Roads/Frontier/PooledRoadEnhanceStage.cs` (`PooledRoadEnhanceStage`) calls `enhancer.EnhanceTileDirectory`.
  - Source: `src/SharpNinja.Valhalla/Mjolnir/GraphEnhancer.cs` exposes `EnhanceTileDirectory`.
  - Wired from `ManagedRoadGraphBuilder.cs` via `PooledRoadEnhanceStage.ApplyAsync`.
  - Tests exist: `PooledRoadEnhanceStageTests.cs`, `PooledProductionPathGuardTests.cs`.
  - `f1-f3-gate-filter.log`: `Passed! - Failed: 0, Passed: 38, Skipped: 0, Total: 38`.
  - Note: earlier `f1-smoke.log` shows a FAIL on `BuildPooledFrontierAsync_DoesNotReturnCompleteInMemoryTileDictionary`; later full suite + gate filter supersede that intermediate smoke failure for this claim.

### C-F2: AdaptiveGenerationParallelism.FitWorkerCount + SelectedDop + adaptive tests green
- Verdict: PASS
- Evidence:
  - Source: `src/.../Lifecycle/AdaptiveGenerationParallelism.cs` defines `FitWorkerCount`.
  - Used in `ManagedRoadGraphBuilder.cs` (~line 293) and `PooledRoadEnhanceStage.cs` (~line 70).
  - `ManagedRoadGraphResourceMetrics.SelectedDop` property at `ManagedRoadGraphBuilder.cs` line 64; assigned from fitted DOP.
  - Tests: `AdaptiveGenerationParallelismTests.cs`, `AdaptiveResourceSchedulingTests.cs` (`BuildPooledFrontier_PropagatesSelectedDop_InFrontierOrResourceMetrics`).
  - Covered by green catalog filter (157) and full suite (376) logs on tip SHA.

### C-F3: Gate 8 determinism matrix tests exist/passed; CLI/request default remains Legacy
- Verdict: PASS
- Evidence:
  - Tests: `PooledFrontierDeterminismMatrixTests.cs` with DOP theory (1/2/4), parity/compat/restriction/shape/stale/default-lock tests.
  - Included in filter list of `f0-f3-full-and-commit.ps1` and green full suite logs.
  - Request default: `ManagedRoadGraphBuildRequest.Pipeline` init `= ManagedRoadGraphPipeline.Legacy` (ManagedRoadGraphBuilder.cs lines 33-34).
  - CLI default: `ValhallaGenerationCli.cs` road-pipeline default `ManagedRoadGraphPipeline.Legacy.ToString()` (lines 838-839).
  - Structural lock-in test asserts default remains Legacy until promotion.

### C-F4: Nashville qualification formal FR-VALHALLA-020 pass (memory AND performance)
- Verdict: FAIL
- Evidence (hostile attack holds):
  - Report exists: scratch `nashville-report.json` and repo `artifacts/qualification/nashville/nashville-pooled-qualification-report.json` (SHA256 match F5B1B9D4...).
  - Warm-up + five measured runs present (runs index 0 role warm-up; 1-5 measured; all success:true on monaco).
  - Formal exit REJECTED by report itself:
    - `pbfPath` is `artifacts\monaco.osm.pbf` (Monaco proxy, not Nashville/Tennessee formal extract success).
    - `verdict.performanceGatePassed`: **false**
    - `verdict.memoryGatePassed`: true (memory alone insufficient)
    - `verdict.status`: `experimental-proxy-monaco-tn-attempt-failed`
    - notes: Tennessee-latest failed external-sort scratch budget; Official Valhalla oracle not run; status remains experimental until FR-VALHALLA-020 oracle+performance gates pass.
  - `nashville-tn-runner.log`: multiple `RUN_FAIL` for external sort scratch budget below merge bound.
- Claim of formal FR-VALHALLA-020 pass (memory AND performance) is false.

### C-F5: Lower-48 formal report under 32 vCPU / 64 GiB / 1 TiB with three successful pooled runs
- Verdict: FAIL
- Evidence (hostile attack holds):
  - Report exists: scratch `l48-report.json` / repo `artifacts/qualification/lower48/l48-pooled-qualification-report.json` (SHA256 match D159E1AA...).
  - Machine: `vCpu=16`, `memoryGiB=23` (scratch `machine-profile.txt`: VCpu=16, MemoryGiB=23.4, L48FormalBarMet=False).
  - Does NOT meet 32 vCPU / 64 GiB / 1 TiB formal bar.
  - `pbfPath`: NOT_PRESENT; `pbfSha256`: NONE.
  - All three runs `success:false` with failures `blocked-formal-capacity-and-pbf-missing` / `blocked`.
  - `verdict.status`: `blocked-formal-capacity-and-pbf-missing`; memory/performance/semantic gates all false.
  - Zero successful pooled L48 runs.

### C-F6: Branch merged to main after gates; promotion/rollout; MCP PHASE-MJOLNIRFRONTIER-001 done:true full DoD
- Verdict: FAIL
- Evidence (hostile attack holds):
  - LIVE: `git merge-base --is-ancestor HEAD main` exit 1; HEAD not on main.
  - LIVE: only `codex/pooled-node-frontier` (+ remote) contains HEAD; main at `fd6cce9`.
  - PR #1 OPEN, not merged: `gh pr view` -> state OPEN, mergedAt null, url https://github.com/sharpninja/valhalla-dotnet/pull/1
  - Implementer `f6-main-and-cli.txt`: `merged_to_main=false`, `promotion_7day_l48=not_started`, `mcp_done=false`.
  - MCP TODO snapshot `todo-get-PHASE-MJOLNIRFRONTIER-001.yaml`: top-level `done: false`; Gates 6 residual/7/8, Nashville, L48, merge/rollout tasks still `done: false`.
  - PR-only + done:false + not merged => formal F6 exit fails.

### C-SCRATCH: Required scratch evidence files exist under implementer path
- Verdict: PASS
- Evidence: verified present under `C:\Users\kingd\AppData\Local\Temp\grok-goal-845873d7e251\implementer`:
  - tip: f0-tip.txt
  - suites: f0-f3-full-suite-Debug.log, f0-f3-full-suite-Release.log, codex-tdd-filter-Debug.log, codex-tdd-filter-Release.log, f1-f3-gate-filter.log
  - reports: nashville-report.json, l48-report.json, machine-profile.txt
  - todo-get: todo-get-PHASE-MJOLNIRFRONTIER-001.yaml
  - also present: f6-main-and-cli.txt, f6-pr.txt, todo-update-PHASE-MJOLNIRFRONTIER-001.yaml

### Session-log completeTurn (ancillary)
- Verdict: UNKNOWN
- Evidence: plugin present at `C:\Users\kingd\.grok\installed-plugins\f--github-mcpserver-grok-plugin-67f1f31f\lib\repl-invoke.ps1`. Attempted `workflow.sessionlog.completeTurn` without a clearly bootstrapped active turn; no durable success payload verified. `workflow.sessionlog.status` rejected (schema_validation_failed / no schema). Session-log only; does not alter product claim verdicts.

## Explicit FAIL list
1. C-F0 - worktree tip dirty (`?? artifacts/qualification/`); clean tip required for F0 exit.
2. C-F4 - Nashville formal FR-VALHALLA-020 not passed (monaco proxy; performanceGatePassed=false; experimental status; TN extract failed; no official oracle).
3. C-F5 - Lower-48 formal blocked (16 vCPU / 23 GiB host; PBF missing; 0 successful runs).
4. C-F6 - not merged to main; PR OPEN only; MCP TODO done:false; promotion not started; DoD incomplete.

## Ratings
- AccuracyRating: 0.45 (code-level F1-F3 and scratch packaging largely real; formal F0 clean-tip / F4 / F5 / F6 exit narrative does not survive re-verification)
- CompletenessRating: 0.85 (scratch suites/reports/tip/todo largely present; formal qualification and merge evidence complete as failures, not as successes)

## OverallVerdict
DISAGREE

Do not promote. Do not mark PHASE-MJOLNIRFRONTIER-001 done. Re-run hostile validation only after: clean tip (or intentionally tracked qualification artifacts), formal Nashville (not monaco proxy) with performance+memory+oracle, formal L48 on 32/64/1TiB with three successful pooled runs, merge to main after gates, MCP done:true with full DoD.
