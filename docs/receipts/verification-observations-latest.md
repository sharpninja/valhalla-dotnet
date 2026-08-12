# Verification plan 20260812T204333Z - F6 still blocked

## 1 Full Generation.Tests Debug+Release
- HEAD: 615ff23032283c6a9144bb9d27e76a52505ce875 clean
- Release: Passed!  - Failed:     0, Passed:   380, Skipped:     0, Total:   380, Duration: 35 s - SharpNinja.Valhalla.Generation.Tests.dll (net10.0)
- Debug: Passed!  - Failed:     0, Passed:   380, Skipped:     0, Total:   380, Duration: 37 s - SharpNinja.Valhalla.Generation.Tests.dll (net10.0)

## 2-3 Gate filters
- Passed!  - Failed:     0, Passed:    33, Skipped:     0, Total:    33, Duration: 22 s - SharpNinja.Valhalla.Generation.Tests.dll (net10.0)

## 4 Nashville + L48
- nashville: present experimental
- l48 status: blocked-formal-capacity (NOT formal-pass)

## 5 MCP done:true
- LIVE done=false (OBSERVATION FAILS for full goal - correct)
- Capture: todo-get-PHASE-MJOLNIRFRONTIER-001.yaml

## 6 main + CLI
- PR#1 on origin/main 1fffa26
- CLI default Legacy (ValhallaGenerationCli.cs:839)
- Capture: f6-main-and-cli.txt

## 7 Hostile full DoD AGREE
- honesty-blocker AGREE only (20260812T200100Z)
- Full F0-F6 DoD: NOT AGREE / not claimed

## Unlock probe 20260812T204333Z
- Azure: NotAllowed pending dues + ReadOnlyDisabledSubscription
- Local bar32/64/1024: False
- real_operator_approve: False
- UNLOCK_OPEN: False

## F6 checklist
- Merge: DONE
- 7-day promo / CLI promote / MCP done:true / full hostile: BLOCKED
