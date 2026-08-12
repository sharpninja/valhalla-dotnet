# Verification plan full re-run 20260812T203923Z

## 1. Full Generation.Tests Debug+Release
- HEAD: 1e36c6f84f591cfe72fc9df6babf6e760efab5f4
- git status porcelain: (clean)
- Release log contains Passed 380: True
- Debug log contains Passed 380: True
- Release tail:  | Passed!  - Failed:     0, Passed:   380, Skipped:     0, Total:   380, Duration: 35 s - SharpNinja.Valhalla.Generation.Tests.dll (net10.0) | 
- Debug tail:  | Passed!  - Failed:     0, Passed:   380, Skipped:     0, Total:   380, Duration: 37 s - SharpNinja.Valhalla.Generation.Tests.dll (net10.0) | 

## 2-3. Gate filters
- gate filter Passed 33: True
- tail: Passed!  - Failed:     0, Passed:    33, Skipped:     0, Total:    33, Duration: 22 s - SharpNinja.Valhalla.Generation.Tests.dll (net10.0) | 

## 4. Nashville + L48 reports
- nashville status/keys: campaign, pipeline, branchSha, pbfPath, pbfSha256, configPath, oracle, machine, runs, verdict, notes
- nashville verdict.status: experimental-performance-win-memory-uninstrumented-official
- l48 verdict.status: blocked-formal-capacity
- l48 machine: vCpu=16 mem=23
- l48 pbfPath: E:\valhalla-qual\pbf\us-lower48.osm.pbf
- OBSERVATION: L48 is blocked-formal-capacity NOT formal-pass (full DoD fail on this step)

## 5. MCP TODO done:true
- OBSERVATION FAILS for full goal: done=false (required until formal L48+promo or approved amendment)

## 6. main + CLI policy
- PR#1 in origin/main: True
- CLI default line: 
- OBSERVATION: Legacy default correct; promotion not done

## 7. Hostile full DoD AGREE
- Honesty-blocker AGREE: docs/hostile-validator-20260812T200100Z.md
- Full F0-F6 DoD claim set: NOT AGREE (would DISAGREE: no formal L48, no 7-day, CLI not promoted, MCP done false)

## Host / F6 promotion
- Azure: NotAllowed pending dues (see f6-gate-reprobe)
- Formal L48 runner: host bar fail-closed
- 7-day promo: not started (no formal-pass day stamps)
- Amendment draft INACTIVE awaiting APPROVE AMD-MJOLNIRFRONTIER-001-L48-DEFER

## Overall for 100% goal
INCOMPLETE: F5 formal L48 + F6 promo/CLI/MCP-done + full hostile AGREE blocked on operator host or approved amendment.
