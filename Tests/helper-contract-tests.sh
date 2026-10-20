#!/usr/bin/env bash

set -euo pipefail

file_mode() {
  case "$(uname -s)" in
    Darwin) stat -f '%Lp' "$1" ;;
    *) stat -c '%a' "$1" ;;
  esac
}

ROOT=$(cd "$(dirname "$0")/.." && pwd)
HELPER_REPOSITORY_SOURCE="$ROOT/Support/claude-switch"
ASKPASS_REPOSITORY_SOURCE="$ROOT/Support/claude-syncbar-askpass"
USAGE_REPOSITORY_SOURCE="$ROOT/Support/usage-summary.mjs"
TMP=$(mktemp -d "${TMPDIR:-/tmp}/claude-syncbar-helper.XXXXXX")
TEST_PROCESS_PIDS=()
cleanup() {
  local pid
  for pid in "${TEST_PROCESS_PIDS[@]-}"; do
    kill -KILL "$pid" >/dev/null 2>&1 || true
  done
  rm -rf "$TMP"
}
trap cleanup EXIT

# Git records only the executable bit, not the distinction between 0755 and
# the owner-only 0700 mode required by the installed Keychain bridge. Exercise
# both scripts with the secured copies that build-app.sh places in the bundle.
HELPER="$TMP/claude-switch"
ASKPASS_SOURCE="$TMP/claude-syncbar-askpass"
USAGE_SOURCE="$TMP/usage-summary.mjs"
cp "$HELPER_REPOSITORY_SOURCE" "$HELPER"
cp "$ASKPASS_REPOSITORY_SOURCE" "$ASKPASS_SOURCE"
cp "$USAGE_REPOSITORY_SOURCE" "$USAGE_SOURCE"
chmod 700 "$HELPER" "$ASKPASS_SOURCE" "$USAGE_SOURCE"

node "$ROOT/Tests/usage-summary-tests.mjs"
bash "$ROOT/Tests/remote-claude-update-tests.sh"
bash "$ROOT/Tests/termux-lock-tests.sh"
bash "$ROOT/Tests/usage-controller-concurrency-tests.sh"

HOME_DIR="$TMP/home"
STATE="$HOME_DIR/.local/share/claude-switch"
CLAUDE="$HOME_DIR/.claude"
mkdir -p "$STATE/profiles" "$CLAUDE" "$HOME_DIR/.ssh"
chmod 700 "$STATE" "$STATE/profiles" "$CLAUDE" "$HOME_DIR/.ssh"

# The helper stores every login in this canonical form, so fixtures written
# the same way can be compared with its files byte for byte.
login_json() {
  jq -S -c -n --arg account "$1" --arg access "$2" --arg refresh "$3" '{
    claudeAiOauth:{accessToken:$access,refreshToken:$refresh,expiresAt:4102444800000,scopes:["user:inference","user:profile"]},
    oauthAccount:{accountUuid:$account,emailAddress:($account + "@example.com")}
  }'
}

write_auth() {
  local id="$1"
  login_json "account-$id" "access-$id" "refresh-$id" >"$STATE/profiles/$id.auth.json"
  chmod 600 "$STATE/profiles/$id.auth.json"
}

# Claude Code reads a login from two files; unrelated entries in them must survive.
set_active() {
  local source="$1" credentials="${2:-$CLAUDE/.credentials.json}" config="${3:-$CLAUDE/.claude.json}"
  jq --slurpfile login "$source" '.claudeAiOauth = $login[0].claudeAiOauth' \
    <<<"$(cat "$credentials" 2>/dev/null || printf '{}')" >"$credentials.next"
  jq --slurpfile login "$source" '.oauthAccount = $login[0].oauthAccount' \
    <<<"$(cat "$config" 2>/dev/null || printf '{}')" >"$config.next"
  mv "$credentials.next" "$credentials"
  mv "$config.next" "$config"
  chmod 600 "$credentials" "$config"
}

active_login() {
  local credentials="${1:-$CLAUDE/.credentials.json}" config="${2:-$CLAUDE/.claude.json}"
  jq -S -c -n --slurpfile credentials "$credentials" --slurpfile config "$config" \
    '{claudeAiOauth:$credentials[0].claudeAiOauth,oauthAccount:$config[0].oauthAccount}'
}

active_sha() {
  active_login "$@" | shasum -a 256 | awk '{print $1}'
}

active_is() {
  local expected="$1"
  shift
  [ "$(cat "$expected")" = "$(active_login "$@")" ]
}

write_auth 1
write_auth 2
write_auth 3
printf '{"mcpOAuth":{"fixture":"kept"}}\n' >"$CLAUDE/.credentials.json"
printf '{"numStartups":7}\n' >"$CLAUDE/.claude.json"
set_active "$STATE/profiles/3.auth.json"
printf '3\n' >"$STATE/current"
chmod 600 "$STATE/current"

node_output=$(HOME="$HOME_DIR" CLAUDE_SWITCH_STATE_ROOT="$STATE" CLAUDE_CONFIG_DIR="$CLAUDE" "$HELPER" __node preflight 3)
case "$node_output" in *"active=3"*) ;; *) printf 'missing dynamic profile result: %s\n' "$node_output" >&2; exit 1 ;; esac

IDENTITY="$HOME_DIR/.ssh/id_ed25519"
CERTIFICATE="$HOME_DIR/.ssh/id_ed25519-cert.pub"
printf 'private-key-placeholder\n' >"$IDENTITY"
printf 'certificate-placeholder\n' >"$CERTIFICATE"
chmod 600 "$IDENTITY"
chmod 644 "$CERTIFICATE"

jq -n \
  --arg identity "$IDENTITY" \
  --arg certificate "$CERTIFICATE" \
  '{schemaVersion:1,nextAccountID:4,accounts:[
      {id:1,email:"one@example.com"},{id:2,email:"two@example.com"},{id:3,email:"three@example.com"}
    ],devices:[{
      id:"build-server",displayName:"빌드 서버",host:"10.0.0.20",port:2222,username:"builder",
      credentialID:"A1B2C3D4-E5F6-4A7B-8C9D-001122334455",
      authentication:"privateKey",identityFile:$identity,certificateFile:$certificate,
      hasPassword:false,hasKeyPassphrase:true,enabled:true
    },{
      id:"legacy-node",displayName:"기존 노드",host:"10.0.0.21",port:22,username:"legacy",
      authentication:"openSSHConfig",identityFile:null,certificateFile:null,
      hasPassword:false,hasKeyPassphrase:false,enabled:true
    },{
      id:"password-node",displayName:"비밀번호 노드",host:"10.0.0.22",port:22,username:"passworduser",
      credentialID:"C1D2E3F4-A5B6-4C7D-8E9F-223344556677",
      authentication:"password",identityFile:null,certificateFile:null,
      hasPassword:true,hasKeyPassphrase:false,enabled:true
    },{
      id:"telemetry-node",displayName:"텔레메트리 노드",host:"10.0.0.23",port:22,username:"telemetry",
      authentication:"openSSHConfig",identityFile:null,certificateFile:null,
      hasPassword:false,hasKeyPassphrase:false,enabled:true
    },{
      id:"staging-node",displayName:"스테이징 노드",host:"10.0.0.24",port:22,username:"staging",
      credentialID:"B1C2D3E4-F5A6-4B7C-8D9E-112233445566",
      authentication:"openSSHConfig",identityFile:null,certificateFile:null,
      hasPassword:false,hasKeyPassphrase:false,enabled:false
    }]}' >"$STATE/config.json"
