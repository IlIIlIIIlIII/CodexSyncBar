#!/usr/bin/env bash
set -euo pipefail
ROOT=$(cd "$(dirname "$0")/.." && pwd)
TEST_ROOT=$(mktemp -d "${TMPDIR:-/tmp}/codex-cli-update-tests.XXXXXX")
trap 'rm -rf "$TEST_ROOT"' EXIT

# Load definitions only. No real SSH, package installation, or process signal.
sed '$d' "$ROOT/Support/gpt-switch" >"$TEST_ROOT/functions.sh"
source "$TEST_ROOT/functions.sh"
TEST_ORIGINAL_PATH="$PATH"
fixture() {
  TEST_HOME="$TEST_ROOT/home"
  CODEX_DIR="$TEST_HOME/.codex"
  export TEST_PREFIX="$TEST_ROOT/npm"
  export TEST_VERSION="$TEST_ROOT/version"
  export TEST_EVENTS="$TEST_ROOT/events"
  export TEST_INSTALL_FAIL=0 TEST_INVALID_AFTER=0
  mkdir -p "$TEST_PREFIX/bin" "$TEST_PREFIX/lib/node_modules/@openai/codex/bin"
  printf '1.0.0\n' >"$TEST_VERSION"
  : >"$TEST_EVENTS"
  cat >"$TEST_PREFIX/lib/node_modules/@openai/codex/bin/codex.js" <<'SH'
#!/bin/bash
printf 'codex-cli %s\n' "$(cat "$TEST_VERSION")"
SH
  cat >"$TEST_PREFIX/bin/npm" <<'SH'
#!/bin/bash
if [ "$1" = root ]; then printf '%s/lib/node_modules\n' "$TEST_PREFIX"; exit; fi
printf 'install %s\n' "$*" >>"$TEST_EVENTS"
[ "$TEST_INSTALL_FAIL" = 0 ] || exit 17
if [ "$TEST_INVALID_AFTER" = 1 ]; then printf 'invalid\n' >"$TEST_VERSION"; else printf '2.0.0\n' >"$TEST_VERSION"; fi
SH
  chmod +x "$TEST_PREFIX/bin/npm" "$TEST_PREFIX/lib/node_modules/@openai/codex/bin/codex.js"
  ln -sf ../lib/node_modules/@openai/codex/bin/codex.js "$TEST_PREFIX/bin/codex"
  export PATH="$TEST_PREFIX/bin:$TEST_ORIGINAL_PATH"
}
node_stop_clients() {
  printf 'stop\n' >>"$TEST_EVENTS"
  printf 'stopped_processes=%s forced_processes=0\n' "${TEST_STOP_COUNT:-0}"
}
codex_app_servers_run_version() { [ "${TEST_RECONNECTED:-0}" = 1 ]; }
sleep() { :; }

fixture
output=$(node_update_codex)
[[ "$output" == *'before=1.0.0 after=2.0.0 manager=npm restart=not-running'* ]]
[[ "$(tail -1 "$TEST_EVENTS")" == stop ]]
grep -F 'install --global @openai/codex@latest' "$TEST_EVENTS" >/dev/null

fixture
TEST_INSTALL_FAIL=1
if (node_update_codex) >"$TEST_ROOT/failure" 2>&1; then exit 1; fi
! grep -q '^stop$' "$TEST_EVENTS"

fixture
TEST_INVALID_AFTER=1
if (node_update_codex) >"$TEST_ROOT/failure" 2>&1; then exit 1; fi
! grep -q '^stop$' "$TEST_EVENTS"

fixture
TEST_STOP_COUNT=2 TEST_RECONNECTED=1
output=$(node_update_codex)
[[ "$output" == *'restart=reconnected'* ]]

fixture
TEST_STOP_COUNT=2 TEST_RECONNECTED=0
rc=0
output=$(node_update_codex) || rc=$?
[ "$rc" = 2 ]
[[ "$output" == *'after=2.0.0 manager=npm restart=reconnect-pending'* ]]

fixture
rm "$TEST_PREFIX/bin/codex"
cp "$TEST_PREFIX/lib/node_modules/@openai/codex/bin/codex.js" "$TEST_PREFIX/bin/codex"
if (node_update_codex) >"$TEST_ROOT/failure" 2>&1; then exit 1; fi
[ ! -s "$TEST_EVENTS" ]

# A shim in another bin directory must still update the owning npm prefix.
fixture
mkdir -p "$TEST_ROOT/shims"
ln -sf "$TEST_PREFIX/bin/codex" "$TEST_ROOT/shims/codex"
PATH="$TEST_ROOT/shims:$PATH"
TEST_STOP_COUNT=0
output=$(node_update_codex)
[[ "$output" == *'manager=npm restart=not-running'* ]]

