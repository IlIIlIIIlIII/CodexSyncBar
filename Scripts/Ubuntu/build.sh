#!/usr/bin/env bash
set -euo pipefail
source "$(dirname "$0")/common.sh"
syncbar_require_dotnet
mkdir -p "$SYNCBAR_OUTPUT/backend/Runtime" "$SYNCBAR_OUTPUT/ui"
install -m 755 "$SYNCBAR_REPO/Ubuntu/packaging/prepare_session.py" "$SYNCBAR_OUTPUT/prepare-session.py"

"$SYNCBAR_DOTNET" publish \
    "$SYNCBAR_REPO/Ubuntu/CodexSyncBar.Ubuntu.Backend/CodexSyncBar.Ubuntu.Backend.csproj" \
    --configuration Release --runtime linux-x64 --self-contained true \
    --output "$SYNCBAR_OUTPUT/backend" --nologo

install -m 755 "$SYNCBAR_REPO/Support/gpt-switch" "$SYNCBAR_OUTPUT/backend/Runtime/gpt-switch"
install -m 755 "$SYNCBAR_REPO/Support/codex-syncbar-askpass" "$SYNCBAR_OUTPUT/backend/Runtime/codex-syncbar-askpass"
install -m 755 "$SYNCBAR_REPO/Support/usage-summary.mjs" "$SYNCBAR_OUTPUT/backend/Runtime/usage-summary.mjs"
install -m 755 \
    "$SYNCBAR_REPO/Ubuntu/CodexSyncBar.Ubuntu.Platform/Runtime/codex-syncbar-linux-askpass" \
    "$SYNCBAR_OUTPUT/backend/Runtime/codex-syncbar-linux-askpass"
install -m 755 \
    "$SYNCBAR_REPO/Ubuntu/CodexSyncBar.Ubuntu.Platform/Runtime/codex-syncbar-secret-tool" \
    "$SYNCBAR_OUTPUT/backend/Runtime/codex-syncbar-secret-tool"
python3 - "$SYNCBAR_REPO/Ubuntu/ui/codex_syncbar" "$SYNCBAR_OUTPUT/ui/codex_syncbar" <<'PY'
import shutil
import sys
shutil.copytree(sys.argv[1], sys.argv[2], dirs_exist_ok=True,
                ignore=shutil.ignore_patterns('__pycache__', '*.pyc'))
PY
printf 'Ubuntu application built: %s\n' "$SYNCBAR_OUTPUT"