chmod 600 "$STATE/config.json"
cp "$STATE/config.json" "$STATE/config.valid.json"
chmod 600 "$STATE/config.valid.json"

FAKE_SSH="$TMP/fake-ssh"
cat >"$FAKE_SSH" <<'SH'
#!/usr/bin/env bash
if [ -n "${CLAUDE_SWITCH_TEST_SSH_SLEEP:-}" ]; then sleep "$CLAUDE_SWITCH_TEST_SSH_SLEEP"; fi
printf '%s\n' "$@" >"$CLAUDE_SWITCH_TEST_SSH_ARGS"
printf '%s\n' "$*" >>"$CLAUDE_SWITCH_TEST_SSH_CALLS"
printf '%s\n' "${CLAUDE_SYNCBAR_CREDENTIAL_ID:-}" >"$CLAUDE_SWITCH_TEST_CREDENTIAL_ID"
cat >"$CLAUDE_SWITCH_TEST_SSH_STDIN"
if [ -n "${CLAUDE_SWITCH_TEST_FAIL_TARGET:-}" ]; then
  case " $* " in
    *" $CLAUDE_SWITCH_TEST_FAIL_TARGET "*" __node install-access "*|*" $CLAUDE_SWITCH_TEST_FAIL_TARGET "*" __node inspect-access "*) exit 42 ;;
  esac
fi
if [ -n "${CLAUDE_SWITCH_TEST_LIVE_FALLBACK_TARGET:-}" ]; then
  case " $* " in
    *" $CLAUDE_SWITCH_TEST_LIVE_FALLBACK_TARGET "*" __node install-access "*) exit 42 ;;
    *" $CLAUDE_SWITCH_TEST_LIVE_FALLBACK_TARGET "*" __node stop-clients "*) exit 43 ;;
  esac
fi
case " $* " in
  *" __node status "*) printf 'active=3 fingerprint=abcdef123456 mode=600 auth_mode=claudeai cli=logged-in credential=access-only access_fp=123456abcdef expires_at=4102444800\n' ;;
  *" __node version "*) printf '2.1.3\n' ;;
  *" __node initialize "*) printf 'active=3 fingerprint=abcdef123456 state=initialized\n' ;;
  *" __node verify "*) printf 'active=3 fingerprint=abcdef123456\n' ;;
  *".bootstrap."*) printf 'bootstrap=installed\n' ;;
  *) printf 'version=2.1.3 active=3 active_fp=abcdef123456 target_fp=abcdef123456\n' ;;
esac
SH
chmod 700 "$FAKE_SSH"
FAKE_ASKPASS="$TMP/fake-askpass"
printf '#!/usr/bin/env bash\nprintf "test-secret\\n"\n' >"$FAKE_ASKPASS"
chmod 700 "$FAKE_ASKPASS"
SSH_ARGS="$TMP/ssh-args"
SSH_CALLS="$TMP/ssh-calls"
SSH_STDIN="$TMP/ssh-stdin"
CREDENTIAL_ID="$TMP/credential-id"

FAKE_REFRESH="$TMP/refresh-auth.mjs"
printf 'fake refresh helper\n' >"$FAKE_REFRESH"
chmod 700 "$FAKE_REFRESH"
FAKE_NODE="$TMP/fake-node"
cat >"$FAKE_NODE" <<'SH'
#!/usr/bin/env bash
set -euo pipefail
shift
action="$1"
shift
short_hash() {
  printf '%s' "$1" | shasum -a 256 | awk '{print substr($1,1,12)}'
}
case "$action" in
  inspect)
    file="$1"
    account=$(jq -er '.oauthAccount.accountUuid' "$file")
    access=$(jq -er '.claudeAiOauth.accessToken' "$file")
    refresh=$(jq -er '.claudeAiOauth.refreshToken' "$file")
    remaining="${CLAUDE_SWITCH_TEST_REMAINING_SECONDS:-2000000000}"
    jq -cn \
      --arg accountFingerprint "$(short_hash "$account")" \
      --arg accessFingerprint "$(short_hash "$access")" \
      --arg refreshFingerprint "$(short_hash "$refresh")" \
      --argjson remainingSeconds "$remaining" \
      '{accountFingerprint:$accountFingerprint,accessFingerprint:$accessFingerprint,
        refreshFingerprint:$refreshFingerprint,expiresAt:4102444800,
        remainingSeconds:$remainingSeconds,lastRefreshEpoch:1}'
    ;;
  sanitize)
    jq -S -c '.claudeAiOauth.refreshToken = ""' "$1" >"$2"
    chmod 600 "$2"
    ;;
  *) exit 2 ;;
esac
SH
chmod 700 "$FAKE_NODE"

common_env=(
  HOME="$HOME_DIR"
  CLAUDE_SWITCH_STATE_ROOT="$STATE"
  CLAUDE_CONFIG_DIR="$CLAUDE"
  CLAUDE_SWITCH_CONFIG_FILE="$STATE/config.json"
  CLAUDE_SWITCH_SSH_BIN="$FAKE_SSH"
  CLAUDE_SWITCH_ASKPASS_HELPER="$FAKE_ASKPASS"
  CLAUDE_SWITCH_REFRESH_HELPER="$FAKE_REFRESH"
  CLAUDE_SWITCH_USAGE_HELPER="$USAGE_SOURCE"
  CLAUDE_SWITCH_NODE_BIN="$FAKE_NODE"
  CLAUDE_SWITCH_TEST_SSH_ARGS="$SSH_ARGS"
  CLAUDE_SWITCH_TEST_SSH_CALLS="$SSH_CALLS"
  CLAUDE_SWITCH_TEST_SSH_STDIN="$SSH_STDIN"
  CLAUDE_SWITCH_TEST_CREDENTIAL_ID="$CREDENTIAL_ID"
)

# A remote can report the current controller version while still carrying an
# older usage helper. The controller must repair that schema mismatch before it
# exposes the remote total to the app-wide aggregate.
USAGE_CONFIG="$TMP/usage-config.json"
jq -n '{schemaVersion:1,nextAccountID:4,accounts:[
    {id:1,email:"one@example.com"},{id:2,email:"two@example.com"},{id:3,email:"three@example.com"}
  ],devices:[{
    id:"usage-node",displayName:"사용량 노드",host:"10.0.0.30",port:22,username:"usage",
    authentication:"openSSHConfig",identityFile:null,certificateFile:null,
    hasPassword:false,hasKeyPassphrase:false,enabled:true
  }]}' >"$USAGE_CONFIG"
