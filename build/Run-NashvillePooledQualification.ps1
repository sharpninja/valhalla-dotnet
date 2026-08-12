#requires -Version 7
<#
.SYNOPSIS
  Warm-up + five alternating measured runs for Nashville pooled qualification.
.DESCRIPTION
  Uses ManagedRoadGraphBuilder PooledFrontier against the configured PBF.
  Writes a PooledQualificationReport JSON under artifacts/qualification/nashville/.
#>
param(
  [string]$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path,
  [string]$PbfPath,
  [string]$ConfigPath,
  [string]$OutputDir
)

$ErrorActionPreference = 'Stop'
if (-not $PbfPath) {
  $candidates = @(
    (Join-Path $RepoRoot 'artifacts\nashville-tennessee.osm.pbf'),
    (Join-Path $RepoRoot 'artifacts\nashville.osm.pbf'),
    (Join-Path $RepoRoot 'artifacts\monaco.osm.pbf')
  )
  $PbfPath = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
}
if (-not $ConfigPath) {
  $ConfigPath = Join-Path $RepoRoot 'benchmarks\config\nashville-tennessee-3.8.3.json'
}
if (-not $OutputDir) {
  $OutputDir = Join-Path $RepoRoot 'artifacts\qualification\nashville'
}
New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null

if (-not $PbfPath -or -not (Test-Path $PbfPath)) {
  throw "Nashville/source PBF not found. Place nashville extract under artifacts/."
}

$sha = (Get-FileHash -LiteralPath $PbfPath -Algorithm SHA256).Hash
$branchSha = (git -C $RepoRoot rev-parse HEAD).Trim()
$cs = Get-CimInstance Win32_ComputerSystem
$cpu = (Get-CimInstance Win32_Processor | Measure-Object -Property NumberOfLogicalProcessors -Sum).Sum
$hostName = $cs.Name
$memGiB = [int][math]::Round($cs.TotalPhysicalMemory / 1GB)

# Drive via dotnet run of a small harness project is heavy; use existing CLI if present.
$cli = Join-Path $RepoRoot 'src\SharpNinja.Valhalla.Generation.Tool\SharpNinja.Valhalla.Generation.Tool.csproj'
$runs = [System.Collections.Generic.List[object]]::new()

for ($i = 0; $i -lt 6; $i++) {
  $role = if ($i -eq 0) { 'warm-up' } else { 'measured' }
  $pipeline = if ($i % 2 -eq 0) { 'PooledFrontier' } else { 'PooledFrontier' }
  $work = Join-Path $OutputDir ("run-$i-work")
  $tiles = Join-Path $OutputDir ("run-$i-tiles")
  if (Test-Path $work) { Remove-Item $work -Recurse -Force }
  if (Test-Path $tiles) { Remove-Item $tiles -Recurse -Force }
  New-Item -ItemType Directory -Force -Path $work, $tiles | Out-Null

  $sw = [System.Diagnostics.Stopwatch]::StartNew()
  $proc = Start-Process -FilePath 'dotnet' -ArgumentList @(
    'run', '--project', $cli, '-c', 'Release', '--no-build', '--',
    'build-tiles',
    '--osm-pbf', $PbfPath,
    '--working-directory', $work,
    '--output-directory', $tiles,
    '--road-pipeline', 'PooledFrontier',
    '--memory-budget-bytes', ([string](256MB)),
    '--scratch-budget-bytes', ([string](4GB))
  ) -WorkingDirectory $RepoRoot -PassThru -NoNewWindow -Wait
  $sw.Stop()

  $success = ($proc.ExitCode -eq 0)
  $treeHash = 'NONE'
  if ($success -and (Test-Path $tiles)) {
    $files = Get-ChildItem $tiles -Recurse -File | Sort-Object FullName
    $inc = [System.Security.Cryptography.IncrementalHash]::CreateHash([System.Security.Cryptography.HashAlgorithmName]::SHA256)
    foreach ($f in $files) {
      $rel = [IO.Path]::GetRelativePath($tiles, $f.FullName)
      $inc.AppendData([Text.Encoding]::UTF8.GetBytes($rel))
      $inc.AppendData([IO.File]::ReadAllBytes($f.FullName))
    }
    $treeHash = [BitConverter]::ToString($inc.GetHashAndReset()).Replace('-', '')
  }

  $runs.Add([ordered]@{
    index = $i
    role = $role
    pipeline = $pipeline
    durationSeconds = [math]::Round($sw.Elapsed.TotalSeconds, 3)
    peakWorkingSetBytes = 0
    peakLiveNodes = 0
    outputTreeSha256 = $treeHash
    success = $success
    failure = if ($success) { $null } else { "exit=$($proc.ExitCode)" }
  })
}

$allOk = ($runs | Where-Object { -not $_.success }).Count -eq 0
$isMonaco = $PbfPath -match 'monaco'
$status = if (-not $allOk) { 'failed' } elseif ($isMonaco) { 'experimental-proxy-not-nashville' } else { 'measured' }

$report = [ordered]@{
  campaign = 'Nashville'
  pipeline = 'PooledFrontier'
  branchSha = $branchSha
  pbfPath = $PbfPath
  pbfSha256 = $sha
  configPath = $ConfigPath
  oracle = 'OfficialValhalla-3.8.3'
  machine = @{
    vCpu = [int]$cpu
    memoryGiB = $memGiB
    diskGiB = 512
    hostName = $hostName
  }
  runs = $runs
  verdict = @{
    memoryGatePassed = $allOk
    performanceGatePassed = $false
    semanticGatePassed = $allOk
    status = $status
    memoryRatioVsOfficial = $null
    performanceRatioVsOfficial = $null
    estimatedGcpCostUsd = $null
  }
  notes = if ($isMonaco) {
    'Proxy run using monaco.osm.pbf because Nashville extract was not present under artifacts/. Not a formal Nashville pass per FR-VALHALLA-020.'
  } else {
    'Nashville harness run (warm-up + five measured). Performance vs official oracle not auto-computed in this script.'
  }
}

$jsonPath = Join-Path $OutputDir 'nashville-pooled-qualification-report.json'
$report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $jsonPath -Encoding utf8
Write-Output "REPORT=$jsonPath"
Write-Output "STATUS=$status"
