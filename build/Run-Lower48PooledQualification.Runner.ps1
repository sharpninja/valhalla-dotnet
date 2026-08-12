#requires -Version 7
<#
.SYNOPSIS
  Formal Lower-48 pooled qualification when host bar is met.
.DESCRIPTION
  Requires 32 vCPU / 64 GiB / 1 TiB free disk and us-lower48 PBF.
  Runs three pooled ManagedRoadGraphBuilder builds + optional official docker baseline.
  Writes artifacts/qualification/lower48/l48-pooled-qualification-report.json
#>
param(
  [string]$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path,
  [string]$PbfPath,
  [string]$WorkRoot = 'E:\valhalla-qual\lower48',
  [string]$OfficialImage = 'ghcr.io/valhalla/valhalla:3.8.3',
  [switch]$SkipOfficialBaseline
)

$ErrorActionPreference = 'Stop'
$RequiredVCpu = 32
$RequiredMemoryGiB = 64
$RequiredDiskGiB = 1024

$cs = Get-CimInstance Win32_ComputerSystem
$cpu = [int](Get-CimInstance Win32_Processor | Measure-Object NumberOfLogicalProcessors -Sum).Sum
$memGiB = [int][math]::Round($cs.TotalPhysicalMemory / 1GB)
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
    (Join-Path $RepoRoot 'artifacts\us-latest.osm.pbf')
  )
  $PbfPath = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
}

$meets = ($cpu -ge $RequiredVCpu) -and ($memGiB -ge $RequiredMemoryGiB) -and ($maxFreeGiB -ge $RequiredDiskGiB)
if (-not $meets) {
  throw "Formal L48 host bar not met: vCpu=$cpu memGiB=$memGiB freeDiskGiB=$maxFreeGiB (need $RequiredVCpu/$RequiredMemoryGiB/$RequiredDiskGiB)"
}
if (-not $PbfPath -or -not (Test-Path -LiteralPath $PbfPath)) {
  throw 'Formal L48 requires us-lower48.osm.pbf (or configured -PbfPath).'
}

New-Item -ItemType Directory -Force -Path $WorkRoot | Out-Null
$branchSha = (git -C $RepoRoot rev-parse HEAD).Trim()
$pbfSha = (Get-FileHash -LiteralPath $PbfPath -Algorithm SHA256).Hash
$outDir = Join-Path $RepoRoot 'artifacts\qualification\lower48'
New-Item -ItemType Directory -Force -Path $outDir | Out-Null

# Build small runner project once
$tmp = Join-Path $WorkRoot 'runner'
New-Item -ItemType Directory -Force -Path $tmp | Out-Null
@"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup>
  <ItemGroup><ProjectReference Include="$RepoRoot\src\SharpNinja.Valhalla.Generation\SharpNinja.Valhalla.Generation.csproj" /></ItemGroup>
</Project>
"@ | Set-Content (Join-Path $tmp 'L48.csproj')
@'
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using SharpNinja.Valhalla.Generation;
using SharpNinja.Valhalla.Generation.Roads;
using SharpNinja.Valhalla.Mjolnir;

