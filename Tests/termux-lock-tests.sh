#!/usr/bin/env bash
set -euo pipefail
ROOT=$(cd "$(dirname "$0")/.." && pwd)
TEST_ROOT=$(mktemp -d "${TMPDIR:-/tmp}/syncbar-termux-locks.XXXXXX")
holder=""
trap 'if [ -n "$holder" ]; then kill "$holder" 2>/dev/null || true; wait "$holder" 2>/dev/null || true; fi; rm -rf "$TEST_ROOT"' EXIT
export PREFIX="$TEST_ROOT/termux"
export GPT_SWITCH_STATE_ROOT="$TEST_ROOT/state" CODEX_HOME="$TEST_ROOT/codex"
mkdir -p "$PREFIX/bin" "$GPT_SWITCH_STATE_ROOT/profiles" "$CODEX_HOME"
printf '#!/bin/sh\nexit 0\n' >"$PREFIX/bin/pkg"
chmod 700 "$PREFIX/bin/pkg"
sed '$d' "$ROOT/Support/gpt-switch" >"$TEST_ROOT/functions.sh"
source "$TEST_ROOT/functions.sh"

# Neither existing files, dangling symlinks nor directories may be replaced.
for type in file symlink directory; do
  printf 'original\n' >"$TEST_ROOT/source"
  case "$type" in
    file) printf 'existing\n' >"$TEST_ROOT/destination" ;;
    symlink) ln -s "$TEST_ROOT/missing" "$TEST_ROOT/destination" ;;
    directory) mkdir "$TEST_ROOT/destination" ;;
  esac
  if hardlink_lock_file "$TEST_ROOT/source" "$TEST_ROOT/destination"; then exit 1; fi
  [ "$(cat "$TEST_ROOT/source")" = original ]
  [ ! -e "$TEST_ROOT/missing" ]
  rm -rf "$TEST_ROOT/destination"
done

# Contenders must be rejected while the first process owns the flock gate.
printf '%s\n' '{"auth_mode":"chatgpt","tokens":{"id_token":"test","access_token":"test","refresh_token":"","account_id":"test"}}' \
  >"$GPT_SWITCH_STATE_ROOT/profiles/1.auth.json"
chmod 600 "$GPT_SWITCH_STATE_ROOT/profiles/1.auth.json"
bash -c 'source "$1"; acquire_lock; printf ready >"$2"; while [ ! -f "$3" ]; do sleep 0.05; done; release_lock' \
  bash "$TEST_ROOT/functions.sh" "$TEST_ROOT/ready" "$TEST_ROOT/release" &
holder=$!
for ((i=0; i<100; i++)); do [ ! -f "$TEST_ROOT/ready" ] || break; sleep 0.05; done
[ -f "$TEST_ROOT/ready" ]
if bash "$ROOT/Support/gpt-switch" __node initialize 1 >"$TEST_ROOT/contender" 2>&1; then exit 1; fi
grep -q 'another switch operation is already running' "$TEST_ROOT/contender"
[ ! -f "$CODEX_HOME/auth.json" ]
touch "$TEST_ROOT/release"
wait "$holder"
holder=""
bash "$ROOT/Support/gpt-switch" __node initialize 1 >"$TEST_ROOT/initialized"
bash "$ROOT/Support/gpt-switch" __node verify 1 >/dev/null

# Recover an abandoned owner file without losing the completed credentials.
printf 'pid=99999999\ntoken=abandoned\n' >"$GPT_SWITCH_STATE_ROOT/.lock"
chmod 600 "$GPT_SWITCH_STATE_ROOT/.lock"
bash "$ROOT/Support/gpt-switch" __node initialize 1 >/dev/null
[ ! -f "$GPT_SWITCH_STATE_ROOT/.lock" ]
[ "$(stat -c %a "$GPT_SWITCH_STATE_ROOT/.lock-gate")" = 600 ]
printf 'Termux locking tests passed (no clobber, contention, credentials, stale recovery).\n'