chmod 600 "$USAGE_CONFIG"
USAGE_REMOTE_STATE="$TMP/usage-remote-installed"
USAGE_SSH="$TMP/usage-ssh"
cat >"$USAGE_SSH" <<'SH'
#!/usr/bin/env bash
set -euo pipefail
case " $* " in
  *" __node version "*) printf '2.1.3\n' ;;
  *" __node usage-summary "*)
    if [ -f "$CLAUDE_SWITCH_TEST_USAGE_REMOTE_STATE" ]; then
      printf '%s\n' '{"schemaVersion":6,"generatedAt":"2026-08-03T00:00:00.000Z","scannedFiles":1,"requests":2,"inputTokens":18,"cachedInputTokens":0,"cacheWriteInputTokens":0,"outputTokens":2,"reasoningOutputTokens":0,"totalTokens":20,"buckets":[],"errors":[]}'
    else
      printf '%s\n' '{"schemaVersion":4,"generatedAt":"2026-08-03T00:00:00.000Z","scannedFiles":1,"requests":99,"inputTokens":990,"cachedInputTokens":0,"cacheWriteInputTokens":0,"outputTokens":9,"reasoningOutputTokens":0,"totalTokens":999,"buckets":[],"errors":[]}'
    fi
    ;;
  *".bootstrap."*)
    cat >/dev/null
    printf 'installed\n' >"$CLAUDE_SWITCH_TEST_USAGE_REMOTE_STATE"
    printf 'bootstrap=installed\n'
    ;;
  *) exit 2 ;;
esac
SH
chmod 700 "$USAGE_SSH"
mkdir -p "$CLAUDE/projects"
usage_output=$(env "${common_env[@]}" \
  CLAUDE_SWITCH_CONFIG_FILE="$USAGE_CONFIG" \
  CLAUDE_SWITCH_SSH_BIN="$USAGE_SSH" \
  CLAUDE_SWITCH_NODE_BIN="$(command -v node)" \
  CLAUDE_SWITCH_TEST_USAGE_REMOTE_STATE="$USAGE_REMOTE_STATE" \
  "$HELPER" usage-summary)
printf '%s\n' "$usage_output" | jq -e -s '
  length == 2 and .[0].id == "macbook" and .[0].summary.schemaVersion == 6 and
  .[1].id == "usage-node" and .[1].isReachable == true and
  .[1].summary.schemaVersion == 6 and .[1].summary.totalTokens == 20
' >/dev/null
[ -f "$USAGE_REMOTE_STATE" ]

status_json=$(env "${common_env[@]}" "$HELPER" status-json)
printf '%s\n' "$status_json" | jq -e -s '
  length == 5 and .[0].id == "macbook" and .[1].id == "build-server" and
  .[1].profileID == 3 and .[2].id == "legacy-node" and .[3].id == "password-node" and
  .[4].id == "telemetry-node" and all(.[]; .id != "staging-node")
' >/dev/null

chmod 755 "$FAKE_ASKPASS"
if env "${common_env[@]}" "$HELPER" test-device build-server >"$TMP/insecure-askpass.out" 2>&1; then
  printf 'insecure askpass helper was accepted\n' >&2
  exit 1
fi
grep -F 'askpass helper permissions are unsafe' "$TMP/insecure-askpass.out" >/dev/null
chmod 700 "$FAKE_ASKPASS"

env "${common_env[@]}" "$HELPER" test-device build-server >/dev/null
grep -Fx -- '-i' "$SSH_ARGS" >/dev/null
grep -Fx -- "$IDENTITY" "$SSH_ARGS" >/dev/null
grep -Fx -- '-p' "$SSH_ARGS" >/dev/null
grep -Fx -- '2222' "$SSH_ARGS" >/dev/null
grep -F -- "CertificateFile=$CERTIFICATE" "$SSH_ARGS" >/dev/null
grep -Fx -- 'BatchMode=no' "$SSH_ARGS" >/dev/null
[ "$(cat "$CREDENTIAL_ID")" = "A1B2C3D4-E5F6-4A7B-8C9D-001122334455" ]

env "${common_env[@]}" "$HELPER" test-device password-node >/dev/null
[ "$(cat "$CREDENTIAL_ID")" = "C1D2E3F4-A5B6-4C7D-8E9F-223344556677" ]
grep -Fx -- 'PubkeyAuthentication=no' "$SSH_ARGS" >/dev/null
grep -Fx -- 'PreferredAuthentications=password' "$SSH_ARGS" >/dev/null
grep -Fx -- 'NumberOfPasswordPrompts=1' "$SSH_ARGS" >/dev/null

FAKE_ASKPASS_LINK="$TMP/fake-askpass-link"
ln -s "$FAKE_ASKPASS" "$FAKE_ASKPASS_LINK"
if env "${common_env[@]}" CLAUDE_SWITCH_ASKPASS_HELPER="$FAKE_ASKPASS_LINK" \
  "$HELPER" test-device password-node >"$TMP/symlink-askpass.out" 2>&1; then
  printf 'symlinked askpass helper was accepted\n' >&2
  exit 1
fi
grep -F 'askpass helper is unsafe' "$TMP/symlink-askpass.out" >/dev/null

env "${common_env[@]}" "$HELPER" test-device staging-node >/dev/null
grep -Fx -- 'staging@10.0.0.24' "$SSH_ARGS" >/dev/null

set +e
sync_output=$(env "${common_env[@]}" CLAUDE_SWITCH_TEST_FAIL_TARGET='telemetry@10.0.0.23' \
  "$HELPER" sync-access 1 2>&1)
sync_rc=$?
set -e
[ "$sync_rc" -eq 2 ]
printf '%s\n' "$sync_output" | grep -E 'profile=1 .*synced=3 pending=1 result=partial' >/dev/null
printf '%s\n' "$sync_output" | grep -F 'overall=partial pending=1' >/dev/null

set +e
sync_output=$(env "${common_env[@]}" CLAUDE_SWITCH_TEST_LIVE_FALLBACK_TARGET='legacy@10.0.0.21' \
  "$HELPER" sync-access 1 2>&1)
sync_rc=$?
set -e
[ "$sync_rc" -eq 2 ]
printf '%s\n' "$sync_output" | grep -E 'profile=1 .*synced=3 pending=1 result=partial' >/dev/null
printf '%s\n' "$sync_output" | grep -F 'overall=partial pending=1' >/dev/null

ACCESS_ONLY="$TMP/access-only-new.json"
jq -S -c '.claudeAiOauth.accessToken = "access-new" | .claudeAiOauth.refreshToken = ""' \
  "$STATE/profiles/3.auth.json" >"$ACCESS_ONLY"
chmod 600 "$ACCESS_ONLY"
account_fp=$(printf 'account-3' | shasum -a 256 | awk '{print substr($1,1,12)}')
access_fp=$(printf 'access-new' | shasum -a 256 | awk '{print substr($1,1,12)}')

