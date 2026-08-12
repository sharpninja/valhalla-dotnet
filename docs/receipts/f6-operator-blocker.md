# F6 formal L48 host blocker (lab-only policy)

TimestampUtc: 20260812T215155Z
Tip: 6ee5c4465971d3c365234fce2668b8a5f6e58e51

## Lab topology (operator-stated 2026-08-12)
- Deploy hosts: PAYTON-LEGION2 and PAYTON-DESKTOP only
- Deploy orchestrator: Octopus
- Source origin: GitHub
- GitHub Actions: not used
- Azure: not involved (do not provision Azure VMs or treat Azure as a host path)

## Formal L48 bar
32 vCPU / 64 GiB RAM / >=1024 GiB free disk + us-lower48.osm.pbf

## PAYTON-LEGION2 (this agent host)
- cpu=16 memGiB=23.37 freeDiskGiB~1259
- bar32_64_1024=False (CPU and RAM fail; disk alone would pass)
- PBF READY: E:\valhalla-qual\pbf\us-lower48.osm.pbf (12077262565 B, sha256 A195FD9408BDD1599DD0BE81ED6DD521F5029557B409DFE6D22FBA983A73B2C3)
- Docker: CPUs 16 / Total Memory 11.36 GiB (also under bar)

## PAYTON-DESKTOP
- Ping: True (192.168.1.77)
- WinRM/CIM/Invoke-Command: fail (0x8009030e Negotiate / logon session)
- SSH 22: timeout; SSH 2222: connection reset
- Admin shares C$/D$/E$: False
- Cannot measure CPU/RAM/disk or start formal L48 from LEGION2 without operator remoting fix or local run on DESKTOP

## Scripts ready (lab host with bar met)
- build/Run-Lower48PooledQualification.Runner.ps1
- build/Run-PooledFrontierPromotionCampaign.ps1
- build/Promote-PooledFrontierCliDefault.ps1
- build/Complete-F6FormalHostChain.ps1

## Operator unlock paths (Azure removed)
1. On a lab host that meets 32/64/1024 (likely PAYTON-DESKTOP if it qualifies): run formal L48 chain with staged PBF (copy or share E:\valhalla-qual\pbf if needed)
2. Enable agent remoting from LEGION2 to DESKTOP (WinRM TrustedHosts + credentials, or working SSH) so agent can measure and run formal L48 there
3. Reply exactly: APPROVE AMD-MJOLNIRFRONTIER-001-L48-DEFER to defer formal L48 + 7-day promo from phase DoD (CLI stays Legacy)

## MCP
done remains false until formal-pass L48 + 7 daily stamps + CLI promote after stamp, or approved amendment.

## MCP marker
AGENTS-README-FIRST.yaml missing on TruckMate root at 20260812T215313Z; live TODO update not applied. Intended remaining is lab-only unlock list above.