var pbf = args[0];
var work = args[1];
var tiles = args[2];
var outJson = args[3];
if (Directory.Exists(work)) Directory.Delete(work, true);
if (Directory.Exists(tiles)) Directory.Delete(tiles, true);
var sw = Stopwatch.StartNew();
bool ok = true; string? fail = null; string hash = "NONE"; long peak = 0; int tileCount = 0;
try {
  var r = await new ManagedRoadGraphBuilder().BuildAsync(new ManagedRoadGraphBuildRequest(
    [pbf], work, tiles, IntermediateStorageMode.MemoryMapped,
    48L*1024*1024*1024, 800L*1024*1024*1024,
    new TileBuilderConfig { GridDivisions = 8, Hierarchy = false, Shortcuts = false, MaxDegreeOfParallelism = 16 })
  { Pipeline = ManagedRoadGraphPipeline.PooledFrontier });
  ok = r.TileBuilderResult.Success;
  peak = r.PeakIntermediateMemoryBytes;
  tileCount = r.TileBuilderResult.TileCount;
  using var inc = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
  foreach (var f in Directory.GetFiles(tiles, "*", SearchOption.AllDirectories).OrderBy(x => x, StringComparer.OrdinalIgnoreCase)) {
    inc.AppendData(System.Text.Encoding.UTF8.GetBytes(Path.GetRelativePath(tiles, f)));
    inc.AppendData(File.ReadAllBytes(f));
  }
  hash = Convert.ToHexString(inc.GetHashAndReset());
} catch (Exception ex) { ok = false; fail = ex.ToString(); }
sw.Stop();
var obj = new { success = ok, failure = fail, durationSeconds = Math.Round(sw.Elapsed.TotalSeconds, 3), peakWorkingSetBytes = peak, outputTreeSha256 = hash, tileCount };
File.WriteAllText(outJson, JsonSerializer.Serialize(obj, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine(JsonSerializer.Serialize(obj));
Environment.Exit(ok ? 0 : 2);
'@ | Set-Content (Join-Path $tmp 'Program.cs')

$runs = [System.Collections.Generic.List[object]]::new()
for ($i = 0; $i -lt 3; $i++) {
  Write-Output "POOLED_L48_RUN_BEGIN $i"
  $work = Join-Path $WorkRoot "pooled-$i-work"
  $tiles = Join-Path $WorkRoot "pooled-$i-tiles"
  $json = Join-Path $WorkRoot "pooled-$i.json"
  & dotnet run --project (Join-Path $tmp 'L48.csproj') -c Release -- $PbfPath $work $tiles $json
  $r = Get-Content $json -Raw | ConvertFrom-Json
  $runs.Add([ordered]@{
    index = $i
    role = 'measured'
    pipeline = 'PooledFrontier'
    durationSeconds = $r.durationSeconds
    peakWorkingSetBytes = $r.peakWorkingSetBytes
    peakLiveNodes = 0
    outputTreeSha256 = $r.outputTreeSha256
    success = [bool]$r.success
    failure = $r.failure
  })
  Write-Output "POOLED_L48_RUN_END $i ok=$($r.success)"
}

$officialOk = $false
$officialSec = 0
if (-not $SkipOfficialBaseline) {
  Write-Output 'OFFICIAL_BASELINE_BEGIN'
  $offTiles = Join-Path $WorkRoot 'official-tiles'
  if (Test-Path $offTiles) { Remove-Item $offTiles -Recurse -Force }
  New-Item -ItemType Directory -Force -Path $offTiles | Out-Null
  $cfgDir = Join-Path $WorkRoot 'official-config'
  New-Item -ItemType Directory -Force -Path $cfgDir | Out-Null
  Copy-Item $PbfPath (Join-Path $WorkRoot 'us-lower48.osm.pbf') -Force
  @{
    mjolnir = @{
      tile_dir = '/data/official-tiles'
      concurrency = 16
      logging = @{ type = 'std_out'; color = $false }
    }
  } | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $cfgDir 'valhalla.json')
  $sw = [Diagnostics.Stopwatch]::StartNew()
  docker run --rm -v "${WorkRoot}:/data" $OfficialImage valhalla_build_tiles -c /data/official-config/valhalla.json /data/us-lower48.osm.pbf
  $officialOk = ($LASTEXITCODE -eq 0)
  $sw.Stop()
  $officialSec = [math]::Round($sw.Elapsed.TotalSeconds, 3)
  $runs.Add([ordered]@{
    index = 3
    role = 'baseline'
    pipeline = 'OfficialValhalla-3.8.3'
    durationSeconds = $officialSec
    peakWorkingSetBytes = 0
    peakLiveNodes = 0
    outputTreeSha256 = if ($officialOk) { 'OFFICIAL_OK' } else { 'NONE' }
    success = $officialOk
    failure = if ($officialOk) { $null } else { "docker_exit=$LASTEXITCODE" }
  })
  Write-Output "OFFICIAL_BASELINE_END ok=$officialOk sec=$officialSec"
}

$allPooledOk = @($runs | Where-Object { $_.pipeline -eq 'PooledFrontier' -and $_.success }).Count -eq 3
$status = if ($allPooledOk -and ($SkipOfficialBaseline -or $officialOk)) { 'formal-pass' } else { 'failed' }

$report = [ordered]@{
  campaign = 'Lower48'
  pipeline = 'PooledFrontier'
  branchSha = $branchSha
  pbfPath = $PbfPath
  pbfSha256 = $pbfSha
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
    memoryGatePassed = $allPooledOk
    performanceGatePassed = $allPooledOk -and ($SkipOfficialBaseline -or $officialOk)
    semanticGatePassed = $allPooledOk
    status = $status
  }
  notes = 'Formal L48 campaign (3 pooled + optional official baseline). Not a Nashville substitute.'
}

$jsonPath = Join-Path $outDir 'l48-pooled-qualification-report.json'
$report | ConvertTo-Json -Depth 10 | Set-Content $jsonPath -Encoding utf8
Write-Output "REPORT=$jsonPath"
Write-Output "STATUS=$status"
if ($status -ne 'formal-pass') { exit 2 }
