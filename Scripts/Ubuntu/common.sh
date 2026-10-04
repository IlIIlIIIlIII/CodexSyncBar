#!/usr/bin/env bash
set -euo pipefail

SYNCBAR_REPO=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)
SYNCBAR_OUTPUT=${SYNCBAR_OUTPUT:-"$SYNCBAR_REPO/dist/ubuntu"}
SYNCBAR_DOTNET=${SYNCBAR_DOTNET:-dotnet}
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1
export DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1

syncbar_require_dotnet() {
    if ! command -v "$SYNCBAR_DOTNET" >/dev/null 2>&1; then
        printf 'Install .NET 10 SDK or set SYNCBAR_DOTNET to its executable.\n' >&2
        exit 1
    fi
}

syncbar_prepare_node() {
    if [[ -n ${SYNCBAR_NODE:-} ]]; then
        export PATH="$(dirname "$SYNCBAR_NODE"):$PATH"
    fi
    if ! command -v node >/dev/null 2>&1; then
        printf 'Install nodejs or set SYNCBAR_NODE to a Node.js executable.\n' >&2
        exit 1
    fi
}
