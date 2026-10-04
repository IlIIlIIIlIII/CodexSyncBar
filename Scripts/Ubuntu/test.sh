#!/usr/bin/env bash
set -euo pipefail
source "$(dirname "$0")/common.sh"
syncbar_require_dotnet
syncbar_prepare_node
umask 077
# Tests supply their own temporary homes; do not inherit an agent auth override.
unset CODEX_HOME CODEX_SYNCBAR_STATE_ROOT
mkdir -p "$SYNCBAR_OUTPUT/tests"
cd "$SYNCBAR_REPO"
"$SYNCBAR_DOTNET" test \
    Windows/CodexSyncBar.Windows.Core.Tests/CodexSyncBar.Windows.Core.Tests.csproj \
    --configuration Release --nologo --logger 'trx;LogFileName=windows-core-linux.trx' \
    --results-directory "$SYNCBAR_OUTPUT/tests"
"$SYNCBAR_DOTNET" test \
    Ubuntu/CodexSyncBar.Ubuntu.Tests/CodexSyncBar.Ubuntu.Tests.csproj \
    --configuration Release --nologo --logger 'trx;LogFileName=ubuntu-core.trx' \
    --results-directory "$SYNCBAR_OUTPUT/tests"
# Includes usage summary, controller concurrency and remote CLI update contracts.
bash Tests/helper-contract-tests.sh
python3 Ubuntu/tests/test_ipc_client.py
python3 Ubuntu/tests/test_user_install.py
python3 Ubuntu/tests/test_live_qa.py
python3 Ubuntu/tests/test_live_gtk_capture.py
python3 Ubuntu/tests/test_secret_tool.py
python3 Ubuntu/tests/test_session_environment.py
bash Scripts/Ubuntu/build.sh
python3 Ubuntu/tests/test_backend_ipc.py --backend "$SYNCBAR_OUTPUT/backend/CodexSyncBar.Backend"
printf 'Ubuntu isolated checks passed. Real account and desktop E2E is a separate acceptance step.\n'
