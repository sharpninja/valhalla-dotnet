# F6 formal L48 host blocker

TimestampUtc: 2026-08-12T19:05:59.7963927Z
LocalHost: PAYTON-LEGION2 (16 vCPU / 23.4 GiB) — fails 32/64/1TiB bar
AzureSubscription: Azure subscription 1 (f52f8b2f-8faa-4207-9adb-67fd64da9b8a)
AzureState: Warned / ReadOnlyDisabledSubscription — cannot create RG or VM
GCP: no credentialed accounts (gcloud auth login required)
us-lower48 PBF: not found on local disks
PAYTON-DESKTOP: ping OK, WinRM auth failed

Promotion infrastructure committed:
- build/Run-Lower48PooledQualification.Runner.ps1
- build/Run-PooledFrontierPromotionCampaign.ps1
- build/Promote-PooledFrontierCliDefault.ps1
- tests/.../PromotionGateTests.cs

MCP done:true is blocked until formal-pass L48 + 7 consecutive daily promotion stamps + CLI promote after stamp.