FAKE_PROCESS_BIN="$TMP/fake-process-bin"
mkdir -p "$FAKE_PROCESS_BIN"
cat >"$FAKE_PROCESS_BIN/mv" <<'SH'
#!/usr/bin/env bash
if [ -n "${CLAUDE_SWITCH_TEST_MV_SLEEP:-}" ]; then sleep "$CLAUDE_SWITCH_TEST_MV_SLEEP"; fi
source_path=""
destination=""
for argument in "$@"; do
  source_path="$destination"
  destination="$argument"
done
# Installing the new access token into Claude Code's credential file fails...
if [ "${CLAUDE_SWITCH_TEST_FAIL_ACCESS_INSTALL:-0}" = 1 ] && \
   [ "$destination" = "$CLAUDE_SWITCH_TEST_CREDENTIALS_FILE" ] && \
   grep -F '"access-new"' "$source_path" >/dev/null 2>&1; then
  exit 90
fi
# ...and optionally so does putting the previous login back.
if [ "${CLAUDE_SWITCH_TEST_FAIL_ACCESS_ROLLBACK:-0}" = 1 ] && \
   { [ "$destination" = "$CLAUDE_SWITCH_TEST_PROFILE_FILE" ] || [ "$destination" = "$CLAUDE_SWITCH_TEST_CREDENTIALS_FILE" ]; } && \
   grep -F '"access-3"' "$source_path" >/dev/null 2>&1; then
  exit 91
fi
exec /bin/mv "$@"
SH
chmod 700 "$FAKE_PROCESS_BIN/mv"

# A switch replaces only the login entries of Claude Code's two files.
env "${common_env[@]}" "$HELPER" __node switch 2 1 >/dev/null
[ "$(cat "$STATE/current")" = 2 ]
active_is "$STATE/profiles/2.auth.json"
[ "$(jq -r '.mcpOAuth.fixture' "$CLAUDE/.credentials.json")" = kept ]
[ "$(jq -r '.numStartups' "$CLAUDE/.claude.json")" = 7 ]
[ "$(file_mode "$CLAUDE/.credentials.json")" = 600 ]
env "${common_env[@]}" "$HELPER" __node switch 3 0 >/dev/null
active_is "$STATE/profiles/3.auth.json"
stop_output=$(env "${common_env[@]}" "$HELPER" __node stop-clients)
[ "$stop_output" = 'stopped_processes=0 forced_processes=0' ]

profile_before=$(shasum -a 256 "$STATE/profiles/3.auth.json" | awk '{print $1}')
active_before=$(active_sha)
if env "${common_env[@]}" PATH="$FAKE_PROCESS_BIN:$PATH" \
  CLAUDE_SWITCH_TEST_FAIL_ACCESS_INSTALL=1 CLAUDE_SWITCH_TEST_CREDENTIALS_FILE="$CLAUDE/.credentials.json" \
  "$HELPER" __node install-access 3 "$account_fp" "$access_fp" \
  <"$ACCESS_ONLY" >"$TMP/install-rollback.out" 2>&1; then
  printf 'access install succeeded although the credential file could not be replaced\n' >&2
  exit 1
fi
grep -F 'could not install active auth' "$TMP/install-rollback.out" >/dev/null
[ "$profile_before" = "$(shasum -a 256 "$STATE/profiles/3.auth.json" | awk '{print $1}')" ]
[ "$active_before" = "$(active_sha)" ]
if find "$STATE" -maxdepth 1 -type d -name '.install-access.3.*' | grep -q .; then
  printf 'successful access rollback left a stale backup\n' >&2
  exit 1
fi

if env "${common_env[@]}" PATH="$FAKE_PROCESS_BIN:$PATH" \
  CLAUDE_SWITCH_TEST_FAIL_ACCESS_INSTALL=1 CLAUDE_SWITCH_TEST_FAIL_ACCESS_ROLLBACK=1 \
  CLAUDE_SWITCH_TEST_PROFILE_FILE="$STATE/profiles/3.auth.json" CLAUDE_SWITCH_TEST_CREDENTIALS_FILE="$CLAUDE/.credentials.json" \
  "$HELPER" __node install-access 3 "$account_fp" "$access_fp" \
  <"$ACCESS_ONLY" >"$TMP/double-rollback-failure.out" 2>&1; then
  printf 'access install succeeded after both rollback copies failed\n' >&2
  exit 1
fi
grep -F 'rollback failed; backup preserved at' "$TMP/double-rollback-failure.out" >/dev/null
access_backup=$(find "$STATE" -maxdepth 1 -type d -name '.install-access.3.*' -print -quit)
[ -n "$access_backup" ]
[ "$profile_before" = "$(shasum -a 256 "$access_backup/profile.auth.json" | awk '{print $1}')" ]
[ "$active_before" = "$(shasum -a 256 "$access_backup/active.auth.json" | awk '{print $1}')" ]
cp "$access_backup/profile.auth.json" "$STATE/profiles/3.auth.json"
set_active "$access_backup/active.auth.json"
chmod 600 "$STATE/profiles/3.auth.json"
rm -rf "$access_backup"

BOOTSTRAP_REMOTE_HOME="$TMP/bootstrap-remote-home"
mkdir -p "$BOOTSTRAP_REMOTE_HOME"
chmod 700 "$BOOTSTRAP_REMOTE_HOME"
BOOTSTRAP_SSH="$TMP/bootstrap-ssh"
cat >"$BOOTSTRAP_SSH" <<'SH'
#!/usr/bin/env bash
set -euo pipefail
remote_command=""
for remote_command in "$@"; do :; done
[ -n "$remote_command" ]
if [ -n "${CLAUDE_SWITCH_TEST_FAIL_PATTERN:-}" ]; then
  case "$remote_command" in
    *"$CLAUDE_SWITCH_TEST_FAIL_PATTERN"*) exit 97 ;;
  esac
fi
remote_home="$CLAUDE_SWITCH_TEST_REMOTE_HOME"
mkdir -p "$remote_home/.local/share/claude-switch" "$remote_home/.claude"
chmod 700 "$remote_home" "$remote_home/.local/share/claude-switch" "$remote_home/.claude"
# No CLAUDE_CONFIG_DIR: a remote device uses Claude Code's default file layout.
env -u CLAUDE_CONFIG_DIR HOME="$remote_home" \
  CLAUDE_SWITCH_STATE_ROOT="$remote_home/.local/share/claude-switch" \
  /bin/bash -c "$remote_command"
SH
chmod 700 "$BOOTSTRAP_SSH"

ROLLBACK_REMOTE_HOME="$TMP/bootstrap-rollback-home"
mkdir -p "$ROLLBACK_REMOTE_HOME/.local/bin" "$ROLLBACK_REMOTE_HOME/.local/lib/claude-switch"
printf 'previous-helper\n' >"$ROLLBACK_REMOTE_HOME/.local/bin/claude-switch"
printf 'link-target\n' >"$ROLLBACK_REMOTE_HOME/askpass-target"
ln -s "$ROLLBACK_REMOTE_HOME/askpass-target" \
  "$ROLLBACK_REMOTE_HOME/.local/lib/claude-switch/claude-syncbar-askpass"
