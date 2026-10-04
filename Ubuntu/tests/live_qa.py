#!/usr/bin/env python3
"""Opt-in live account-switch QA. Default: read-only preflight; never imports credentials.

Run from a supervisor that survives Codex desktop restart. --expected is JSON keyed
by registered profile ID, each containing accountIdSha256 and emailSha256. Obtain
these hashes from the canonical credentials independently before running this test.
Only --execute permits service apply. The evidence journal contains hashes only.
Execution requires a separate systemd user unit named codex-syncbar-live-qa-*.service.
The baseline is taken after provisioning; this tool does not restore bootstrap changes
or original token bytes, and never performs bootstrap itself.
"""
import argparse
from datetime import datetime, timezone
import hashlib
import json
import os
from pathlib import Path
import re
import signal
import socket
import stat
import subprocess
import sys
import time
import uuid


class Failure(Exception):
    def __init__(self, code, details=None):
        super().__init__(code)
        self.details = details or {}


def safe_gtk_diagnostics(response):
    """Keep structural capture state, never arbitrary exception text or UI data."""
    result = {}
    state = response.get('driverState')
    if not isinstance(state, dict):
        return result
    if type(state.get('step')) is int:
        result['step'] = state['step']
    capture = state.get('capture')
    if isinstance(capture, dict):
        safe = {}
        for key in ('attempts', 'width', 'height'):
            if type(capture.get(key)) is int:
                safe[key] = capture[key]
        for key in ('mapped', 'visible', 'realized', 'paintableNode', 'childMapped', 'childNode', 'renderNode', 'contentVisible'):
            if type(capture.get(key)) is bool:
                safe[key] = capture[key]
        if capture.get('source') in ('pending', 'Gtk.WidgetPaintable', 'Gtk.Widget.snapshot_child'):
            safe['source'] = capture['source']
        result['capture'] = safe
    operation = response.get('operation')
    if isinstance(operation, dict):
        identifier = operation.get('id')
        if isinstance(identifier, str) and re.fullmatch(r'[a-zA-Z0-9_-]{1,80}', identifier):
            result['operationId'] = identifier
        if operation.get('state') in ('completed', 'failed', 'recoveryRequired', 'cancelled'):
            result['operationState'] = operation['state']
    return result



def ensure_independent_supervisor(cgroup_path="/proc/self/cgroup"):
    groups = Path(cgroup_path).read_text()
    match = re.search(r"/(codex-syncbar-live-qa-[A-Za-z0-9_.-]+\.service)(?:/|$)", groups, re.MULTILINE)
    if not match:
        raise Failure("execute_requires_dedicated_systemd_user_service")
    return match.group(1)


def process_start_ticks(entry):
    # Linux stat field 22; comm may itself contain spaces and parentheses.
    fields = (entry / "stat").read_text().rsplit(")", 1)[1].split()
    return int(fields[19])


