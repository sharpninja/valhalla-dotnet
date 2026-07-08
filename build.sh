#!/usr/bin/env bash
set -euo pipefail

# POSIX bootstrap: pass remaining args through to the Nuke build runner.
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
dotnet run --project "$SCRIPT_DIR/build/_build.csproj" -- "$@"