rm -f "$ROLLBACK_REMOTE_HOME/askpass-target"
chmod 755 "$ROLLBACK_REMOTE_HOME/.local/bin/claude-switch"
if env "${common_env[@]}" CLAUDE_SWITCH_SSH_BIN="$BOOTSTRAP_SSH" \
  CLAUDE_SWITCH_TEST_REMOTE_HOME="$ROLLBACK_REMOTE_HOME" \
  "$HELPER" bootstrap-device staging-node >"$TMP/bootstrap-rollback.out" 2>&1; then
  printf 'bootstrap accepted an unsafe existing remote askpass\n' >&2
  exit 1
fi
grep -Fx 'previous-helper' "$ROLLBACK_REMOTE_HOME/.local/bin/claude-switch" >/dev/null
[ -L "$ROLLBACK_REMOTE_HOME/.local/lib/claude-switch/claude-syncbar-askpass" ]

bootstrap_output=$(env "${common_env[@]}" \
  CLAUDE_SWITCH_SSH_BIN="$BOOTSTRAP_SSH" CLAUDE_SWITCH_TEST_REMOTE_HOME="$BOOTSTRAP_REMOTE_HOME" \
  "$HELPER" bootstrap-device staging-node)
printf '%s\n' "$bootstrap_output" | grep -F 'device=staging-node result=ok active=3 profiles=3' >/dev/null
cmp -s "$HELPER" "$BOOTSTRAP_REMOTE_HOME/.local/bin/claude-switch"
cmp -s "$FAKE_ASKPASS" "$BOOTSTRAP_REMOTE_HOME/.local/lib/claude-switch/claude-syncbar-askpass"
cmp -s "$USAGE_SOURCE" "$BOOTSTRAP_REMOTE_HOME/.local/lib/claude-switch/usage-summary.mjs"
[ "$(file_mode "$BOOTSTRAP_REMOTE_HOME/.local/bin/claude-switch")" = 755 ]
[ "$(file_mode "$BOOTSTRAP_REMOTE_HOME/.local/lib/claude-switch/claude-syncbar-askpass")" = 700 ]
[ "$(cat "$BOOTSTRAP_REMOTE_HOME/.local/share/claude-switch/current")" = 3 ]
for profile in 1 2 3; do
  jq -e '.claudeAiOauth.refreshToken == ""' \
    "$BOOTSTRAP_REMOTE_HOME/.local/share/claude-switch/profiles/$profile.auth.json" >/dev/null
done
active_is "$BOOTSTRAP_REMOTE_HOME/.local/share/claude-switch/profiles/3.auth.json" \
  "$BOOTSTRAP_REMOTE_HOME/.claude/.credentials.json" "$BOOTSTRAP_REMOTE_HOME/.claude.json"

bootstrap_tree_manifest() {
  (
    cd "$BOOTSTRAP_REMOTE_HOME"
    find .local/bin/claude-switch .local/lib/claude-switch/claude-syncbar-askpass .local/lib/claude-switch/usage-summary.mjs \
      .local/share/claude-switch .claude/.credentials.json .claude.json -type f -exec shasum -a 256 {} + 2>/dev/null | sort
    find .local/bin/claude-switch .local/lib/claude-switch/claude-syncbar-askpass .local/lib/claude-switch/usage-summary.mjs \
      .local/share/claude-switch .claude/.credentials.json .claude.json \( -type f -o -type d \) \
      -exec bash -c 'for f do case "$(uname -s)" in Darwin) stat -f "%N %Lp" "$f";; *) stat -c "%n %a" "$f";; esac; done' _ {} + 2>/dev/null | sort
  )
}
bootstrap_before_failure=$(bootstrap_tree_manifest)
if env "${common_env[@]}" \
  CLAUDE_SWITCH_SSH_BIN="$BOOTSTRAP_SSH" CLAUDE_SWITCH_TEST_REMOTE_HOME="$BOOTSTRAP_REMOTE_HOME" \
  CLAUDE_SWITCH_TEST_FAIL_PATTERN='__node install-access 2 ' \
  "$HELPER" bootstrap-device staging-node >"$TMP/bootstrap-failure.out" 2>&1; then
  printf 'bootstrap profile failure unexpectedly succeeded\n' >&2
  exit 1
fi
bootstrap_after_failure=$(bootstrap_tree_manifest)
[ "$bootstrap_after_failure" = "$bootstrap_before_failure" ]
if find "$STATE" -maxdepth 1 -name '.bootstrap-remote-backup.staging-node.*.tar' -print -quit | grep -q .; then
  printf 'successful bootstrap rollback left a recovery archive\n' >&2
  exit 1
fi

bootstrap_before_corrupt_restore=$(bootstrap_tree_manifest)
staging_endpoint_fp=$(jq -cn \
  --arg target 'staging@10.0.0.24' --argjson port 22 \
  --arg authentication openSSHConfig --arg identity '' --arg certificate '' \
  '{target:$target,port:$port,authentication:$authentication,identity:$identity,certificate:$certificate}' | \
  shasum -a 256 | awk '{print $1}')
staging_recovery="$STATE/.bootstrap-remote-backup.staging-node.$staging_endpoint_fp.tar"
printf 'not-a-valid-tar-archive\n' >"$staging_recovery"
chmod 600 "$staging_recovery"
if env "${common_env[@]}" \
  CLAUDE_SWITCH_SSH_BIN="$BOOTSTRAP_SSH" CLAUDE_SWITCH_TEST_REMOTE_HOME="$BOOTSTRAP_REMOTE_HOME" \
  "$HELPER" bootstrap-device staging-node >"$TMP/bootstrap-corrupt-restore.out" 2>&1; then
  printf 'bootstrap accepted a corrupt recovery archive\n' >&2
  exit 1
fi
bootstrap_after_corrupt_restore=$(bootstrap_tree_manifest)
[ "$bootstrap_after_corrupt_restore" = "$bootstrap_before_corrupt_restore" ]
[ -f "$staging_recovery" ]
[ "$(file_mode "$staging_recovery")" = 600 ]
rm -f "$staging_recovery"

# A preserved recovery archive must never be uploaded after the same device
# ID is edited to point at another SSH endpoint.
foreign_recovery="$STATE/.bootstrap-remote-backup.staging-node.aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa.tar"
printf 'preserved-for-old-host\n' >"$foreign_recovery"
chmod 600 "$foreign_recovery"
jq '(.devices[] | select(.id == "staging-node") | .host) = "10.0.0.25"' \
  "$STATE/config.valid.json" >"$STATE/config.json"
chmod 600 "$STATE/config.json"
if env "${common_env[@]}" CLAUDE_SWITCH_SSH_BIN="$BOOTSTRAP_SSH" \
  CLAUDE_SWITCH_TEST_REMOTE_HOME="$BOOTSTRAP_REMOTE_HOME" \
  "$HELPER" bootstrap-device staging-node >"$TMP/bootstrap-host-binding.out" 2>&1; then
  printf 'bootstrap restored an archive to a changed SSH endpoint\n' >&2
  exit 1