def desktop_processes(codex_home, proc_root="/proc"):
    """Observe packaged roots using the same fail-closed scope proof as the service.

    Electron can overwrite its environment and flatten its command line. A root
    without HOME is scoped only by its packaged app-server descendant, never by
    this observer's HOME. Every PID and parent edge is rechecked before reporting.
    """
    entries = {}
    for entry in Path(proc_root).iterdir():
        if not entry.name.isdigit():
            continue
        try:
            if entry.stat().st_uid != os.getuid():
                continue
            fields = (entry / "stat").read_text().rsplit(")", 1)[1].split()
            entries[int(entry.name)] = {"entry": entry, "parent": int(fields[1]),
                "start": int(fields[19]), "executable": os.readlink(entry / "exe"),
                "arguments": (entry / "cmdline").read_bytes().replace(b"\0", b" ").decode(errors="replace")}
        except (OSError, ValueError, IndexError):
            continue

    def still_same(item):
        try:
            fields = (item["entry"] / "stat").read_text().rsplit(")", 1)[1].split()
            return (item["entry"].stat().st_uid == os.getuid() and int(fields[1]) == item["parent"] and
                int(fields[19]) == item["start"] and os.readlink(item["entry"] / "exe") == item["executable"])
        except (OSError, ValueError, IndexError):
            return False

    def scope(item):
        try:
            environment = {}
            for field in (item["entry"] / "environ").read_bytes().split(b"\0"):
                for key in (b"CODEX_HOME", b"HOME"):
                    if field.startswith(key + b"="):
                        value = os.fsdecode(field[len(key) + 1:])
                        if key in environment and environment[key] != value:
                            raise Failure("desktop_scope_ambiguous")
                        environment[key] = value
            home = environment.get(b"CODEX_HOME") or (os.path.join(environment[b"HOME"], ".codex") if environment.get(b"HOME") else None)
            if home and not os.path.isabs(home):
                raise Failure("desktop_scope_ambiguous")
            return os.path.abspath(home) if home else None
        except PermissionError:
            raise Failure("desktop_home_unreadable") from None
        except (FileNotFoundError, ProcessLookupError):
            return None

    processes = {}
    for pid, root in entries.items():
        executable = root["executable"]
        packaged = (executable == "/usr/lib/chatgpt/ChatGPT" or
            Path(executable).name in ("codex", "Codex", "codex-desktop", "ChatGPT") and
            executable.startswith(("/opt/Codex/", "/opt/codex/")))
        if not packaged or re.search(r"(?:^|\s)--type=", root["arguments"]):
            continue
        ancestor, seen, nested = root["parent"], {pid}, False
        while ancestor in entries and ancestor not in seen:
            seen.add(ancestor)
            if entries[ancestor]["executable"] == executable:
                nested = True
                break
            ancestor = entries[ancestor]["parent"]
        if nested:
            continue
        observed = {scope(root)} - {None}
        proof, unknown_descendant = [root], False
        app_server = str(Path(executable).parent / "resources" / "codex")
        for child_pid, child in entries.items():
            if child["executable"] != app_server or not re.search(r"(?:^|\s)app-server(?:\s|$)", child["arguments"]):
                continue
            listen = re.findall(r"(?:^|\s)--listen(?:=|\s+)(\S+)", child["arguments"])
            if any(not address.startswith("unix://") for address in listen):
                continue
            chain, ancestor, seen = [child], child["parent"], {child_pid}
            while ancestor != pid and ancestor in entries and ancestor not in seen:
                seen.add(ancestor)
                middle = entries[ancestor]
                if middle["executable"] != executable:
                    break
                chain.append(middle)
                ancestor = middle["parent"]
            if ancestor != pid:
                continue
            child_scope = scope(child)
            proof.extend(chain)
            if child_scope:
                observed.add(child_scope)
            else:
                unknown_descendant = True
        if not all(still_same(item) for item in proof):
            continue
        if not observed or unknown_descendant:
            raise Failure("desktop_scope_unproven")
        if len(observed) != 1:
            raise Failure("desktop_scope_ambiguous")
        if next(iter(observed)) != os.path.abspath(codex_home):
            continue
        identity = hashlib.sha256(f"{executable}\0{pid}\0{root['start']}".encode()).hexdigest()
        processes.setdefault(executable, []).append({"pid": pid, "startTimeTicks": root["start"], "identitySha256": identity})
    return {name: sorted(values, key=lambda item: item["pid"]) for name, values in processes.items()}


def desktop_state(codex_home, proc_root="/proc"):
    return {name: len(values) for name, values in desktop_processes(codex_home, proc_root).items()}


def verify_desktop_restart(before, after, expected):
    if not expected or any({name: len(values) for name, values in sample.items()} != expected for sample in (before, after)):
        raise Failure("desktop_restart_scope_mismatch")
    previous = {item["identitySha256"] for values in before.values() for item in values}
    current = {item["identitySha256"] for values in after.values() for item in values}
    if previous & current:
        raise Failure("desktop_restart_not_observed")


def wait_for_desktop_state(codex_home, expected, timeout=15):
    deadline = time.monotonic() + timeout
    stable_since = None
    while time.monotonic() < deadline:
        if desktop_state(codex_home) == expected:
            if not expected:
                return True
            stable_since = stable_since or time.monotonic()
            if time.monotonic() - stable_since >= 2:
                return True
        else:
            stable_since = None
        time.sleep(0.25)
    return False


