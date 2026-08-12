# Verification plan under ACTIVE amendment 20260812T220231Z

## Scope
AMD-MJOLNIRFRONTIER-001-L48-DEFER ACTIVE (operator APPROVE). Formal L48 + 7-day promo + CLI flip deferred.

## 1 Full suites
- HEAD: 12386016aeabe0d9a254b411bb59f23bf6ccada9
- Release: Passed!  - Failed:     0, Passed:   380, Skipped:     0, Total:   380, Duration: 51 s - SharpNinja.Valhalla.Generation.Tests.dll (net10.0)
- Debug: Passed!  - Failed:     0, Passed:   380, Skipped:     0, Total:   380, Duration: 47 s - SharpNinja.Valhalla.Generation.Tests.dll (net10.0)

## 2-3 Gate filters
- Prior gate filter log: Passed!  - Failed:     0, Passed:    33, Skipped:     0, Total:    33, Duration: 22 s - SharpNinja.Valhalla.Generation.Tests.dll (net10.0)

## 4 Reports
- nashville: present experimental
- l48: blocked-formal-capacity (expected non-formal-pass under amendment)

## 5 MCP done:true
- LIVE done=true under amendment (todo-get-PHASE-MJOLNIRFRONTIER-001.yaml)
- doneSummary cites AMD-MJOLNIRFRONTIER-001-L48-DEFER

## 6 main+CLI
- PR1 on origin/main
- CLI Legacy (flip deferred)

## 7 Hostile amended DoD AGREE
- OverallVerdict AGREE: docs/hostile-validator-20260812T220524Z.md (PASS 10 / FAIL 0)

## F6 under amendment
- Merge: DONE
- Formal L48/promo/CLI: DEFERRED
- Phase closed under amended DoD when hostile AGREE

