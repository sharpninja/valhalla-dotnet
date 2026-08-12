#requires -Version 7
<#
.SYNOPSIS
  Operator runbook: complete F6 formal L48 + promotion + CLI promote after a compliant host exists.
.DESCRIPTION
  Run ON a machine with >=32 logical CPUs, >=64 GiB RAM, >=1024 GiB free disk,
  and us-lower48.osm.pbf (default E:\valhalla-qual\pbf\us-lower48.osm.pbf).
  Does NOT mark MCP done:true (agent does that after receipts exist).
#>
param(
  [string]$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path,
  [string]$PbfPath = 'E:\valhalla-qual\pbf\us-lower48.osm.pbf',
  [switch]$SkipOfficialBaseline,
  [switch]$DayStampOnly
)

$ErrorActionPreference = 'Stop'
Set-Location $RepoRoot

Write-Output "REPO=$RepoRoot HEAD=$((git rev-parse HEAD).Trim())"
Write-Output "PBF=$PbfPath"

if (-not $DayStampOnly) {
  $runner = Join-Path $RepoRoot 'build\Run-Lower48PooledQualification.Runner.ps1'
  $args = @('-File', $runner, '-PbfPath', $PbfPath)
  if ($SkipOfficialBaseline) { $args += '-SkipOfficialBaseline' }
  & pwsh.exe -NoProfile -NonInteractive @args
  if ($LASTEXITCODE -ne 0) { throw "Formal L48 Runner failed EXIT=$LASTEXITCODE" }
  $report = Join-Path $RepoRoot 'artifacts\qualification\lower48\l48-pooled-qualification-report.json'
  if (-not (Test-Path $report)) { throw "Missing L48 report: $report" }
  $status = (Get-Content $report -Raw | ConvertFrom-Json).verdict.status
  if ($status -ne 'formal-pass') { throw "L48 status=$status (need formal-pass)" }
  Write-Output "L48_FORMAL_PASS=true"
}

$promo = Join-Path $RepoRoot 'build\Run-PooledFrontierPromotionCampaign.ps1'
& pwsh.exe -NoProfile -NonInteractive -File $promo -PbfPath $PbfPath
if ($LASTEXITCODE -ne 0) { throw "Promotion campaign day stamp failed EXIT=$LASTEXITCODE" }

& pwsh.exe -NoProfile -NonInteractive -File $promo -CheckOnly
Write-Output 'NOTE: Repeat -DayStampOnly on 6 more consecutive UTC calendar days, then:'
Write-Output "  pwsh -File build\Promote-PooledFrontierCliDefault.ps1"
Write-Output 'Then agent: update MCP PHASE-MJOLNIRFRONTIER-001 done:true + full hostile AGREE.'