# Runs on each target, so raw remote auth never leaves that target. SDK account/read
# currently exposes type/email/planType, not workspace account ID. Verify both the
# local auth account ID and the independent SDK account type/email without claiming
# that the SDK response proves a workspace ID it does not expose.
PROBE = r'''
import hashlib, json, os, pathlib, selectors, shutil, stat, subprocess, sys, time
class ProbeFailure(Exception): pass
def digest(value):
    return hashlib.sha256(value.encode()).hexdigest() if isinstance(value, str) and value else None
def run():
    home = pathlib.Path(os.environ.get("CODEX_HOME", str(pathlib.Path.home()/".codex")))
    path = home/"auth.json"
    info = path.lstat()
    if not stat.S_ISREG(info.st_mode) or info.st_uid != os.getuid() or info.st_mode & 0o077 or info.st_nlink != 1:
        raise RuntimeError()
    auth = json.loads(path.read_bytes())
    tokens = auth.get("tokens") or {}
    if auth.get("auth_mode") != "chatgpt" or auth.get("OPENAI_API_KEY") or not tokens.get("account_id"):
        raise RuntimeError()
    if os.environ.get("CODEX_SYNCBAR_QA_ACCESS_ONLY")=="1" and tokens.get("refresh_token"):
        raise ProbeFailure("remote_baseline_contains_refresh_token")
    cli = shutil.which("codex")
    if not cli and pathlib.Path("/usr/lib/chatgpt/resources/codex").is_file():
        cli = "/usr/lib/chatgpt/resources/codex"
    if not cli:
        raise RuntimeError()
    child = subprocess.Popen([cli, "-c", 'cli_auth_credentials_store="file"', "app-server", "--listen", "stdio://"],
        stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL)
    def send(message):
        child.stdin.write(json.dumps(message).encode()+b"\n"); child.stdin.flush()
    selector = selectors.DefaultSelector()
    selector.register(child.stdout, selectors.EVENT_READ)
    pending = b""
    def receive(wanted):
        nonlocal pending
        deadline = time.monotonic()+40
        while time.monotonic()<deadline:
            while b"\n" in pending:
                line, pending = pending.split(b"\n",1)
                item=json.loads(line)
                if item.get("id")==wanted:
                    if "error" in item: raise RuntimeError()
                    return item["result"]
            if selector.select(max(0,deadline-time.monotonic())):
                chunk=os.read(child.stdout.fileno(),65536)
                if not chunk: raise RuntimeError()
                pending+=chunk
                if len(pending)>1048576: raise RuntimeError()
        raise RuntimeError()
    try:
        send({"id":1,"method":"initialize","params":{"clientInfo":{"name":"syncbar-live-qa","version":"1"},"capabilities":{}}})
        receive(1)
        send({"method":"initialized","params":{}})
        send({"id":2,"method":"account/read","params":{"refreshToken":False}})
        account=receive(2).get("account") or {}
        if account.get("type")!="chatgpt" or not account.get("email"): raise RuntimeError()
        # Confirm account/read did not switch the file while the probe was running.
        after=json.loads(path.read_bytes())
        if after.get("auth_mode")!="chatgpt" or after.get("tokens",{}).get("account_id")!=tokens["account_id"]:
            raise RuntimeError()
        return {"accountIdSha256":digest(tokens["account_id"]),"emailSha256":digest(account["email"].strip().lower()),
            "authMode":"chatgpt","sdkAccountType":account["type"],"sdkWorkspaceIdExposed":bool(account.get("id")),
            "hasRefreshToken":bool(tokens.get("refresh_token")),"fileMode":oct(stat.S_IMODE(info.st_mode))}
    finally:
        selector.close()
        child.terminate()
        try: child.wait(timeout=5)
        except subprocess.TimeoutExpired: child.kill(); child.wait(timeout=5)
try:
    print(json.dumps({"ok":True,"result":run()}))
except BaseException as error:
    print(json.dumps({"ok":False,"error":str(error) if isinstance(error,ProbeFailure) else "identity_probe_failed"})); sys.exit(1)
'''


def rpc(path, method, **params):
    request_id = uuid.uuid4().hex
    with socket.socket(socket.AF_UNIX, socket.SOCK_STREAM) as connection:
        connection.settimeout(120)
        connection.connect(path)
        connection.sendall(json.dumps({"id": request_id, "method": method, "params": params}).encode() + b"\n")
        stream = connection.makefile("rb")
        line = stream.readline(2 * 1024 * 1024)
        if not line.endswith(b"\n"):
            raise Failure("invalid_service_response")
        response = json.loads(line)
    if response.get("id") != request_id or not response.get("ok"):
        code = (response.get("error") or {}).get("code", "service_request_failed")
        raise Failure(code if re.fullmatch(r"[a-z_]{1,64}", str(code)) else "service_request_failed")
    return response.get("result")


