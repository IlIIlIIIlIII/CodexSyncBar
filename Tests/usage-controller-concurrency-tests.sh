#!/usr/bin/env bash
set -euo pipefail

ROOT=$(cd "$(dirname "$0")/.." && pwd)
TEST_ROOT=$(mktemp -d "${TMPDIR:-/tmp}/syncbar-usage-concurrency.XXXXXX")
TEST_PIDS=()
cleanup() {
  for pid in "${TEST_PIDS[@]-}"; do kill "$pid" 2>/dev/null || true; done
  rm -rf "$TEST_ROOT"
}
trap cleanup EXIT
STATE="$TEST_ROOT/state"
CODEX="$TEST_ROOT/codex"
HELPER="$TEST_ROOT/gpt-switch"
mkdir -p "$STATE" "$CODEX/sessions"
cp "$ROOT/Support/gpt-switch" "$HELPER"
cp "$ROOT/Support/codex-syncbar-askpass" "$TEST_ROOT/askpass"
chmod 700 "$HELPER" "$STATE" "$CODEX"
chmod 700 "$TEST_ROOT/askpass"
sed '$d' "$HELPER" >"$TEST_ROOT/functions.sh"
jq -n '{schemaVersion:1,nextAccountID:2,accounts:[{id:1,email:"test@example.com"}],devices:[{
  id:"remote",displayName:"Remote",host:"example.invalid",port:22,username:"tester",
  authentication:"openSSHConfig",hasPassword:false,hasKeyPassphrase:false,enabled:true
}]}' >"$STATE/config.json"
chmod 600 "$STATE/config.json"
cat >"$TEST_ROOT/node" <<'SH'
#!/usr/bin/env bash
set -euo pipefail
if [ "${TEST_SLOW_SCAN:-0}" = 1 ]; then
  touch "$TEST_ROOT/scan-started"
  for i in {1..500}; do
    [ -f "$TEST_ROOT/release-scan" ] && break
    sleep 0.02
  done
  [ -f "$TEST_ROOT/release-scan" ] || exit 1
fi
printf '%s\n' '{"schemaVersion":6,"totalTokens":20,"buckets":[],"errors":[]}'
SH
cat >"$TEST_ROOT/ssh" <<'SH'
#!/usr/bin/env bash
case " $* " in
  *" __node version "*) printf '2.1.3\n' ;;
  *" __node usage-summary "*)
    if [ "${TEST_DISABLE_DEVICE:-0}" = 1 ]; then
      jq '.devices[0].enabled = false' "$GPT_SWITCH_CONFIG_FILE" >"$GPT_SWITCH_CONFIG_FILE.tmp"
      chmod 600 "$GPT_SWITCH_CONFIG_FILE.tmp"
      mv "$GPT_SWITCH_CONFIG_FILE.tmp" "$GPT_SWITCH_CONFIG_FILE"
    fi
    if [ "${TEST_REMOVE_CONFIGURATION:-0}" = 1 ]; then rm -f "$GPT_SWITCH_CONFIG_FILE"; fi
    schema=${TEST_REMOTE_SCHEMA:-6}
    [ ! -f "$TEST_ROOT/installed" ] || schema=6
    printf '{"schemaVersion":%s,"totalTokens":20,"buckets":[],"errors":[]}\n' "$schema"
    ;;
  *" __node status "*) printf 'active=1 fingerprint=test mode=600 auth_mode=chatgpt cli=logged-in\n' ;;
  *".bootstrap."*)
    [ -f "$GPT_SWITCH_STATE_ROOT/.controller-lock" ] || exit 73
    cat >/dev/null
    touch "$TEST_ROOT/installed"
    printf 'bootstrap=installed\n'
    ;;
  *) exit 64 ;;
esac
SH
chmod 700 "$TEST_ROOT/node" "$TEST_ROOT/ssh"
common_env=(
  GPT_SWITCH_STATE_ROOT="$STATE" CODEX_HOME="$CODEX"
  GPT_SWITCH_CONFIG_FILE="$STATE/config.json"
  GPT_SWITCH_NODE_BIN="$TEST_ROOT/node"
  GPT_SWITCH_USAGE_HELPER="$ROOT/Support/usage-summary.mjs"
  GPT_SWITCH_ASKPASS_HELPER="$TEST_ROOT/askpass"
  GPT_SWITCH_SSH_BIN="$TEST_ROOT/ssh" TEST_ROOT="$TEST_ROOT"
)
wait_for_file() {
  for i in {1..500}; do
    [ -f "$1" ] && return 0
    sleep 0.02
  done
  printf 'timed out waiting for %s\n' "$1" >&2
  return 1
}

# Hold the real cross-process controller lock, as status/auth work does.
env "${common_env[@]}" bash -c '
  source "$1"
  acquire_controller_lock
  touch "$TEST_ROOT/lock-started"
  for i in {1..1000}; do
    [ -f "$TEST_ROOT/release-lock" ] && exit 0
    sleep 0.02
  done
  exit 1
