#!/usr/bin/env bash
set -euo pipefail
ROOT=$(cd "$(dirname "$0")/.." && pwd)
TEST_ROOT=$(mktemp -d "${TMPDIR:-/tmp}/claude-cli-update-tests.XXXXXX")
trap 'rm -rf "$TEST_ROOT"' EXIT

# Load definitions only. No real SSH, package installation, or process signal.
sed '$d' "$ROOT/Support/claude-switch" >"$TEST_ROOT/functions.sh"
source "$TEST_ROOT/functions.sh"
TEST_ORIGINAL_PATH="$PATH"
TEST_ORIGINAL_HOME="$HOME"
fixture() {
  rm -rf "$TEST_ROOT/home" "$TEST_ROOT/npm"
  # The native installer's version directory is found below HOME.
  export HOME="$TEST_ROOT/home"
  export TEST_PREFIX="$TEST_ROOT/npm"
  export TEST_VERSION="$TEST_ROOT/version"
  export TEST_EVENTS="$TEST_ROOT/events"
  export TEST_INSTALL_FAIL=0 TEST_INVALID_AFTER=0 TEST_NO_CHANGE=0
  mkdir -p "$HOME" "$TEST_PREFIX/bin" "$TEST_PREFIX/lib/node_modules/@anthropic-ai/claude-code"
  printf '1.0.0\n' >"$TEST_VERSION"
  : >"$TEST_EVENTS"
  cat >"$TEST_PREFIX/lib/node_modules/@anthropic-ai/claude-code/cli.js" <<'SH'
#!/bin/bash
if [ "${1:-}" = update ]; then
  printf 'self-update\n' >>"$TEST_EVENTS"
  [ "$TEST_INSTALL_FAIL" = 0 ] || exit 17
  [ "$TEST_NO_CHANGE" = 1 ] || printf '2.0.0\n' >"$TEST_VERSION"
  exit 0
fi
printf '%s (Claude Code)\n' "$(cat "$TEST_VERSION")"
SH
  cat >"$TEST_PREFIX/bin/npm" <<'SH'
#!/bin/bash
if [ "$1" = root ]; then printf '%s/lib/node_modules\n' "$TEST_PREFIX"; exit; fi
printf 'install %s\n' "$*" >>"$TEST_EVENTS"
[ "$TEST_INSTALL_FAIL" = 0 ] || exit 17
if [ "$TEST_INVALID_AFTER" = 1 ]; then printf 'invalid\n' >"$TEST_VERSION"
elif [ "$TEST_NO_CHANGE" = 0 ]; then printf '2.0.0\n' >"$TEST_VERSION"; fi
SH
  chmod +x "$TEST_PREFIX/bin/npm" "$TEST_PREFIX/lib/node_modules/@anthropic-ai/claude-code/cli.js"
  ln -sf ../lib/node_modules/@anthropic-ai/claude-code/cli.js "$TEST_PREFIX/bin/claude"
  export PATH="$TEST_PREFIX/bin:$TEST_ORIGINAL_PATH"
}

fixture
info=$(node_claude_info)
[[ "$(printf '%s' "$info" | jq -r .manager)" == npm ]]
[[ "$(printf '%s' "$info" | jq -r .version)" == 1.0.0 ]]
[[ "$(printf '%s' "$info" | jq -r .canUpdate)" == true ]]
[ ! -s "$TEST_EVENTS" ]
output=$(node_update_claude)
[[ "$output" == 'before=1.0.0 after=2.0.0 manager=npm restart=not-running' ]]
grep -F 'install --global @anthropic-ai/claude-code@latest' "$TEST_EVENTS" >/dev/null

# Nothing newer was published: running sessions need no restart.
fixture
TEST_NO_CHANGE=1
output=$(node_update_claude)
[[ "$output" == 'before=1.0.0 after=1.0.0 manager=npm restart=not-needed' ]]

fixture
TEST_INSTALL_FAIL=1
if (node_update_claude) >"$TEST_ROOT/failure" 2>&1; then exit 1; fi
grep -F 'npm Claude Code update failed' "$TEST_ROOT/failure" >/dev/null

fixture
TEST_INVALID_AFTER=1
if (node_update_claude) >"$TEST_ROOT/failure" 2>&1; then exit 1; fi
grep -F 'failed version validation' "$TEST_ROOT/failure" >/dev/null

# A copied binary outside any known installer is never updated in place.
fixture
rm "$TEST_PREFIX/bin/claude"
cp "$TEST_PREFIX/lib/node_modules/@anthropic-ai/claude-code/cli.js" "$TEST_PREFIX/bin/claude"
info=$(node_claude_info)
printf '%s' "$info" | jq -e '.manager == "unknown" and (.canUpdate | not)' >/dev/null
if (node_update_claude) >"$TEST_ROOT/failure" 2>&1; then exit 1; fi
[ ! -s "$TEST_EVENTS" ]

