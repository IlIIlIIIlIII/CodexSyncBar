#!/usr/bin/env bash
set -euo pipefail
source "$(dirname "$0")/common.sh"
if [[ ! -x "$SYNCBAR_OUTPUT/backend/CodexSyncBar.Backend" ]]; then
    printf 'Build first with Scripts/Ubuntu/build.sh. Use --demo for synthetic data.\n' >&2
    exit 1
fi
export SYNCBAR_APP_DIR="$SYNCBAR_OUTPUT"
export SYNCBAR_DEV=1
exec bash "$SYNCBAR_REPO/Ubuntu/packaging/codex-syncbar" "$@"
