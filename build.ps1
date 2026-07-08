[CmdletBinding()]
param(
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]] $BuildArguments
)

# Windows bootstrap: pass remaining args through to the Nuke build runner.
$ErrorActionPreference = 'Stop'
$buildProject = Join-Path $PSScriptRoot 'build/_build.csproj'
dotnet run --project $buildProject -- @BuildArguments
exit $LASTEXITCODE
