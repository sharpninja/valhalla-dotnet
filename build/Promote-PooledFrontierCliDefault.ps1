#requires -Version 7
<#
.SYNOPSIS
  Flip CLI road-pipeline default to PooledFrontier only after promotion-ready stamp.
#>
param(
  [string]$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path,
  [switch]$DryRun
)

$ErrorActionPreference = 'Stop'
$stamp = Join-Path $RepoRoot 'artifacts\qualification\promotion\daily-l48\promotion-ready.json'
$l48 = Join-Path $RepoRoot 'artifacts\qualification\lower48\l48-pooled-qualification-report.json'

if (-not (Test-Path $stamp)) {
  throw "Promotion-ready stamp missing: $stamp (need 7 consecutive daily L48 successes)"
}
if (-not (Test-Path $l48)) { throw "L48 report missing: $l48" }
$l48Obj = Get-Content $l48 -Raw | ConvertFrom-Json
if ($l48Obj.verdict.status -ne 'formal-pass') {
  throw "L48 not formal-pass: $($l48Obj.verdict.status)"
}

$cli = Join-Path $RepoRoot 'src\SharpNinja.Valhalla.Generation.Tool\ValhallaGenerationCli.cs'
if (-not (Test-Path $cli)) { throw "CLI source missing: $cli" }

$text = Get-Content $cli -Raw
# Find default road-pipeline Legacy and flip to PooledFrontier only at the option default
if ($text -notmatch 'road-pipeline') {
  throw 'road-pipeline option not found in ValhallaGenerationCli.cs'
}

# Guard: do not flip if already PooledFrontier as default in help/default assignment patterns
$updated = $text
# Common pattern: default ManagedRoadGraphPipeline.Legacy in option binder
$updated2 = $updated -replace 'ManagedRoadGraphPipeline\.Legacy(?=[^\n]*road-pipeline|)', {
  # too broad - use careful replace on known default sites only
  $_.Value
}

# Safer: only change explicit default in option definition comments / DefaultValue
# Read file lines and flip the default assignment for road-pipeline option only
$lines = Get-Content $cli
$inRoadPipeline = $false
$changed = 0
for ($i = 0; $i -lt $lines.Count; $i++) {
  if ($lines[$i] -match 'road-pipeline') { $inRoadPipeline = $true }
  if ($inRoadPipeline -and $lines[$i] -match 'ManagedRoadGraphPipeline\.Legacy') {
    $lines[$i] = $lines[$i] -replace 'ManagedRoadGraphPipeline\.Legacy', 'ManagedRoadGraphPipeline.PooledFrontier'
    $changed++
    $inRoadPipeline = $false
  }
  if ($inRoadPipeline -and $lines[$i] -match '^\s*\[Option' -and $i -gt 0 -and $lines[$i] -notmatch 'road-pipeline') {
    # left option block without change
  }
}

if ($changed -eq 0) {
  # fallback: flip BuildRequest default? Plan says CLI default
  throw 'Could not locate CLI road-pipeline default Legacy assignment to flip; inspect ValhallaGenerationCli.cs'
}

Write-Output "WOULD_CHANGE_LINES=$changed"
if ($DryRun) {
  Write-Output 'DRY_RUN=true'
  exit 0
}

Set-Content -Path $cli -Value ($lines -join "`n") -Encoding utf8
Write-Output "UPDATED=$cli"
Write-Output 'NOTE=Run tests ManagedRoadGraphPipelineContractTests / ValhallaGenerationCliContractTests and update expectations before commit.'
