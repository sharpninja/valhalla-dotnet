#!/usr/bin/env bash
set -euo pipefail

# POSIX bootstrap: pass remaining args through to the Nuke build runner.
# Run from the repo root so Nuke resolves RootDirectory here regardless of the caller's cwd.
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$SCRIPT_DIR"
dotnet run --project "$SCRIPT_DIR/build/_build.csproj" -- "$@"
