#requires -Version 7
<#
.SYNOPSIS
  Formal Lower-48 pooled qualification harness (32 vCPU / 64 GiB / 1 TiB bar).
.DESCRIPTION
  Refuses to emit a formal-pass report unless machine meets the formal bar and
  us-lower48 (or configured) PBF exists. Runs three pooled builds + records
  baseline slot for official Valhalla. Never treats Nashville metrics as L48.
#>
param(
  [string]$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path,
  [string]$PbfPath,
  [string]$OutputDir,
  [int]$RequiredVCpu = 32,
  [int]$RequiredMemoryGiB = 64,
  [int]$RequiredDiskGiB = 1024,
  [switch]$AllowBlockedReport
)

$ErrorActionPreference = 'Stop'
if (-not $OutputDir) {
  $OutputDir = Join-Path $RepoRoot 'artifacts\qualification\lower48'
}
New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null

$cs = Get-CimInstance Win32_ComputerSystem
$cpu = [int](Get-CimInstance Win32_Processor | Measure-Object NumberOfLogicalProcessors -Sum).Sum
$memGiB = [int][math]::Round($cs.TotalPhysicalMemory / 1GB)
# Free disk max across fixed drives
$maxFreeGiB = 0
Get-PSDrive -PSProvider FileSystem | ForEach-Object {
  if ($null -ne $_.Free) {
    $g = [int][math]::Floor($_.Free / 1GB)
    if ($g -gt $maxFreeGiB) { $maxFreeGiB = $g }
  }
}

if (-not $PbfPath) {
  $candidates = @(
    (Join-Path $RepoRoot 'artifacts\us-lower48.osm.pbf'),
    (Join-Path $RepoRoot 'artifacts\us-latest.osm.pbf'),
    (Join-Path $RepoRoot 'artifacts\north-america-latest.osm.pbf')
  )
  $PbfPath = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
}

$branchSha = (git -C $RepoRoot rev-parse HEAD).Trim()
$meetsBar = ($cpu -ge $RequiredVCpu) -and ($memGiB -ge $RequiredMemoryGiB) -and ($maxFreeGiB -ge $RequiredDiskGiB)
$pbfOk = $PbfPath -and (Test-Path -LiteralPath $PbfPath)

if (-not $meetsBar -or -not $pbfOk) {
  $runs = @(0,1,2 | ForEach-Object {
    [ordered]@{
      index = $_
      role = 'measured'
      pipeline = 'PooledFrontier'
      durationSeconds = 0
      peakWorkingSetBytes = 0
      peakLiveNodes = 0
      outputTreeSha256 = 'NONE'
      success = $false
      failure = if (-not $meetsBar) {
        "host bar not met: vCpu=$cpu memGiB=$memGiB freeDiskGiB=$maxFreeGiB (need $RequiredVCpu/$RequiredMemoryGiB/$RequiredDiskGiB)"
      } else {
        'us-lower48.osm.pbf (or configured PBF) missing'
      }
    }
  })
  $report = [ordered]@{
    campaign = 'Lower48'
    pipeline = 'PooledFrontier'
    branchSha = $branchSha
    pbfPath = if ($PbfPath) { $PbfPath } else { 'NOT_PRESENT' }
    pbfSha256 = 'NONE'
    configPath = 'benchmarks/config/lower48-3.8.3.json'
    oracle = 'OfficialValhalla-3.8.3'
    machine = @{
      vCpu = $cpu
      memoryGiB = $memGiB
      diskGiB = $maxFreeGiB
      hostName = $env:COMPUTERNAME
    }
    runs = $runs
    verdict = @{
      memoryGatePassed = $false
      performanceGatePassed = $false
      semanticGatePassed = $false
      status = 'blocked-formal-capacity-and-pbf-missing'
      memoryRatioVsOfficial = $null
      performanceRatioVsOfficial = $null
      estimatedGcpCostUsd = $null
    }
    notes = 'Formal L48 refused. Desktop/capacity probes are not substitutes. No Nashville extrapolation.'
  }
  $jsonPath = Join-Path $OutputDir 'l48-pooled-qualification-report.json'
  $report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $jsonPath -Encoding utf8
  Write-Output "REPORT=$jsonPath"
  Write-Output "STATUS=blocked"
  if (-not $AllowBlockedReport) {
    exit 2
  }
  exit 0
}

# Formal path: three pooled runs (implementation uses ManagedRoadGraphBuilder via companion runner)
throw "Formal L48 runner requires companion Program host on compliant machine; bar met but interactive runner not invoked in this script path. Use build/Run-Lower48PooledQualification.Runner when present."
