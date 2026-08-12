# F6 formal L48 host blocker (updated)

TimestampUtc: 2026-08-12T19:55:00Z

## Host bar
LocalHost: PAYTON-LEGION2 (16 vCPU / 23.37 GiB / max free disk 1259 GiB) fails formal bar 32 vCPU / 64 GiB / 1024 GiB free.

## Azure
Subscription: Azure subscription 1 (f52f8b2f-8faa-4207-9adb-67fd64da9b8a)
CLI state field: Warned
Write actions: ReadOnlyDisabledSubscription (cannot create RG/VM; provider Microsoft.Compute NotRegistered)
Receipt: docs/receipts/f6-azure-try-latest.txt
Prior enable attempt: NotAllowed pending dues (see f6-azure-provision.log / f6-azure-retry)

## GCP
No credentialed accounts (gcloud auth list empty). Operator must gcloud auth login.

## PBF (READY)
E:\valhalla-qual\pbf\us-latest.osm.pbf = 12077262565 bytes
E:\valhalla-qual\pbf\us-lower48.osm.pbf = hardlink/same content
SHA256: A195FD9408BDD1599DD0BE81ED6DD521F5029557B409DFE6D22FBA983A73B2C3
Pointer: artifacts/us-lower48.osm.pbf.pointer.txt

## Formal runner fail-closed (re-run 2026-08-12T19:55Z)
build/Run-Lower48PooledQualification.Runner.ps1 throws:
  Formal L48 host bar not met: vCpu=16 memGiB=23 freeDiskGiB=1259 (need 32/64/1024)
PromotionGateTests Release: Passed 4 / Failed 0 (docs/receipts/f6-promotion-tests.log)

## Promotion
Scripts ready on branch tip:
- Run-Lower48PooledQualification.Runner.ps1
- Run-PooledFrontierPromotionCampaign.ps1 (7 consecutive calendar days)
- Promote-PooledFrontierCliDefault.ps1

7 consecutive daily L48 builds cannot complete in a single session wall-clock
without a formal L48 host. Calendar days are required by the promotion campaign.

## MCP
done remains false until formal-pass L48 + 7 daily stamps + CLI promote after stamp.

## Operator paths to unblock full DoD
1. Pay/re-enable Azure subscription, then provision 32 vCPU / 64 GiB / >=1 TiB free disk VM and attach or copy us-lower48.osm.pbf
2. Authenticate GCP (or provide another compliant host) with same bar + PBF
3. Explicit plan amendment deferring formal L48 + 7-day promotion from PHASE-MJOLNIRFRONTIER-001 DoD (operator-written only)