fi
grep -F 'different or unbound SSH endpoint' "$TMP/bootstrap-host-binding.out" >/dev/null
[ -f "$foreign_recovery" ]
rm -f "$foreign_recovery"
cp "$STATE/config.valid.json" "$STATE/config.json"
chmod 600 "$STATE/config.json"

auth_before=$(active_sha)
profiles_before=$(find "$STATE/profiles" -type f -name '*.auth.json' -exec shasum -a 256 {} + | sort)
printf '{not-json\n' >"$STATE/config.json"
chmod 600 "$STATE/config.json"
if env "${common_env[@]}" "$HELPER" status-json >"$TMP/malformed.out" 2>&1; then
  printf 'malformed configuration was accepted\n' >&2
  exit 1
fi
grep -F 'configuration is invalid' "$TMP/malformed.out" >/dev/null
[ "$auth_before" = "$(active_sha)" ]
[ "$profiles_before" = "$(find "$STATE/profiles" -type f -name '*.auth.json' -exec shasum -a 256 {} + | sort)" ]
cp "$STATE/config.valid.json" "$STATE/config.json"
chmod 600 "$STATE/config.json"

jq '.devices[0].displayName = "Build Server"' \
  "$STATE/config.valid.json" >"$STATE/config.json"
chmod 600 "$STATE/config.json"
env "${common_env[@]}" "$HELPER" status-json >/dev/null

for rejected_name in $'Build\nServer' $'Build\tServer' $'Build\x7fServer'; do
  jq --arg display_name "$rejected_name" '.devices[0].displayName = $display_name' \
    "$STATE/config.valid.json" >"$STATE/config.json"
  chmod 600 "$STATE/config.json"
  if env "${common_env[@]}" "$HELPER" status-json >"$TMP/invalid-display-name.out" 2>&1; then
    printf 'control character in SSH display name was accepted\n' >&2
    exit 1
  fi
  grep -F 'configuration is invalid' "$TMP/invalid-display-name.out" >/dev/null
done
cp "$STATE/config.valid.json" "$STATE/config.json"
chmod 600 "$STATE/config.json"

jq '.devices[2].credentialID = .devices[0].credentialID' \
  "$STATE/config.valid.json" >"$STATE/config.json"
chmod 600 "$STATE/config.json"
if env "${common_env[@]}" "$HELPER" status-json >"$TMP/duplicate-credential.out" 2>&1; then
  printf 'duplicate SSH credential IDs were accepted\n' >&2
  exit 1
fi
grep -F 'configuration is invalid' "$TMP/duplicate-credential.out" >/dev/null

jq 'del(.devices[2].credentialID)' "$STATE/config.valid.json" >"$STATE/config.json"
chmod 600 "$STATE/config.json"
if env "${common_env[@]}" "$HELPER" status-json >"$TMP/missing-credential.out" 2>&1; then
  printf 'password device without a credential ID was accepted\n' >&2
  exit 1
fi
grep -F 'configuration is invalid' "$TMP/missing-credential.out" >/dev/null
cp "$STATE/config.valid.json" "$STATE/config.json"
chmod 600 "$STATE/config.json"

env "${common_env[@]}" CLAUDE_SWITCH_TEST_SSH_SLEEP=0.15 \
  "$HELPER" status-json >"$TMP/lock-holder.out" 2>&1 &
lock_holder_pid=$!
attempt=0
while [ ! -f "$STATE/.controller-lock" ] && [ "$attempt" -lt 100 ]; do
  sleep 0.01
  attempt=$((attempt + 1))
done
[ -f "$STATE/.controller-lock" ]
[ ! -L "$STATE/.controller-lock" ]
[ "$(file_mode "$STATE/.controller-lock")" = 600 ]
first_lock_token=$(sed -n 's/^token=//p' "$STATE/.controller-lock")
[ -n "$first_lock_token" ]
if env "${common_env[@]}" "$HELPER" status-json >"$TMP/concurrent-lock.out" 2>&1; then
  printf 'a second controller command acquired a live lock\n' >&2
  exit 1
fi
grep -F 'another controller operation is already running' "$TMP/concurrent-lock.out" >/dev/null
[ "$first_lock_token" = "$(sed -n 's/^token=//p' "$STATE/.controller-lock")" ]
wait "$lock_holder_pid"
[ ! -e "$STATE/.controller-lock" ]
[ -f "$STATE/.controller-gate" ]
[ ! -L "$STATE/.controller-gate" ]
[ "$(file_mode "$STATE/.controller-gate")" = 600 ]

env "${common_env[@]}" PATH="$FAKE_PROCESS_BIN:$PATH" CLAUDE_SWITCH_TEST_MV_SLEEP=0.15 \
  "$HELPER" __node switch 3 >"$TMP/node-lock-holder.out" 2>&1 &
node_lock_holder_pid=$!
attempt=0
while [ ! -f "$STATE/.lock" ] && [ "$attempt" -lt 100 ]; do
  sleep 0.01
  attempt=$((attempt + 1))
done
[ -f "$STATE/.lock" ]
first_node_lock_token=$(sed -n 's/^token=//p' "$STATE/.lock")
if env "${common_env[@]}" "$HELPER" __node switch 3 >"$TMP/concurrent-node-lock.out" 2>&1; then
  printf 'a second node command acquired a live switch lock\n' >&2
  exit 1
fi
grep -F 'another switch operation is already running' "$TMP/concurrent-node-lock.out" >/dev/null
[ "$first_node_lock_token" = "$(sed -n 's/^token=//p' "$STATE/.lock")" ]
wait "$node_lock_holder_pid"
[ ! -e "$STATE/.lock" ]
[ -f "$STATE/.lock-gate" ]
[ ! -L "$STATE/.lock-gate" ]
[ "$(file_mode "$STATE/.lock-gate")" = 600 ]

printf 'pid=999991\ntoken=dead-file\n' >"$STATE/.controller-lock"
chmod 600 "$STATE/.controller-lock"
env "${common_env[@]}" "$HELPER" status-json >/dev/null
[ ! -e "$STATE/.controller-lock" ]

mkdir "$STATE/.controller-lock"
chmod 700 "$STATE/.controller-lock"
printf 'pid=2147483647\n' >"$STATE/.controller-lock/owner"
chmod 600 "$STATE/.controller-lock/owner"
env "${common_env[@]}" "$HELPER" status-json >/dev/null
[ ! -e "$STATE/.controller-lock" ]

mkdir "$STATE/.controller-lock"
chmod 700 "$STATE/.controller-lock"
printf 'pid=2147483647\ntoken=not-a-legacy-owner\n' >"$STATE/.controller-lock/owner"
chmod 600 "$STATE/.controller-lock/owner"
if env "${common_env[@]}" "$HELPER" status-json >"$TMP/unsafe-legacy-owner.out" 2>&1; then
  printf 'a non-legacy directory owner format was migrated\n' >&2
  exit 1
fi
grep -F 'unsafe legacy controller lock directory' "$TMP/unsafe-legacy-owner.out" >/dev/null
rm -f "$STATE/.controller-lock/owner"
rmdir "$STATE/.controller-lock"