# Exercise the other supported installers without downloading or installing.
fixture
mkdir -p "$CODEX_DIR/packages/standalone/releases/test/bin"
cp "$TEST_PREFIX/lib/node_modules/@openai/codex/bin/codex.js" "$CODEX_DIR/packages/standalone/releases/test/bin/codex"
ln -sf "$CODEX_DIR/packages/standalone/releases/test/bin/codex" "$TEST_PREFIX/bin/codex"
cat >"$TEST_PREFIX/bin/curl" <<'SH'
#!/bin/bash
cat <<'INSTALLER'
#!/bin/sh
[ "$CODEX_NON_INTERACTIVE" = 1 ] || exit 1
[ "$CODEX_INSTALL_DIR" = "$TEST_PREFIX/bin" ] || exit 1
printf '2.0.0\n' >"$TEST_VERSION"
INSTALLER
SH
chmod +x "$TEST_PREFIX/bin/curl"
output=$(node_update_codex)
[[ "$output" == *'after=2.0.0 manager=standalone restart=not-running'* ]]
for manager in Caskroom Cellar; do
  fixture
  mkdir -p "$TEST_ROOT/$manager/codex/2/bin"
  cp "$TEST_PREFIX/lib/node_modules/@openai/codex/bin/codex.js" "$TEST_ROOT/$manager/codex/2/bin/codex"
  ln -sf "$TEST_ROOT/$manager/codex/2/bin/codex" "$TEST_PREFIX/bin/codex"
  cat >"$TEST_PREFIX/bin/brew" <<'SH'
#!/bin/bash
printf '%s\n' "$*" >>"$TEST_EVENTS"
printf '2.0.0\n' >"$TEST_VERSION"
SH
  chmod +x "$TEST_PREFIX/bin/brew"
  output=$(node_update_codex)
  [[ "$output" == *'manager=brew-'* ]]
done

# Run the public controller through an SSH substitute, including the shipped
# helper payload and disabled-device filtering. First host fails; second runs.
TEST_STATE="$TEST_HOME/.local/share/gpt-switch"
mkdir -p "$TEST_STATE"
jq -n '{schemaVersion:1,nextAccountID:2,accounts:[{id:1,email:"test@example.com"}],devices:
  ["offline","online","disabled"] | to_entries | map({id:.value,displayName:.value,
   host:"example.invalid",port:22,username:"tester",authentication:"openSSHConfig",
   hasPassword:false,hasKeyPassphrase:false,enabled:(.value != "disabled")})}' >"$TEST_STATE/config.json"
chmod 600 "$TEST_STATE/config.json"
cat >"$TEST_ROOT/ssh" <<'SH'
#!/bin/bash
cat >"$TEST_PAYLOAD"
[[ "${!#}" == 'bash -l -s -- __node update-codex' ]] || exit 63
printf 'call\n' >>"$TEST_EVENTS"
if [ "$(wc -l <"$TEST_EVENTS" | tr -d ' ')" = 1 ]; then echo 'connection failed' >&2; exit 255; fi
printf 'before=1.0.0 after=2.0.0 manager=npm restart=not-running\n'
SH
chmod +x "$TEST_ROOT/ssh"
: >"$TEST_EVENTS"
rc=0
output=$(GPT_SWITCH_STATE_ROOT="$TEST_STATE" GPT_SWITCH_SSH_BIN="$TEST_ROOT/ssh" TEST_PAYLOAD="$TEST_ROOT/payload" \
  /bin/bash "$ROOT/Support/gpt-switch" update-codex all) || rc=$?
[ "$rc" = 2 ]
printf '%s\n' "$output" | jq -se 'length == 2 and .[0].deviceID == "offline" and .[0].exitStatus == 255 and .[1].deviceID == "online" and .[1].exitStatus == 0' >/dev/null
cmp "$TEST_ROOT/payload" "$ROOT/Support/gpt-switch"

# Verify every reconnected native server, not just the first matching PID.
source "$TEST_ROOT/functions.sh"
codex_app_server_pids() { printf '%s\n' "$TEST_PID_LIST"; }
readlink() {
  case "$1" in
    /proc/201/exe) echo /fake/codex-new ;;
    /proc/202/exe) echo /fake/node ;;
    /proc/203/exe) echo /fake/codex-old ;;
    *) command readlink "$@" ;;
  esac
}
lsof() {
  case "$3" in
    201) printf 'p201\nn/fake/codex-new\n' ;;
    202) printf 'p202\nn/fake/node\n' ;;
    203) printf 'p203\nn/fake/codex-old\n' ;;
  esac
}
codex_cli_version() {
  case "$1" in
    /proc/201/exe|/fake/codex-new) echo 2.0.0 ;;
    /proc/203/exe|/fake/codex-old) echo 1.0.0 ;;
  esac
}
TEST_PID_LIST=$'201\n202'
codex_app_servers_run_version 2.0.0
TEST_PID_LIST=$'201\n203'
! codex_app_servers_run_version 2.0.0
TEST_PID_LIST=202
! codex_app_servers_run_version 2.0.0
TEST_PID_LIST=""
! codex_app_servers_run_version 2.0.0

printf 'Remote Codex update tests passed (install, failure isolation, validation, restart, pending, unsupported path, SSH batch).\n'
