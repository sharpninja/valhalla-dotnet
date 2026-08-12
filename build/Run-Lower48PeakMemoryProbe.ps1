#requires -Version 7
param(
  [string]$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path,
  [string]$PbfPath = 'E:\valhalla-qual\pbf\us-lower48.osm.pbf',
  [string]$WorkRoot = 'E:\valhalla-qual\lower48-peak',
  [int]$MaxDop = 4,
  [long]$MemoryBudgetGiB = 12,
  [long]$ScratchBudgetGiB = 800,
  [int]$RunCount = 1,
  [int]$HeartbeatSeconds = 10
)
$ErrorActionPreference = 'Stop'
if (-not (Test-Path -LiteralPath $PbfPath)) { throw "PBF missing: $PbfPath" }
$cpu = [int](Get-CimInstance Win32_Processor | Measure-Object NumberOfLogicalProcessors -Sum).Sum
$memGiB = [math]::Round((Get-CimInstance Win32_ComputerSystem).TotalPhysicalMemory / 1GB, 2)
$hostName = $env:COMPUTERNAME
New-Item -ItemType Directory -Force -Path $WorkRoot | Out-Null
$outDir = Join-Path $RepoRoot 'artifacts\qualification\lower48'
$receiptDir = Join-Path $RepoRoot 'docs\receipts'
New-Item -ItemType Directory -Force -Path $outDir,$receiptDir | Out-Null
$branchSha = (git -C $RepoRoot rev-parse HEAD).Trim()
$pbfSha = (Get-FileHash -LiteralPath $PbfPath -Algorithm SHA256).Hash
$pbfSize = (Get-Item -LiteralPath $PbfPath).Length
$stamp = (Get-Date).ToUniversalTime().ToString('yyyyMMddTHHmmssZ')
$tmp = Join-Path $WorkRoot ("runner-" + $stamp)
New-Item -ItemType Directory -Force -Path $tmp | Out-Null
$csproj = @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup>
  <ItemGroup><ProjectReference Include="__REPO__\src\SharpNinja.Valhalla.Generation\SharpNinja.Valhalla.Generation.csproj" /></ItemGroup>
