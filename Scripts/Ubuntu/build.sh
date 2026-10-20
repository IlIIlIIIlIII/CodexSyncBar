#!/usr/bin/env bash
set -euo pipefail
source "$(dirname "$0")/common.sh"
syncbar_require_dotnet
mkdir -p "$SYNCBAR_OUTPUT/backend/Runtime" "$SYNCBAR_OUTPUT/ui"
install -m 755 "$SYNCBAR_REPO/Ubuntu/packaging/prepare_session.py" "$SYNCBAR_OUTPUT/prepare-session.py"

"$SYNCBAR_DOTNET" publish \
    "$SYNCBAR_REPO/Ubuntu/ClaudeSyncBar.Ubuntu.Backend/ClaudeSyncBar.Ubuntu.Backend.csproj" \
    --configuration Release --runtime linux-x64 --self-contained true \
    --output "$SYNCBAR_OUTPUT/backend" --nologo

install -m 755 "$SYNCBAR_REPO/Support/claude-switch" "$SYNCBAR_OUTPUT/backend/Runtime/claude-switch"
install -m 755 "$SYNCBAR_REPO/Support/claude-syncbar-askpass" "$SYNCBAR_OUTPUT/backend/Runtime/claude-syncbar-askpass"
install -m 755 "$SYNCBAR_REPO/Support/usage-summary.mjs" "$SYNCBAR_OUTPUT/backend/Runtime/usage-summary.mjs"
install -m 755 \
    "$SYNCBAR_REPO/Ubuntu/ClaudeSyncBar.Ubuntu.Platform/Runtime/claude-syncbar-linux-askpass" \
    "$SYNCBAR_OUTPUT/backend/Runtime/claude-syncbar-linux-askpass"
install -m 755 \
    "$SYNCBAR_REPO/Ubuntu/ClaudeSyncBar.Ubuntu.Platform/Runtime/claude-syncbar-secret-tool" \
    "$SYNCBAR_OUTPUT/backend/Runtime/claude-syncbar-secret-tool"
python3 - "$SYNCBAR_REPO/Ubuntu/ui/claude_syncbar" "$SYNCBAR_OUTPUT/ui/claude_syncbar" <<'PY'
import shutil
import sys
shutil.copytree(sys.argv[1], sys.argv[2], dirs_exist_ok=True,
                ignore=shutil.ignore_patterns('__pycache__', '*.pyc'))
PY
mkdir -p "$SYNCBAR_OUTPUT/gnome"
cp -a "$SYNCBAR_REPO/Ubuntu/gnome/." "$SYNCBAR_OUTPUT/gnome/"
printf 'Ubuntu application built: %s\n' "$SYNCBAR_OUTPUT"
