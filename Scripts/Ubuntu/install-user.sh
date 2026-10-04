#!/usr/bin/env bash
set -euo pipefail
source "$(dirname "$0")/common.sh"
exec /usr/bin/python3 "$SYNCBAR_REPO/Ubuntu/packaging/install_user.py" \
    --source "$SYNCBAR_OUTPUT" "$@"
