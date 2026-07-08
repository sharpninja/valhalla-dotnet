[CmdletBinding()]
param(
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]] $BuildArguments
)

# Windows bootstrap: pass remaining args through to the Nuke build runner.
# Run from the repo root (this script's dir) so Nuke resolves RootDirectory here regardless of the
# caller's current directory.
$ErrorActionPreference = 'Stop'
Set-Location -LiteralPath $PSScriptRoot
$buildProject = Join-Path $PSScriptRoot 'build/_build.csproj'
dotnet run --project $buildProject -- @BuildArguments
exit $LASTEXITCODE