sleep 5 &
legacy_lock_holder_pid=$!
mkdir "$STATE/.controller-lock"
chmod 700 "$STATE/.controller-lock"
printf 'pid=%s\n' "$legacy_lock_holder_pid" >"$STATE/.controller-lock/owner"
chmod 600 "$STATE/.controller-lock/owner"
if env "${common_env[@]}" "$HELPER" status-json >"$TMP/legacy-live-lock.out" 2>&1; then
  printf 'a live legacy controller lock was migrated\n' >&2
  exit 1
fi
grep -F 'another controller operation is already running' "$TMP/legacy-live-lock.out" >/dev/null
grep -Fx "pid=$legacy_lock_holder_pid" "$STATE/.controller-lock/owner" >/dev/null
kill "$legacy_lock_holder_pid" 2>/dev/null || true
wait "$legacy_lock_holder_pid" 2>/dev/null || true
rm -f "$STATE/.controller-lock/owner"
rmdir "$STATE/.controller-lock"

mkdir "$STATE/.controller-lock"
chmod 700 "$STATE/.controller-lock"
if env "${common_env[@]}" "$HELPER" status-json >"$TMP/unsafe-ownerless-lock.out" 2>&1; then
  printf 'an ownerless legacy controller lock was accepted\n' >&2
  exit 1
fi
grep -F 'unsafe legacy controller lock directory' "$TMP/unsafe-ownerless-lock.out" >/dev/null
[ -d "$STATE/.controller-lock" ]
rmdir "$STATE/.controller-lock"

ln -s "$TMP" "$STATE/.controller-lock"
if env "${common_env[@]}" "$HELPER" status-json >"$TMP/symlink-lock.out" 2>&1; then
  printf 'a symlinked controller lock was accepted\n' >&2
  exit 1
fi
grep -F 'refusing symlinked controller lock' "$TMP/symlink-lock.out" >/dev/null
[ -L "$STATE/.controller-lock" ]
rm "$STATE/.controller-lock"

printf 'pid=999991\ntoken=dead-node-file\n' >"$STATE/.lock"
chmod 600 "$STATE/.lock"
env "${common_env[@]}" "$HELPER" __node switch 3 >/dev/null
[ ! -e "$STATE/.lock" ]

env "${common_env[@]}" CLAUDE_SWITCH_TEST_SSH_SLEEP=0.15 \
  "$HELPER" status-json >"$TMP/token-release.out" 2>&1 &
token_release_pid=$!
attempt=0
while [ ! -f "$STATE/.controller-lock" ] && [ "$attempt" -lt 100 ]; do
  sleep 0.01
  attempt=$((attempt + 1))
done
[ -f "$STATE/.controller-lock" ]
replacement_owner="$STATE/.controller-lock.replacement"
printf 'pid=%s\ntoken=replacement\n' "$token_release_pid" >"$replacement_owner"
chmod 600 "$replacement_owner"
mv -f "$replacement_owner" "$STATE/.controller-lock"
wait "$token_release_pid"
grep -F 'controller lock ownership changed; refusing to remove it' "$TMP/token-release.out" >/dev/null
grep -Fx 'token=replacement' "$STATE/.controller-lock" >/dev/null
rm -f "$STATE/.controller-lock"

accounts_json=$(env "${common_env[@]}" "$HELPER" accounts-json)
printf '%s\n' "$accounts_json" | jq -e 'map(.id) == [1,2,3]' >/dev/null

COLLECTING_LOGIN="$STATE/login-transactions/1.collecting-test"
mkdir -p "$COLLECTING_LOGIN"
chmod 700 "$COLLECTING_LOGIN"
printf 'state=collecting profile=1 pid=999991\n' >"$COLLECTING_LOGIN/manifest"
chmod 600 "$COLLECTING_LOGIN/manifest"
recovery_output=$(env "${common_env[@]}" "$HELPER" recover-controller)
printf '%s\n' "$recovery_output" | grep -F \
  'login_recovery=ok logout_recovery=ok overall=ok' >/dev/null
[ ! -e "$COLLECTING_LOGIN" ]

