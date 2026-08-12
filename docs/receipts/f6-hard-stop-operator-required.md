# F6 hard stop - operator required

TimestampUtc: 20260812T203551Z
BranchTip: f8e79c2a647e16d8edc51982cbf44753806db45f
Worktree: F:\GitHub\valhalla-dotnet\.worktrees\pooled-node-frontier

## Goal AC blocked
PLAN-FINISH F5/F6 and product plan section 14 DoD require:
- Lower-48 qualification passes (32 vCPU / 64 GiB / 1 TiB + formal-pass report)
- Seven consecutive daily Lower-48 pooled builds
- CLI default flip only after promotion-ready stamp
- MCP PHASE-MJOLNIRFRONTIER-001 done:true only with full DoD evidence
- Hostile AGREE on full completion claim set

## Live blocker (re-probed)
- Azure enable: NotAllowed - subscription has pending dues (f6-op-reprobe-latest.txt)
- Azure RG create: ReadOnlyDisabledSubscription
- GCP: no credentialed accounts
- Local PAYTON-LEGION2: 16 vCPU / 23.37 GiB (fails formal bar)
- PAYTON-DESKTOP: pingable; WinRM/SSH/admin share unusable without operator remoting setup
- PBF: READY E:\valhalla-qual\pbf\us-lower48.osm.pbf (12077262565 B, SHA256 A195FD9408BDD1599DD0BE81ED6DD521F5029557B409DFE6D22FBA983A73B2C3)

## Agent-side complete (not full DoD)
- PR #1 merged origin/main 1fffa26
- Generation.Tests Debug+Release 380/380 on tip
- Promotion scripts + Complete-F6FormalHostChain.ps1 runbook
- CLI still Legacy (correct)
- MCP done:false (correct)
- Hostile honesty-blocker AGREE 20260812T200100Z (not full DoD)

## Operator actions that unlock full DoD
1. Pay Azure pending dues and re-enable subscription, then allow agent to provision 32/64/1TiB VM
2. Provide gcloud auth or another host meeting the formal bar
3. Write an explicit plan amendment deferring formal L48 + 7-day promo from PHASE DoD (operator-authored)

## Explicit non-actions by agent
- Will not fabricate formal-pass L48 report
- Will not flip CLI default without promotion-ready stamp
- Will not set MCP done:true without full DoD
- Will not claim full hostile AGREE on incomplete F0-F6 exits