' bash "$TEST_ROOT/functions.sh" >"$TEST_ROOT/lock.log" 2>&1 &
holder=$!
TEST_PIDS+=("$holder")
wait_for_file "$TEST_ROOT/lock-started"
cp "$STATE/.controller-lock" "$TEST_ROOT/lock-before"
# This stale recovery artifact would be deleted by ensure_dirs().
mkdir "$STATE/.swap-building.usage-test"
printf 'state=building\npid=99999999\n' >"$STATE/.swap-building.usage-test/manifest"
if ! env "${common_env[@]}" "$HELPER" usage-summary >"$TEST_ROOT/usage.jsonl" 2>"$TEST_ROOT/usage.err"; then
  cat "$TEST_ROOT/usage.err" >&2
  printf 'usage collection failed while another controller operation was running\n' >&2
  exit 1
fi
jq -es 'length == 2 and all(.[]; .isReachable and .summary.totalTokens == 20)' "$TEST_ROOT/usage.jsonl" >/dev/null
cmp "$STATE/.controller-lock" "$TEST_ROOT/lock-before"
env "${common_env[@]}" GPT_SWITCH_NODE_BIN=missing-node-runtime "$HELPER" usage-summary >"$TEST_ROOT/fallback.jsonl"
jq -es 'length == 2 and all(.[]; .isReachable)' "$TEST_ROOT/fallback.jsonl" >/dev/null
[ -f "$STATE/.swap-building.usage-test/manifest" ]

# A collector repair is a mutation: it must defer while the lock is held.
env "${common_env[@]}" TEST_REMOTE_SCHEMA=4 "$HELPER" usage-summary >"$TEST_ROOT/mismatch.jsonl"
jq -es '.[0].isReachable and (.[1].isReachable | not) and .[1].error == "usage summary schema mismatch"' "$TEST_ROOT/mismatch.jsonl" >/dev/null
[ ! -f "$TEST_ROOT/installed" ]
cmp "$STATE/.controller-lock" "$TEST_ROOT/lock-before"
[ -f "$STATE/.swap-building.usage-test/manifest" ]
touch "$TEST_ROOT/release-lock"
wait "$holder"
TEST_PIDS=()
rm -r "$STATE/.swap-building.usage-test"

# Without contention, schema repair still succeeds under the controller lock.
env "${common_env[@]}" TEST_REMOTE_SCHEMA=4 "$HELPER" usage-summary >"$TEST_ROOT/repaired.jsonl"
jq -es 'length == 2 and all(.[]; .isReachable and .summary.totalTokens == 20)' "$TEST_ROOT/repaired.jsonl" >/dev/null
[ -f "$TEST_ROOT/installed" ]
rm "$TEST_ROOT/installed"

# Never repair during activation or after the device was disabled mid-scan.
mkdir -m 700 "$STATE/device-activation-transactions"
touch "$STATE/device-activation-transactions/pending"
env "${common_env[@]}" TEST_REMOTE_SCHEMA=4 "$HELPER" usage-summary >"$TEST_ROOT/activation.jsonl"
[ ! -f "$TEST_ROOT/installed" ]
rm "$STATE/device-activation-transactions/pending"
cp "$STATE/config.json" "$TEST_ROOT/config-before"
env "${common_env[@]}" TEST_REMOTE_SCHEMA=4 TEST_DISABLE_DEVICE=1 "$HELPER" usage-summary >"$TEST_ROOT/disabled.jsonl"
[ ! -f "$TEST_ROOT/installed" ]
cp "$TEST_ROOT/config-before" "$STATE/config.json"
env "${common_env[@]}" TEST_REMOTE_SCHEMA=4 TEST_REMOVE_CONFIGURATION=1 "$HELPER" usage-summary >"$TEST_ROOT/removed-config.jsonl"
[ ! -f "$TEST_ROOT/installed" ]
cp "$TEST_ROOT/config-before" "$STATE/config.json"

# In the opposite order, a slow real controller scan must not block status.
env "${common_env[@]}" TEST_SLOW_SCAN=1 "$HELPER" usage-summary >"$TEST_ROOT/slow.jsonl" &
scanner=$!
TEST_PIDS+=("$scanner")
wait_for_file "$TEST_ROOT/scan-started"
env "${common_env[@]}" "$HELPER" status-json >"$TEST_ROOT/status.jsonl"
kill -0 "$scanner"
jq -es 'length == 2 and .[1].isReachable' "$TEST_ROOT/status.jsonl" >/dev/null
touch "$TEST_ROOT/release-scan"
wait "$scanner"
TEST_PIDS=()

# Session accounting must not recover or inspect pending authentication work,
# including on nodes without Node.js that use the jq collector.
mkdir -p "$STATE/.login-building.1.test"
printf 'pending-auth-transaction\n' >"$STATE/.login-building.1.test/manifest"
for runtime in "$TEST_ROOT/node" missing-node-runtime; do
  env "${common_env[@]}" GPT_SWITCH_NODE_BIN="$runtime" "$HELPER" usage-summary >"$TEST_ROOT/pending.jsonl"
  jq -es 'length == 2 and all(.[]; .isReachable)' "$TEST_ROOT/pending.jsonl" >/dev/null
  [ "$(cat "$STATE/.login-building.1.test/manifest")" = pending-auth-transaction ]
done
printf 'Usage controller concurrency tests passed\n'
