# Hostile Validator Receipt

- TimestampUtc: 2026-08-12T20:01:00Z
- ValidatorIdentity: GrokSubagentHostile
- Workspace: F:\GitHub\TruckMate
- WorktreeUnderReview: F:\GitHub\valhalla-dotnet\.worktrees\pooled-node-frontier
- Scope: honest-blocker set (NOT full DoD)
- Method: independent re-verification with PowerShell tools only; plan checkboxes and prior implementer receipts not trusted as evidence

## Claims reviewed

### C1: Worktree tip f7f4b46+ and f6-operator-blocker content
- Verdict: PASS
- Evidence:
  - `git rev-parse HEAD` => `f7f4b468397d18efc71850cd78202b7ea712aae6` (short f7f4b46)
  - `git branch --show-current` => `codex/pooled-node-frontier`
  - `git rev-list --count f7f4b46..HEAD` => 0 (exactly at tip; not older)
  - File present: `docs/receipts/f6-operator-blocker.md`
  - Contains: ReadOnlyDisabledSubscription; local 16 vCPU / 23.37 GiB fail vs 32/64/1024; `## PBF (READY)`; `done remains false` under `## MCP`

### C2: l48-pooled-qualification-report.json blocked-formal-capacity
- Verdict: PASS
- Evidence (live file read):
  - `verdict.status` = `blocked-formal-capacity` (not formal-pass)
  - `machine.vCpu` = 16; `machine.memoryGiB` = 23
  - `pbfPath` = `E:\valhalla-qual\pbf\us-lower48.osm.pbf`
  - All three `runs[].success` = false with host-bar failure text `need 32/64/1024`

### C3: Live PBF size and SHA256
- Verdict: PASS
- Evidence (live probe, not receipt copy):
  - Path exists: `E:\valhalla-qual\pbf\us-lower48.osm.pbf` (HardLink)
  - Length = 12077262565
  - `Get-FileHash -Algorithm SHA256` = `A195FD9408BDD1599DD0BE81ED6DD521F5029557B409DFE6D22FBA983A73B2C3`
  - Pointer `artifacts/us-lower48.osm.pbf.pointer.txt` matches size and sha256

### C4: Azure write blocked; local host fails formal bar
- Verdict: PASS
- Evidence:
  - `az account show`: subscription `f52f8b2f-8faa-4207-9adb-67fd64da9b8a` state `Warned`
  - Live `az group create` failed EXIT=1 with `(ReadOnlyDisabledSubscription)`
  - Host probe: LogicalProcessors=16; TotalPhysicalMemoryGiB=23.37; max free disk E: 1259.68 GiB
  - Formal bar 32/64/1024: vCpu FAIL, mem FAIL, disk alone would pass; overall host fails formal bar

### C5: PromotionGateTests Release pass Failed=0
- Verdict: PASS
- Evidence (hostile re-run, not implementer log alone):
  - Command: `dotnet test tests\SharpNinja.Valhalla.Generation.Tests\SharpNinja.Valhalla.Generation.Tests.csproj -c Release --filter FullyQualifiedName~PromotionGate --nologo -v minimal`
  - Result: `Passed!  - Failed:     0, Passed:     4, Skipped:     0, Total:     4` EXIT=0
  - Log: `docs/receipts/hostile-promotion-gate-20260812T195800Z.log`
  - Corroborating prior implementer log `docs/receipts/f6-promotion-tests.log` also Failed 0 / Passed 4 (not sole evidence)

### C6: CLI default remains Legacy
- Verdict: PASS
- Evidence:
  - File: `src/SharpNinja.Valhalla.Generation.Tool/ValhallaGenerationCli.cs`
  - Lines 836-839: `AddDefault(options, "road-pipeline", ManagedRoadGraphPipeline.Legacy.ToString());`
  - No default flip to PooledFrontier observed in AddDefault block

### C7: MCP TODO PHASE-MJOLNIRFRONTIER-001 done:false
- Verdict: PASS
- Evidence (live API after health nonce check):
  - Marker: `F:\GitHub\TruckMate\AGENTS-README-FIRST.yaml` baseUrl `http://PAYTON-LEGION2:7147`
  - Health nonce echo matched (MCP trusted for this query)
  - GET `/mcpserver/todo/PHASE-MJOLNIRFRONTIER-001` with X-Api-Key
  - Response field `done`: false
  - `completedDate`: null

### C8: Operator blocker does not claim full PLAN-FINISH / full DoD
- Verdict: PASS
- Evidence from `docs/receipts/f6-operator-blocker.md`:
  - Title/frame is host blocker, not completion claim
  - Explicit: `done remains false until formal-pass L48 + 7 daily stamps + CLI promote after stamp`
  - Section `## Operator paths to unblock full DoD` lists three unblock options (Azure re-enable, GCP/other host, operator plan amendment)
  - No assertion that full PLAN-FINISH or full DoD is complete

## Explicit FAIL list
- (none)

## OverallVerdict
AGREE

## Notes
- Scope limited to honest-blocker claims only. This AGREE does not certify full PHASE-MJOLNIRFRONTIER-001 DoD, 7-day promotion, CLI promote, or formal L48 pass.
- Prefer DISAGREE when uncertain rule was not triggered: every listed claim re-verified with live tools.