PREPARED_LOGIN="$STATE/login-transactions/3.prepared-test"
mkdir -p "$PREPARED_LOGIN"
chmod 700 "$PREPARED_LOGIN"
cp "$STATE/profiles/3.auth.json" "$PREPARED_LOGIN/profile.auth.json"
active_login >"$PREPARED_LOGIN/active.auth.json"
cp "$STATE/current" "$PREPARED_LOGIN/current"
login_json account-recovery-incoming access-recovery-incoming refresh-recovery-incoming >"$PREPARED_LOGIN/incoming.auth.json"
chmod 600 "$PREPARED_LOGIN"/*.json "$PREPARED_LOGIN/current"
incoming_sha=$(shasum -a 256 "$PREPARED_LOGIN/incoming.auth.json" | awk '{print $1}')
profile_backup_sha=$(shasum -a 256 "$PREPARED_LOGIN/profile.auth.json" | awk '{print $1}')
active_backup_sha=$(shasum -a 256 "$PREPARED_LOGIN/active.auth.json" | awk '{print $1}')
current_backup_sha=$(shasum -a 256 "$PREPARED_LOGIN/current" | awk '{print $1}')
printf 'state=prepared profile=3 pid=999992 profile_existed=1 active=1 current_existed=1 incoming_sha=%s profile_backup_sha=%s active_backup_sha=%s current_backup_sha=%s\n' \
  "$incoming_sha" "$profile_backup_sha" "$active_backup_sha" "$current_backup_sha" \
  >"$PREPARED_LOGIN/manifest"
chmod 600 "$PREPARED_LOGIN/manifest"
cp "$PREPARED_LOGIN/incoming.auth.json" "$STATE/profiles/3.auth.json"
set_active "$PREPARED_LOGIN/incoming.auth.json"
printf '3\n' >"$STATE/current"
chmod 600 "$STATE/profiles/3.auth.json" "$STATE/current"
# Any public status boundary must recover the prepared login before it can
# inspect node state; recover-controller is not a required manual precursor.
env "${common_env[@]}" "$HELPER" status-json >/dev/null
[ ! -e "$PREPARED_LOGIN" ]
[ "$(shasum -a 256 "$STATE/profiles/3.auth.json" | awk '{print $1}')" = "$profile_backup_sha" ]
[ "$(active_sha)" = "$active_backup_sha" ]
[ "$(shasum -a 256 "$STATE/current" | awk '{print $1}')" = "$current_backup_sha" ]

DEVICE_ACTIVATION="$STATE/device-activation-transactions"
mkdir -p "$DEVICE_ACTIVATION"
chmod 700 "$DEVICE_ACTIVATION"
activation_temp="$DEVICE_ACTIVATION/.01234567-89AB-CDEF-0123-456789ABCDEF.tmp"
printf '{}\n' >"$activation_temp"
# A crash before final intent publication leaves config disabled; the exact
# UUID temp is cleaned while the controller gate is held.
env "${common_env[@]}" "$HELPER" test-device staging-node >/dev/null
[ ! -e "$activation_temp" ]

printf '{}\n' >"$DEVICE_ACTIVATION/activation.json"
chmod 600 "$DEVICE_ACTIVATION/activation.json"
activation_auth_sha=$(active_sha)
activation_profiles_sha=$(find "$STATE/profiles" -type f -name '*.auth.json' -exec shasum -a 256 {} + | sort)
if env "${common_env[@]}" "$HELPER" test-device staging-node >"$TMP/activation-guard.out" 2>&1; then
  printf 'controller mutation ran during device activation verification\n' >&2
  exit 1
fi
grep -F 'device activation verification is in progress' "$TMP/activation-guard.out" >/dev/null
# status-json is the one read-only boundary required for post-enable proof.
env "${common_env[@]}" "$HELPER" status-json >/dev/null
[ "$activation_auth_sha" = "$(active_sha)" ]
[ "$activation_profiles_sha" = "$(find "$STATE/profiles" -type f -name '*.auth.json' -exec shasum -a 256 {} + | sort)" ]
rm -rf "$DEVICE_ACTIVATION"

AMBIGUOUS_LOGIN="$STATE/login-transactions/3.ambiguous-test"
mkdir -p "$AMBIGUOUS_LOGIN"
chmod 700 "$AMBIGUOUS_LOGIN"
cp "$STATE/profiles/3.auth.json" "$AMBIGUOUS_LOGIN/profile.auth.json"
active_login >"$AMBIGUOUS_LOGIN/active.auth.json"
cp "$STATE/current" "$AMBIGUOUS_LOGIN/current"
cp "$PREPARED_LOGIN/incoming.auth.json" "$AMBIGUOUS_LOGIN/incoming.auth.json" 2>/dev/null || \
  login_json account-recovery-incoming access-recovery-incoming refresh-recovery-incoming >"$AMBIGUOUS_LOGIN/incoming.auth.json"
chmod 600 "$AMBIGUOUS_LOGIN"/*.json "$AMBIGUOUS_LOGIN/current"
incoming_sha=$(shasum -a 256 "$AMBIGUOUS_LOGIN/incoming.auth.json" | awk '{print $1}')
profile_backup_sha=$(shasum -a 256 "$AMBIGUOUS_LOGIN/profile.auth.json" | awk '{print $1}')
active_backup_sha=$(shasum -a 256 "$AMBIGUOUS_LOGIN/active.auth.json" | awk '{print $1}')
current_backup_sha=$(shasum -a 256 "$AMBIGUOUS_LOGIN/current" | awk '{print $1}')
printf 'state=prepared profile=3 pid=999993 profile_existed=1 active=1 current_existed=1 incoming_sha=%s profile_backup_sha=%s active_backup_sha=%s current_backup_sha=%s\n' \
  "$incoming_sha" "$profile_backup_sha" "$active_backup_sha" "$current_backup_sha" \
  >"$AMBIGUOUS_LOGIN/manifest"
chmod 600 "$AMBIGUOUS_LOGIN/manifest"
login_json account-unrelated access-unrelated refresh-unrelated >"$STATE/profiles/3.auth.json"
chmod 600 "$STATE/profiles/3.auth.json"
ambiguous_sha=$(shasum -a 256 "$STATE/profiles/3.auth.json" | awk '{print $1}')
set +e
env "${common_env[@]}" "$HELPER" recover-controller >"$TMP/ambiguous-login-recovery.out" 2>&1
ambiguous_rc=$?
set -e
[ "$ambiguous_rc" -eq 2 ]
[ -d "$AMBIGUOUS_LOGIN" ]
[ "$(shasum -a 256 "$STATE/profiles/3.auth.json" | awk '{print $1}')" = "$ambiguous_sha" ]
grep -F 'prepared login recovery was ambiguous' "$TMP/ambiguous-login-recovery.out" >/dev/null
cp "$AMBIGUOUS_LOGIN/profile.auth.json" "$STATE/profiles/3.auth.json"
set_active "$AMBIGUOUS_LOGIN/active.auth.json"
cp "$AMBIGUOUS_LOGIN/current" "$STATE/current"
chmod 600 "$STATE/profiles/3.auth.json" "$STATE/current"
rm -rf "$AMBIGUOUS_LOGIN"

LOGOUT_RECOVERY_INTENT="$STATE/controller-transactions/logout_recovery_test.intent"
printf 'state=invalid profile=1 fallback=2 target_fp=a target_access=b fallback_fp=c fallback_access=d\n' \
  >"$LOGOUT_RECOVERY_INTENT"
chmod 600 "$LOGOUT_RECOVERY_INTENT"
set +e
env "${common_env[@]}" "$HELPER" recover-controller >"$TMP/logout-recovery-dispatch.out" 2>&1
logout_recovery_rc=$?
set -e
[ "$logout_recovery_rc" -eq 2 ]
[ -f "$LOGOUT_RECOVERY_INTENT" ]
grep -F 'invalid controller logout intent state' "$TMP/logout-recovery-dispatch.out" >/dev/null
grep -F 'logout_recovery=pending' "$TMP/logout-recovery-dispatch.out" >/dev/null
rm -f "$LOGOUT_RECOVERY_INTENT"

if grep -E 'password-value|passphrase-value' "$STATE/config.json" "$SSH_ARGS" >/dev/null; then
  printf 'plaintext SSH secret leaked into persisted or argv data\n' >&2
  exit 1
fi

version=$("$HELPER" --version)
[ "$version" = "2.1.3" ]

FAKE_SECURITY="$TMP/fake-security"
SECURITY_ARGS="$TMP/security-args"
cat >"$FAKE_SECURITY" <<'SH'
#!/usr/bin/env bash
printf '%s\n' "$@" >"$CLAUDE_SWITCH_TEST_SECURITY_ARGS"
printf 'keychain-secret\n'
SH
chmod 700 "$FAKE_SECURITY"
askpass_output=$(CLAUDE_SYNCBAR_CREDENTIAL_ID='A1B2C3D4-E5F6-4A7B-8C9D-001122334455' \
  CLAUDE_SYNCBAR_SECRET_KIND=password CLAUDE_SYNCBAR_SECURITY_BIN="$FAKE_SECURITY" \
  CLAUDE_SWITCH_TEST_SECURITY_ARGS="$SECURITY_ARGS" "$ASKPASS_SOURCE")
[ "$askpass_output" = keychain-secret ]
grep -Fx -- 'A1B2C3D4-E5F6-4A7B-8C9D-001122334455.password' "$SECURITY_ARGS" >/dev/null

if env "${common_env[@]}" "$HELPER" swap-profiles >"$TMP/public-swap.out" 2>&1; then
  printf 'public swap-profiles command is still available\n' >&2
  exit 1
fi
grep -F 'unknown command: swap-profiles' "$TMP/public-swap.out" >/dev/null

case "$(file_mode "$ASKPASS_SOURCE" 2>/dev/null || stat -c '%a' "$ASKPASS_SOURCE")" in
  700) ;;
  *) printf 'source askpass helper must have mode 0700\n' >&2; exit 1 ;;
esac

printf 'helper contract tests passed\n'
