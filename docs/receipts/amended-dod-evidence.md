# Amended DoD evidence bundle

TimestampUtc: 20260812T220139Z
Amendment: AMD-MJOLNIRFRONTIER-001-L48-DEFER ACTIVE (operator APPROVE)
Tip: dcc7ee101bc1d8fa083192c59788742268d54274
Branch: codex/pooled-node-frontier

## Required under amendment
- F0 suites: Release 380/380 EXIT0; Debug 380/380 EXIT0 (docs/receipts/f0-f3-full-suite-*.log)
- F1-F3: prior gate filter 33/33 + code on main via PR1
- F4 Nashville: docs/receipts/nashville-pooled-qualification-report.json present experimental
- F5 formal L48: DEFERRED; receipt status blocked-formal-capacity (not formal-pass)
- F6 merge: PR #1 1fffa26 on origin/main
- CLI: Legacy (ValhallaGenerationCli.cs:839)
- Harness: Runner + PromotionCampaign + Promote CLI + Complete-F6FormalHostChain present
- Lab policy: PAYTON-LEGION2/DESKTOP + Octopus + GitHub; Azure not used