def ssh_command(device):
    if device.get("authentication") not in ("openSSHConfig", "privateKey"):
        raise Failure("probe_requires_existing_ssh_key_authentication")
    host, user = device["host"], device["username"]
    if not re.fullmatch(r"[A-Za-z0-9._:]+", host) or not re.fullmatch(r"[A-Za-z0-9_][A-Za-z0-9_.-]*", user):
        raise Failure("invalid_ssh_endpoint")
    port = int(device.get("port", 22))
    if not 1 <= port <= 65535:
        raise Failure("invalid_ssh_port")
    command = ["/usr/bin/ssh", "-o", "BatchMode=yes", "-o", "StrictHostKeyChecking=yes",
               "-o", "ConnectTimeout=15", "-p", str(port)]
    if device.get("identityFile"):
        command += ["-o", "IdentitiesOnly=yes", "-i", device["identityFile"]]
    if device.get("certificateFile"):
        command += ["-o", "CertificateFile=" + device["certificateFile"]]
    return command + [user + "@" + host, "bash -l -c 'python3 -'"]


def probe(target, devices, codex_home):
    environment = dict(os.environ)
    environment["CODEX_HOME"] = codex_home
    command = ["/usr/bin/python3", "-I", "-c", PROBE] if target == "local" else ssh_command(devices[target[4:]])
    remote_probe = 'import os; os.environ["CODEX_SYNCBAR_QA_ACCESS_ONLY"]="1"\n' + PROBE
    result = subprocess.run(command, input=None if target == "local" else remote_probe.encode(), env=environment,
                            stdout=subprocess.PIPE, stderr=subprocess.DEVNULL, timeout=100)
    try:
        parsed = json.loads(result.stdout)
        if parsed.get("error") == "remote_baseline_contains_refresh_token":
            raise Failure("remote_baseline_contains_refresh_token")
        if result.returncode or not parsed.get("ok"):
            raise ValueError()
        return parsed["result"]
    except (ValueError, KeyError):
        raise Failure("identity_probe_failed") from None


def matches(actual, expected, remote=False):
    return (actual.get("authMode") == "chatgpt" and actual.get("sdkAccountType") == "chatgpt"
            and actual.get("accountIdSha256") == expected.get("accountIdSha256")
            and actual.get("emailSha256") == expected.get("emailSha256")
            and actual.get("fileMode") == "0o600"
            and (not remote or actual.get("hasRefreshToken") is False))


def gtk_apply(args, profile, targets):
    driver = Path(args.gtk_driver)
    ui = Path(args.ui_path)
    if not driver.is_absolute() or not driver.is_file() or not ui.is_absolute() or not ui.is_dir():
        raise Failure("gtk_driver_and_ui_paths_must_be_absolute")
    environment = dict(os.environ)
    environment["PYTHONPATH"] = str(ui)
    result = subprocess.run(["/usr/bin/python3", str(driver), "--socket", args.socket,
        "--profile-id", str(profile), "--expected-targets", json.dumps(sorted(targets)),
        "--timeout", str(args.operation_timeout)], env=environment, stdout=subprocess.PIPE,
        stderr=subprocess.DEVNULL, timeout=args.operation_timeout + 40)
    try:
        response = json.loads(result.stdout.splitlines()[-1])
        if not isinstance(response, dict):
            raise ValueError()
        if result.returncode or not response.get("ok"):
            allowed = {'gtk_apply_timeout', 'profile_unavailable', 'preview_not_safe',
                'duplicate_apply_not_disabled', 'apply_did_not_complete', 'local_display_mismatch',
                'device_display_mismatch', 'usage_not_verified', 'missing_usage_display_mismatch',
                'usage_display_mismatch', 'gtk_capture_unavailable', 'gtk_capture_write_failed',
                'gtk_driver_failed'}
            code = response.get('error')
            raise Failure(code if isinstance(code, str) and code in allowed else 'gtk_apply_failed',
                          safe_gtk_diagnostics(response))
        operation = response["operation"]
        if result.returncode or not response.get("ok") or operation.get("profileId") != profile or operation.get("kind") != "switch":
            raise ValueError()
        capture = response.get("capture")
        if not isinstance(capture, str) or not Path(capture).is_absolute() or not Path(capture).is_file():
            raise ValueError()
        operation["_gtkCapture"] = capture
        operation["_gtkDiagnostics"] = safe_gtk_diagnostics(response)
        usage = response.get("usageDisplay")
        if not isinstance(usage, dict) or not isinstance(usage.get("windows"), dict):
            raise ValueError()
        observed = datetime.fromisoformat(usage["updatedAt"].replace("Z", "+00:00"))
        if observed.tzinfo is None or not -60 <= (datetime.now(timezone.utc) - observed).total_seconds() <= 900:
            raise ValueError()
        windows = {}
        for name in ("session", "weekly"):
            value = usage["windows"].get(name)
            if value is None:
                windows[name] = None
                continue
            if not isinstance(value, dict) or not re.fullmatch(r"(?:[0-9]{1,3}% 남음|확인 필요)", value.get("text", "")):
                raise ValueError()
            windows[name] = {"text": value["text"], "resetsAt": value.get("resetsAt")}
        operation["_gtkUsageDisplay"] = {"updatedAt": usage["updatedAt"], "windows": windows}
        return operation
    except (ValueError, KeyError, IndexError, TypeError):
        raise Failure("gtk_apply_failed") from None


