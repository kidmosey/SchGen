#!/usr/bin/env bash
# Compat shim — extraction now lives in the schgen CLI itself so that any
# install location works (global tool, dotnet run, source checkout) and the
# output lands in a shared per-user cache instead of a per-project libs/.
# This script just forwards.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

echo "schgen: install-libs.sh is deprecated — use 'schgen install-stock-libs'"
echo "schgen: forwarding now..."
echo

if command -v schgen >/dev/null 2>&1; then
    exec schgen install-stock-libs "$@"
else
    exec dotnet run --project "$SCRIPT_DIR/../src/Schgen.Cli" -- install-stock-libs "$@"
fi