# A shim in another bin directory must still update the owning npm prefix.
fixture
mkdir -p "$TEST_ROOT/shims"
ln -sf "$TEST_PREFIX/bin/claude" "$TEST_ROOT/shims/claude"
PATH="$TEST_ROOT/shims:$PATH"
output=$(node_update_claude)
[[ "$output" == *'manager=npm restart=not-running'* ]]

# The native installer updates itself; no package manager is involved.
fixture
mkdir -p "$HOME/.local/share/claude/versions" "$HOME/.local/bin"
cp "$TEST_PREFIX/lib/node_modules/@anthropic-ai/claude-code/cli.js" "$HOME/.local/share/claude/versions/1.0.0"
ln -sf "$HOME/.local/share/claude/versions/1.0.0" "$TEST_PREFIX/bin/claude"
info=$(node_claude_info)
[[ "$(printf '%s' "$info" | jq -r .manager)" == native ]]
output=$(node_update_claude)
[[ "$output" == 'before=1.0.0 after=2.0.0 manager=native restart=not-running' ]]
[[ "$(cat "$TEST_EVENTS")" == self-update ]]

fixture
mkdir -p "$HOME/.local/share/claude/versions"
cp "$TEST_PREFIX/lib/node_modules/@anthropic-ai/claude-code/cli.js" "$HOME/.local/share/claude/versions/1.0.0"
ln -sf "$HOME/.local/share/claude/versions/1.0.0" "$TEST_PREFIX/bin/claude"
TEST_INSTALL_FAIL=1
if (node_update_claude) >"$TEST_ROOT/failure" 2>&1; then exit 1; fi
grep -F 'official Claude Code update failed' "$TEST_ROOT/failure" >/dev/null

fixture
mkdir -p "$TEST_ROOT/Caskroom/claude-code/2/bin"
cp "$TEST_PREFIX/lib/node_modules/@anthropic-ai/claude-code/cli.js" "$TEST_ROOT/Caskroom/claude-code/2/bin/claude"
ln -sf "$TEST_ROOT/Caskroom/claude-code/2/bin/claude" "$TEST_PREFIX/bin/claude"
cat >"$TEST_PREFIX/bin/brew" <<'SH'
#!/bin/bash
printf '%s\n' "$*" >>"$TEST_EVENTS"
printf '2.0.0\n' >"$TEST_VERSION"
SH
chmod +x "$TEST_PREFIX/bin/brew"
output=$(node_update_claude)
[[ "$output" == *'manager=brew-cask restart=not-running'* ]]
grep -Fx 'upgrade --cask claude-code' "$TEST_EVENTS" >/dev/null
rm -rf "$TEST_ROOT/Caskroom"
fixture

# Run the public controller through an SSH substitute, including the shipped
# helper payload and disabled-device filtering. First host fails; second runs.
TEST_STATE="$HOME/.local/share/claude-switch"
mkdir -p "$TEST_STATE"
jq -n '{schemaVersion:1,nextAccountID:2,accounts:[{id:1,email:"test@example.com"}],devices:
  ["offline","online","disabled"] | to_entries | map({id:.value,displayName:.value,
   host:"example.invalid",port:22,username:"tester",authentication:"openSSHConfig",
   hasPassword:false,hasKeyPassphrase:false,enabled:(.value != "disabled")})}' >"$TEST_STATE/config.json"
chmod 600 "$TEST_STATE/config.json"
cat >"$TEST_ROOT/ssh" <<'SH'
#!/bin/bash
cat >"$TEST_PAYLOAD"
[[ "${!#}" == 'bash -l -s -- __node update-claude' ]] || exit 63
printf 'call\n' >>"$TEST_EVENTS"
if [ "$(wc -l <"$TEST_EVENTS" | tr -d ' ')" = 1 ]; then echo 'connection failed' >&2; exit 255; fi
printf 'before=1.0.0 after=2.0.0 manager=npm restart=not-running\n'
SH
chmod +x "$TEST_ROOT/ssh"
: >"$TEST_EVENTS"
rc=0
output=$(CLAUDE_SWITCH_STATE_ROOT="$TEST_STATE" CLAUDE_SWITCH_SSH_BIN="$TEST_ROOT/ssh" TEST_PAYLOAD="$TEST_ROOT/payload" \
  /bin/bash "$ROOT/Support/claude-switch" update-claude all) || rc=$?
[ "$rc" = 2 ]
printf '%s\n' "$output" | jq -se 'length == 2 and .[0].deviceID == "offline" and .[0].exitStatus == 255 and .[1].deviceID == "online" and .[1].exitStatus == 0' >/dev/null
cmp "$TEST_ROOT/payload" "$ROOT/Support/claude-switch"
export HOME="$TEST_ORIGINAL_HOME"

printf 'Remote Claude Code update tests passed (npm, native, Homebrew, failure isolation, validation, unsupported path, SSH batch).\n'