</Project>
"@
$csproj = $csproj.Replace('__REPO__', $RepoRoot)
Set-Content (Join-Path $tmp 'L48Peak.csproj') -Value $csproj -Encoding utf8
Copy-Item (Join-Path $PSScriptRoot 'L48PeakMemoryProgram.cs') (Join-Path $tmp 'Program.cs') -Force
$memBudget = [long]$MemoryBudgetGiB * 1GB
$scratchBudget = [long]$ScratchBudgetGiB * 1GB
$runs = [System.Collections.Generic.List[object]]::new()
Write-Output ("PROBE_HOST host={0} cpu={1} memGiB={2} heartbeatSec={3}" -f $hostName,$cpu,$memGiB,$HeartbeatSeconds)
for ($i = 0; $i -lt $RunCount; $i++) {
  Write-Output ("L48_PEAK_RUN_BEGIN index={0} dop={1} memBudgetGiB={2} heartbeatSec={3}" -f $i,$MaxDop,$MemoryBudgetGiB,$HeartbeatSeconds)
  $work = Join-Path $WorkRoot ("run-{0}-{1}-work" -f $i,$stamp)
  $tiles = Join-Path $WorkRoot ("run-{0}-{1}-tiles" -f $i,$stamp)
  $json = Join-Path $WorkRoot ("run-{0}-{1}.json" -f $i,$stamp)
  & dotnet run --project (Join-Path $tmp 'L48Peak.csproj') -c Release --no-launch-profile -- $PbfPath $work $tiles $json $memBudget $scratchBudget $MaxDop $HeartbeatSeconds
  $exit = $LASTEXITCODE
  if (-not (Test-Path $json)) { throw "Missing run json: $json exit=$exit" }
  $r = Get-Content $json -Raw | ConvertFrom-Json
  $runs.Add([ordered]@{
    index = $i; role = 'measured'; pipeline = 'PooledFrontier'
    durationSeconds = $r.durationSeconds
    peakIntermediateMemoryBytes = $r.peakIntermediateMemoryBytes
    peakWorkingSetBytes = $r.peakWorkingSetBytes
    peakPrivateBytes = $r.peakPrivateBytes
    peakGcHeapBytes = $r.peakGcHeapBytes
    resourceMetricsHighWaterMarkBytes = $r.resourceMetricsHighWaterMarkBytes
    scratchHighWaterMarkBytes = $r.scratchHighWaterMarkBytes
    selectedDop = $r.selectedDop; tileCount = $r.tileCount
    outputTreeSha256 = $r.outputTreeSha256; success = [bool]$r.success
    failure = $r.failure; resourceMetrics = $r.resourceMetrics; processExit = $exit
  })
  Write-Output ("L48_PEAK_RUN_END index={0} success={1} peakWS={2} peakInter={3} tiles={4} sec={5}" -f $i,$r.success,$r.peakWorkingSetBytes,$r.peakIntermediateMemoryBytes,$r.tileCount,$r.durationSeconds)
}
$peakWs = ($runs | Measure-Object -Property peakWorkingSetBytes -Maximum).Maximum
$peakInter = ($runs | Measure-Object -Property peakIntermediateMemoryBytes -Maximum).Maximum
$anyOk = [bool]($runs | Where-Object { $_.success } | Select-Object -First 1)
$report = [ordered]@{
  campaign = 'Lower48PeakMemory'; pipeline = 'PooledFrontier'
  purpose = 'GCP cost projection inputs: full us-lower48 pooled peak memory'
  branchSha = $branchSha; pbfPath = $PbfPath; pbfSha256 = $pbfSha; pbfSizeBytes = $pbfSize
  machine = [ordered]@{ hostName = $hostName; vCpu = $cpu; memoryGiB = $memGiB; freeDiskGiBAtStart = [int][math]::Floor(((Get-PSDrive E).Free)/1GB) }
  config = [ordered]@{ maxDopRequested = $MaxDop; memoryBudgetGiB = $MemoryBudgetGiB; scratchBudgetGiB = $ScratchBudgetGiB; heartbeatSeconds = $HeartbeatSeconds; intermediateStorage = 'MemoryMapped'; gridDivisions = 8; hierarchy = $false; shortcuts = $false }
  runs = $runs
  peaks = [ordered]@{ maxPeakWorkingSetBytes = $peakWs; maxPeakWorkingSetGiB = [math]::Round($peakWs/1GB,3); maxPeakIntermediateMemoryBytes = $peakInter; maxPeakIntermediateMemoryGiB = [math]::Round($peakInter/1GB,3) }
  verdict = [ordered]@{ status = $(if($anyOk){'lab-capacity-measurement-success'}else{'lab-capacity-measurement-failed'}); formalBarMet = ($cpu -ge 32 -and $memGiB -ge 64); formalPass = $false; notes = 'Lab peak-memory measurement for GCP sizing. HEARTBEAT lines in log are progress-only and do not alter peak capture.' }
  generatedUtc = (Get-Date).ToUniversalTime().ToString('o')
}
$reportJson = $report | ConvertTo-Json -Depth 12
$reportPath = Join-Path $outDir ("l48-peak-memory-report-{0}.json" -f $stamp)
$reportJson | Set-Content $reportPath -Encoding utf8
$reportJson | Set-Content (Join-Path $receiptDir ("l48-peak-memory-report-{0}.json" -f $stamp)) -Encoding utf8
$reportJson | Set-Content (Join-Path $receiptDir 'l48-peak-memory-report-latest.json') -Encoding utf8
$reportJson | Set-Content (Join-Path $outDir 'l48-peak-memory-report-latest.json') -Encoding utf8
Write-Output ("REPORT={0}" -f $reportPath)
Write-Output ("PEAK_WS_GIB={0}" -f [math]::Round($peakWs/1GB,3))
Write-Output ("PEAK_INTER_GIB={0}" -f [math]::Round($peakInter/1GB,3))
Write-Output ("ANY_OK={0}" -f $anyOk)
if (-not $anyOk) { exit 2 }
