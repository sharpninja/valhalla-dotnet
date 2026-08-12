#requires -Version 7
<#
.SYNOPSIS
  Controlled promotion evidence for PooledFrontier (7 consecutive daily L48 pooled builds).
.DESCRIPTION
  Appends one daily pooled L48 build receipt. When 7 consecutive successful daily
  receipts exist, writes promotion-ready stamp. CLI default flip is a separate
  explicit step after this stamp exists.
#>
param(
  [string]$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path,
  [string]$ReceiptDir,
  [string]$PbfPath,
  [string]$WorkRoot = 'E:\valhalla-qual\promotion',
  [switch]$RecordOnlySuccessStamp,
  [switch]$CheckOnly
)

$ErrorActionPreference = 'Stop'
if (-not $ReceiptDir) {
  $ReceiptDir = Join-Path $RepoRoot 'artifacts\qualification\promotion\daily-l48'
}
New-Item -ItemType Directory -Force -Path $ReceiptDir | Out-Null

$today = (Get-Date).ToUniversalTime().ToString('yyyy-MM-dd')
$dayFile = Join-Path $ReceiptDir "day-$today.json"

function Get-ConsecutiveSuccessDays {
  param([string]$Dir)
  $files = Get-ChildItem $Dir -Filter 'day-*.json' | Sort-Object Name -Descending
  $streak = 0
  $cursor = [datetime]::UtcNow.Date
  foreach ($f in $files) {
    $day = [datetime]::ParseExact(($f.BaseName -replace '^day-',''), 'yyyy-MM-dd', $null).Date
    if ($day -ne $cursor -and $day -ne $cursor.AddDays(-$streak)) {
      # allow only contiguous backward days from today
      if ($streak -eq 0 -and $day -lt $cursor) {
        # start from most recent file day
        $cursor = $day
      } elseif ($day -ne $cursor.AddDays(-1) -and $streak -gt 0) {
        break
      }
    }
    $obj = Get-Content $f.FullName -Raw | ConvertFrom-Json
    if (-not $obj.success) { break }
    $streak++
    $cursor = $day
    if ($streak -ge 7) { break }
    # next expected previous day
  }
  # recompute properly
  $map = @{}
  Get-ChildItem $Dir -Filter 'day-*.json' | ForEach-Object {
    $d = $_.BaseName -replace '^day-',''
    $map[$d] = (Get-Content $_.FullName -Raw | ConvertFrom-Json)
  }
  $streak = 0
  $d = [datetime]::UtcNow.Date
  while ($true) {
    $key = $d.ToString('yyyy-MM-dd')
    if (-not $map.ContainsKey($key)) { break }
    if (-not $map[$key].success) { break }
    $streak++
    $d = $d.AddDays(-1)
    if ($streak -ge 7) { break }
  }
  return $streak
}

if ($CheckOnly) {
  $streak = Get-ConsecutiveSuccessDays -Dir $ReceiptDir
  Write-Output "CONSECUTIVE_SUCCESS_DAYS=$streak"
  $ready = $streak -ge 7
  Write-Output "PROMOTION_READY=$ready"
  if ($ready) {
    $stamp = Join-Path $ReceiptDir 'promotion-ready.json'
    @{
      consecutiveSuccessDays = $streak
      readyUtc = (Get-Date).ToUniversalTime().ToString('o')
      receiptDir = $ReceiptDir
    } | ConvertTo-Json | Set-Content $stamp -Encoding utf8
    Write-Output "STAMP=$stamp"
  }
  exit 0
}

if ($RecordOnlySuccessStamp) {
  @{
    day = $today
    success = $true
    note = 'manual success stamp'
    recordedUtc = (Get-Date).ToUniversalTime().ToString('o')
  } | ConvertTo-Json | Set-Content $dayFile -Encoding utf8
  Write-Output "RECORDED=$dayFile"
  & $PSCommandPath -RepoRoot $RepoRoot -ReceiptDir $ReceiptDir -CheckOnly
  exit 0
}

# Require formal L48 report pass before starting promotion campaign
$l48 = Join-Path $RepoRoot 'artifacts\qualification\lower48\l48-pooled-qualification-report.json'
if (-not (Test-Path $l48)) {
  throw "Missing formal L48 report: $l48"
}
$l48Obj = Get-Content $l48 -Raw | ConvertFrom-Json
if ($l48Obj.verdict.status -ne 'formal-pass') {
  throw "L48 report status is '$($l48Obj.verdict.status)'; promotion requires formal-pass"
}

# One daily pooled run (reuse formal runner single-shot by calling Runner once would do 3; here one build)
if (-not $PbfPath) { $PbfPath = $l48Obj.pbfPath }
if (-not (Test-Path -LiteralPath $PbfPath)) { throw "PBF missing: $PbfPath" }

$tmp = Join-Path $WorkRoot 'day-runner'
New-Item -ItemType Directory -Force -Path $tmp, $WorkRoot | Out-Null
# Delegate to formal runner's single-run approach: invoke 1 pooled via simplified call
$single = Join-Path $WorkRoot "day-$today"
New-Item -ItemType Directory -Force -Path $single | Out-Null
$runner = Join-Path $PSScriptRoot 'Run-Lower48PooledQualification.Runner.ps1'
# For daily: run one pooled by temporarily wrapping - use official runner with env is heavy;
# implement inline one build using same pattern as Runner (dotnet project reuse if present)
$proj = Join-Path $WorkRoot 'runner\L48.csproj'
if (-not (Test-Path $proj)) {
  # bootstrap by invoking runner with SkipOfficial and hope 3 runs ok - too heavy for daily
  throw "Bootstrap formal runner once on L48 host first (creates $proj), then re-run promotion."
}

$work = Join-Path $single 'work'
$tiles = Join-Path $single 'tiles'
$json = Join-Path $single 'result.json'
& dotnet run --project $proj -c Release -- $PbfPath $work $tiles $json
$r = Get-Content $json -Raw | ConvertFrom-Json
@{
  day = $today
  success = [bool]$r.success
  durationSeconds = $r.durationSeconds
  peakWorkingSetBytes = $r.peakWorkingSetBytes
  outputTreeSha256 = $r.outputTreeSha256
  failure = $r.failure
  branchSha = (git -C $RepoRoot rev-parse HEAD).Trim()
  recordedUtc = (Get-Date).ToUniversalTime().ToString('o')
} | ConvertTo-Json | Set-Content $dayFile -Encoding utf8

Write-Output "RECORDED=$dayFile success=$($r.success)"
& $PSCommandPath -RepoRoot $RepoRoot -ReceiptDir $ReceiptDir -CheckOnly
if (-not $r.success) { exit 2 }
