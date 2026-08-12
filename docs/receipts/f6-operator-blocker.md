# F6 formal L48 host blocker (updated)

TimestampUtc: 2026-08-12T19:22:21.4962396Z

## Host bar
LocalHost: PAYTON-LEGION2 (16 vCPU / 23.4 GiB) fails 32/64/1TiB bar.

## Azure
Subscription: Azure subscription 1 (f52f8b2f-8faa-4207-9adb-67fd64da9b8a)
State: Disabled / Warned
Enable attempt: NotAllowed - subscription has pending dues; make required payments and retry
(receipt: az-enable2.txt / f6-azure-retry)

## GCP
No credentialed accounts (gcloud auth login required).

## PBF
Staging download of Geofabrik us-latest.osm.pbf (~11.24 GiB) to E:\valhalla-qual\pbf\
(for use when a formal host exists; harness also checks that path).

## Promotion
Scripts ready on branch 4245639+:
- Run-Lower48PooledQualification.Runner.ps1
- Run-PooledFrontierPromotionCampaign.ps1 (7 consecutive calendar days)
- Promote-PooledFrontierCliDefault.ps1

7 consecutive daily L48 builds cannot complete in a single session wall-clock
without a formal L48 host. Calendar days are required by the promotion campaign.

## MCP
done remains false until formal-pass L48 + 7 daily stamps + CLI promote after stamp.