def wait_for_idle(args):
    deadline = time.monotonic() + args.operation_timeout
    while time.monotonic() < deadline:
        state = rpc(args.socket, "snapshot")
        if not state.get("isBusy"):
            if (state.get("operation") or {}).get("state") == "recoveryRequired":
                raise Failure("backend_recovery_required_before_restore")
            return
        time.sleep(1)
    raise Failure("backend_still_busy_restore_deferred")


def run(args):
    if Path(args.evidence).exists():
        raise Failure("evidence_already_exists_choose_new_path")
    socket_info = Path(args.socket).lstat()
    if not stat.S_ISSOCK(socket_info.st_mode) or socket_info.st_uid != os.getuid() or socket_info.st_mode & 0o077:
        raise Failure("unsafe_service_socket")
    supervisor_unit = ensure_independent_supervisor() if args.execute else None
    expected = json.loads(Path(args.expected).read_text())
    canonical_home = expected.get("_metadata", {}).get("codexHomeSha256")
    if canonical_home and canonical_home != hashlib.sha256(os.path.abspath(args.codex_home).encode()).hexdigest():
        raise Failure("canonical_vault_uses_different_codex_home")
    selected = [int(args.account_a), int(args.account_b)]
    if selected[0] == selected[1]:
        raise Failure("two_distinct_accounts_required")
    for profile in selected:
        item = expected.get(str(profile), {})
        if any(not re.fullmatch(r"[0-9a-f]{64}", item.get(key, "")) for key in ("accountIdSha256", "emailSha256")):
            raise Failure("canonical_identity_hashes_required")
    if expected[str(selected[0])]["accountIdSha256"] == expected[str(selected[1])]["accountIdSha256"]:
        raise Failure("two_distinct_account_identities_required")
    targets = ["local"] + ["ssh:" + name for name in args.device]
    if len(set(targets)) != len(targets):
        raise Failure("duplicate_target")
    state = rpc(args.socket, "snapshot")
    if state.get("isDemo") or state.get("isBusy") or state.get("error"):
        raise Failure("production_service_not_ready")
    accounts = {a["id"] for a in state["accounts"] if not a["isPending"] and not a["needsLogin"]}
    if not set(selected) <= accounts:
        raise Failure("registered_accounts_required")
    devices = {d["id"]: d for d in rpc(args.socket, "device.list")}
    desktop_original = desktop_state(args.codex_home)
    if not desktop_original:
        raise Failure("running_scoped_desktop_required_for_restart_qa")
    evidence = {"mode": "execute" if args.execute else "dry-run", "targets": targets,
                "sequence": selected + selected[:1], "events": [], "restored": False,
                "supervisorUnit": supervisor_unit, "desktopBaseline": desktop_original,
                "desktopRestartRequired": True,
                "applyDriver": "gtk-widget-signals-over-ipc" if getattr(args, "gtk_driver", None) else "service-rpc",
                "restorationDriver": "service-rpc",
                "baselineStage": "pre-switch-after-provisioning",
                "restorationScope": "account identity and local desktop running state; not pre-bootstrap token bytes or helper installation"}

    def record(event, **details):
        evidence["events"].append({"time": time.time(), "event": event, **details})
        destination = Path(args.evidence)
        destination.parent.mkdir(mode=0o700, parents=True, exist_ok=True)
        temporary = destination.with_name(destination.name + "." + uuid.uuid4().hex)
        with os.fdopen(os.open(temporary, os.O_CREAT | os.O_EXCL | os.O_WRONLY, 0o600), "w") as output:
            json.dump(evidence, output, indent=2)
        os.replace(temporary, destination)
        print(json.dumps({"event": event, **details}), flush=True)

    initial = rpc(args.socket, "preview", profileId=selected[0])
    active_targets = {t["id"] for t in initial["targets"] if t["included"]}
    if active_targets != set(targets) or not initial["canApply"]:
        raise Failure("enabled_targets_must_exactly_match_requested_targets")
    originals = {t["id"]: t["currentProfileId"] for t in initial["targets"] if t["included"]}
    if any(value not in accounts or str(value) not in expected for value in originals.values()):
        raise Failure("original_account_not_recoverable")
    for target in targets:
        observed = probe(target, devices, args.codex_home)
        if target != "local" and observed.get("hasRefreshToken") is not False:
            record("baseline_refused", target=target, code="remote_baseline_contains_refresh_token")
            raise Failure("remote_baseline_contains_refresh_token")
        if not matches(observed, expected[str(originals[target])], target != "local"):
            raise Failure("baseline_identity_mismatch")
        record("baseline_verified", target=target, profileId=originals[target], identity=observed)
    # Check the local-only restore contract before permitting any live mutation.
    for target, profile in originals.items():
        restore_preview = rpc(args.socket, "preview", profileId=profile, deviceId=target)
        if not restore_preview["canApply"] or [t["id"] for t in restore_preview["targets"] if t["included"]] != [target]:
            raise Failure("individual_restore_not_supported")
    if not args.execute:
        record("dry_run_complete_no_switches")
        return

    def apply(profile, target=None, restoring=False):
        # isComplete may be published just before the coordinator releases its
        # mutation lock. A subsequent preview must wait for isBusy to clear.
        wait_for_idle(args)
        params = {"profileId": profile}
        if target:
            params["deviceId"] = target
        preview = rpc(args.socket, "preview", **params)
        if not preview["canApply"]:
            raise Failure("preview_blocked")
        wanted = {target} if target else set(targets)
        if {t["id"] for t in preview["targets"] if t["included"]} != wanted:
            raise Failure("unexpected_apply_scope")
        desktop_before = None
        if not restoring and "local" in wanted and desktop_original:
            desktop_before = desktop_processes(args.codex_home)
            if {name: len(values) for name, values in desktop_before.items()} != desktop_original:
                raise Failure("desktop_running_state_changed_before_apply")
            record("desktop_before_apply", profileId=profile, processes=desktop_before)
        through_gtk = getattr(args, "gtk_driver", None) and not restoring
        operation = gtk_apply(args, profile, wanted) if through_gtk else rpc(args.socket, "apply", previewId=preview["previewId"], requestId=uuid.uuid4().hex)
        capture = operation.pop("_gtkCapture", None)
        usage_display = operation.pop("_gtkUsageDisplay", None)
        gtk_diagnostics = operation.pop("_gtkDiagnostics", None)
        if through_gtk:
            operation_id = operation["id"]
            operation = rpc(args.socket, "operation", operationId=operation_id)
            if operation.get("id") != operation_id or operation.get("profileId") != profile or operation.get("kind") != "switch":
                raise Failure("gtk_operation_does_not_match_service")
        deadline = time.monotonic() + args.operation_timeout
        while not operation.get("isComplete"):
            if time.monotonic() >= deadline:
                raise Failure("operation_timeout_wait_for_recovery")
            time.sleep(0.5)
            operation = rpc(args.socket, "operation", operationId=operation["id"])
        if operation["state"] != "completed":
            raise Failure("operation_" + operation["state"])
        wait_for_idle(args)
        details = {"profileId": profile, "targets": sorted(wanted), "operationId": operation["id"]}
        if capture:
            details["capture"] = capture
        if usage_display:
            details["usageDisplay"] = usage_display
        if gtk_diagnostics:
            details["gtkDiagnostics"] = gtk_diagnostics
        record("gtk_apply_completed" if through_gtk else "service_apply_completed", **details)
        if not restoring and "local" in wanted:
            if not wait_for_desktop_state(args.codex_home, desktop_original):
                raise Failure("desktop_running_state_changed")
            if desktop_before is not None:
                desktop_after = desktop_processes(args.codex_home)
                verify_desktop_restart(desktop_before, desktop_after, desktop_original)
                record("desktop_restart_verified", profileId=profile, operationId=operation["id"],
                    before=desktop_before, after=desktop_after,
                    scope="same Codex home desktop process replacement; not network readiness")
        for current in wanted:
            observed = probe(current, devices, args.codex_home)
            if not matches(observed, expected[str(profile)], current != "local"):
                raise Failure("post_apply_identity_mismatch")
            record("identity_verified", target=current, profileId=profile, identity=observed)

    failure = None
    try:
        record("live_sequence_started")
        for profile in selected + selected[:1]:
            apply(profile)
    except BaseException as error:
        failure = error
        record("sequence_failed", code=str(error) if isinstance(error, Failure) else "interrupted_or_failed",
               diagnostics=error.details if isinstance(error, Failure) else {})
    finally:
        # Never overwrite the original baseline with a partially completed state.
        # If the backend requires recovery, do not bypass it with direct auth writes.
        record("restoration_started")
        try:
            wait_for_idle(args)
        except BaseException:
            record("restoration_blocked_by_running_operation_or_recovery")
            raise Failure("manual_recovery_required_see_evidence") from None
        restore_errors = []
        for target, profile in originals.items():
            try:
                wait_for_idle(args)
                observed = probe(target, devices, args.codex_home)
                if matches(observed, expected[str(profile)], target != "local"):
                    # The final A step or backend rollback may already have
                    # restored this target. Independent file + SDK proof avoids
                    # unnecessary mutation and another desktop restart.
                    record("baseline_identity_already_restored", target=target, profileId=profile, identity=observed)
                else:
                    apply(profile, target, restoring=True)
            except BaseException as error:
                restore_errors.append(target)
                record("restore_requires_attention", target=target,
                    code=str(error) if isinstance(error, Failure) else "restore_failed")
        try:
            desktop_current = desktop_state(args.codex_home)
            if desktop_original and not desktop_current:
                rpc(args.socket, "open.codex")
            if not wait_for_desktop_state(args.codex_home, desktop_original):
                raise Failure("desktop_restore_incomplete")
            record("desktop_running_state_restored", desktop=desktop_original)
        except BaseException:
            restore_errors.append("local-desktop")
            record("restore_requires_attention", target="local-desktop")
        evidence["restored"] = not restore_errors
        record("restoration_complete" if not restore_errors else "restoration_incomplete")
        if restore_errors:
            raise Failure("manual_recovery_required_see_evidence")
    if failure:
        raise Failure("live_sequence_failed_originals_restored")
    record("live_sequence_passed_originals_restored")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--socket", required=True)
    parser.add_argument("--expected", required=True, help="Canonical profile-ID to SHA256 metadata JSON")
    parser.add_argument("--account-a", type=int, required=True)
    parser.add_argument("--account-b", type=int, required=True)
    parser.add_argument("--device", action="append", required=True, help="Exact registered SSH device ID; repeat")
    parser.add_argument("--codex-home", default=os.environ.get("CODEX_HOME", str(Path.home() / ".codex")))
    parser.add_argument("--evidence", required=True)
    parser.add_argument("--operation-timeout", type=int, default=300)
    parser.add_argument("--gtk-driver", help="Optional application-owned GTK signal driver script for the forward sequence")
    parser.add_argument("--ui-path", help="Installed UI package directory required with --gtk-driver")
    parser.add_argument("--execute", action="store_true", help="Permit actual service-coordinated switches from a dedicated systemd user service")
    args = parser.parse_args()
    if bool(args.gtk_driver) != bool(args.ui_path):
        parser.error("--gtk-driver and --ui-path must be supplied together")
    def interrupted(_signal, _frame):
        raise Failure("interrupted")
    signal.signal(signal.SIGTERM, interrupted)
    try:
        run(args)
        return 0
    except BaseException as error:
        print(json.dumps({"ok": False, "error": str(error) if isinstance(error, Failure) else "preflight_or_probe_failed"}), flush=True)
        return 1


if __name__ == "__main__":
    sys.exit(main())
