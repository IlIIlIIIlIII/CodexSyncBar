#!/usr/bin/env python3
"""Isolated supervisor contracts; never connects to a real backend or SSH host."""
import contextlib
from datetime import datetime, timezone
import hashlib
import io
import json
import os
from pathlib import Path
import socket
import subprocess
import tempfile
from types import SimpleNamespace
import unittest
from unittest.mock import patch
import live_qa
import live_qa_hashes


class SupervisorTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix="syncbar-supervisor-")
        self.root = Path(self.temporary.name)
        self.listener = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
        self.listener.bind(str(self.root / "control.sock"))
        os.chmod(self.root / "control.sock", 0o600)
        self.expected = {"1": {"accountIdSha256": "a"*64, "emailSha256": "c"*64},
                         "2": {"accountIdSha256": "b"*64, "emailSha256": "d"*64}}
        (self.root / "expected.json").write_text(json.dumps(self.expected))
        self.args = SimpleNamespace(socket=str(self.root / "control.sock"), expected=str(self.root / "expected.json"),
            account_a=1, account_b=2, device=["fixture-one", "fixture-two"], execute=False,
            evidence=str(self.root / "evidence.json"), codex_home=str(self.root / "codex"), operation_timeout=2)
        self.originals = {"local": 1, "ssh:fixture-one": 2, "ssh:fixture-two": 1}
        self.current = dict(self.originals)
        self.previews = {}
        self.applies = []
        self.gtk_operation = None
        self.gtk_generation = 0

    def tearDown(self):
        self.listener.close()
        self.temporary.cleanup()

    def rpc(self, _socket, method, **params):
        if method == "snapshot":
            return {"isBusy": False, "accounts": [{"id": n, "isPending": False, "needsLogin": False} for n in (1, 2)]}
        if method == "device.list":
            return [{"id": name} for name in self.args.device]
        if method == "preview":
            key = str(len(self.previews))
            targets = [params["deviceId"]] if "deviceId" in params else list(self.current)
            self.previews[key] = (params["profileId"], targets)
            return {"previewId": key, "canApply": True, "targets": [
                {"id": target, "included": target in targets, "currentProfileId": profile}
                for target, profile in self.current.items()]}
        if method == "apply":
            profile, targets = self.previews[params["previewId"]]
            self.applies.append((profile, targets))
            for target in targets:
                self.current[target] = profile
            return {"id": "fixture-operation", "isComplete": True, "state": "completed"}
        if method == "operation":
            return self.gtk_operation
        raise AssertionError(method)

    def probe(self, target, _devices, _home):
        return {**self.expected[str(self.current[target])], "authMode": "chatgpt", "sdkAccountType": "chatgpt",
                "hasRefreshToken": target == "local", "fileMode": "0o600"}

    def desktop_processes(self, _home):
        generation = len(self.applies) + self.gtk_generation
        return {"/usr/lib/chatgpt/ChatGPT": [{"pid": 100 + generation, "startTimeTicks": 1000 + generation,
            "identitySha256": hashlib.sha256(str(generation).encode()).hexdigest()}]}

    def invoke(self, probe=None):
        with patch.object(live_qa, "rpc", side_effect=self.rpc), patch.object(live_qa, "probe", side_effect=probe or self.probe), \
                patch.object(live_qa, "ensure_independent_supervisor", return_value="codex-syncbar-live-qa-fixture.service"), \
                patch.object(live_qa, "desktop_state", return_value={"/usr/lib/chatgpt/ChatGPT": 1}), \
                patch.object(live_qa, "desktop_processes", side_effect=self.desktop_processes), \
                patch.object(live_qa, "wait_for_desktop_state", return_value=True), contextlib.redirect_stdout(io.StringIO()):
            live_qa.run(self.args)

    def test_default_dry_run_does_not_apply(self):
        self.invoke()
        self.assertEqual([], self.applies)
        self.assertEqual(self.originals, self.current)
        evidence = json.loads((self.root / "evidence.json").read_text())
        self.assertEqual("dry_run_complete_no_switches", evidence["events"][-1]["event"])
        self.assertEqual(0o600, (self.root / "evidence.json").stat().st_mode & 0o777)

    def test_hash_export_drops_raw_fields_and_writes_private_file(self):
        result = {"profiles": [{"profileId": int(key), **value, "rawEmail": "never-export@example.test"}
            for key, value in self.expected.items()], "codexHomeSha256": "f"*64, "configurationRevision": "fixture-revision"}
        def endpoint(path, method):
            return {"isDemo": False, "isBusy": False} if method == "snapshot" else result
        with patch.object(live_qa_hashes, "rpc", side_effect=endpoint):
            summary = live_qa_hashes.export(self.args.socket, str(self.root / "hashes.json"))
        self.assertEqual([1, 2], summary["exportedProfiles"])
        output = self.root / "hashes.json"
        self.assertEqual(0o600, output.stat().st_mode & 0o777)
        self.assertNotIn("never-export", output.read_text())
        self.assertEqual(self.expected["1"], json.loads(output.read_text())["1"])

    def test_hash_export_refuses_duplicate_identity_slots(self):
        duplicate = {"profiles": [{"profileId": identifier, **self.expected["1"]} for identifier in (1, 2)], "codexHomeSha256": "f"*64}
        with self.assertRaisesRegex(live_qa.Failure, "two_registered_distinct_accounts_required"):
            live_qa_hashes.validated_export(duplicate)

    def test_success_restores_each_distinct_original(self):
        self.args.execute = True
        self.invoke()
        self.assertEqual([1, 2, 1], [value for value, targets in self.applies if len(targets) == 3])
        self.assertEqual(self.originals, self.current)
        self.assertEqual([(2, ["ssh:fixture-one"])], [(profile, targets) for profile, targets in self.applies if len(targets) == 1])
        events = json.loads((self.root / "evidence.json").read_text())["events"]
        self.assertEqual({"local", "ssh:fixture-two"}, {event["target"] for event in events if event["event"] == "baseline_identity_already_restored"})
        self.assertTrue(json.loads((self.root / "evidence.json").read_text())["restored"])

    def test_forward_sequence_uses_gtk_driver_and_restore_uses_rpc(self):
        self.args.execute = True
        self.args.gtk_driver = "/fixture/driver.py"
        self.args.ui_path = "/fixture/ui"
        gtk_profiles = []
        def driver(args, profile, targets):
            self.gtk_generation += 1
            gtk_profiles.append(profile)
            for target in targets:
                self.current[target] = profile
            self.gtk_operation = {"id": "fixture-gtk-operation", "kind": "switch", "profileId": profile,
                "isComplete": True, "state": "completed"}
            return dict(self.gtk_operation)
        with patch.object(live_qa, "gtk_apply", side_effect=driver):
            self.invoke()
        self.assertEqual([1, 2, 1], gtk_profiles)
        self.assertTrue(all(len(targets) == 1 for _, targets in self.applies))
        self.assertEqual(self.originals, self.current)

    def test_gtk_capture_and_fresh_usage_are_retained_as_evidence(self):
        driver = self.root / "driver.py"
        driver.write_text("# fixture; subprocess is mocked\n")
        capture = self.root / "capture.png"
        capture.write_bytes(b"fixture")
        self.args.gtk_driver, self.args.ui_path = str(driver), str(self.root)
        response = {"ok": True, "operation": {"id": "gtk", "kind": "switch", "profileId": 2,
            "state": "completed", "isComplete": True}, "capture": str(capture),
            "usageDisplay": {"updatedAt": datetime.now(timezone.utc).isoformat(),
                "windows": {"session": {"text": "72% 남음", "resetsAt": None}, "weekly": None}, "unexpectedSecret": "drop-me"}}
        with patch.object(live_qa.subprocess, "run", return_value=subprocess.CompletedProcess([], 0, json.dumps(response).encode())):
            operation = live_qa.gtk_apply(self.args, 2, set(self.originals))
        self.assertEqual(str(capture), operation["_gtkCapture"])
        self.assertEqual("72% 남음", operation["_gtkUsageDisplay"]["windows"]["session"]["text"])
        self.assertNotIn("unexpectedSecret", operation["_gtkUsageDisplay"])
        response["usageDisplay"]["updatedAt"] = "2000-01-01T00:00:00+00:00"
        with patch.object(live_qa.subprocess, "run", return_value=subprocess.CompletedProcess([], 0, json.dumps(response).encode())), \
                self.assertRaisesRegex(live_qa.Failure, "gtk_apply_failed"):
            live_qa.gtk_apply(self.args, 2, set(self.originals))

    def test_gtk_capture_failure_retains_safe_exact_code_and_operation(self):
        driver = self.root / "driver.py"
        driver.write_text("# mocked child\n")
        self.args.gtk_driver, self.args.ui_path = str(driver), str(self.root)
        response = {"ok": False, "error": "gtk_capture_unavailable",
            "operation": {"id": "completed-qa-op", "state": "completed"},
            "driverState": {"step": 3, "capture": {"attempts": 22, "mapped": True,
                "source": "pending", "paintableNode": False, "width": 1040,
                "exceptionMessage": "never-retain-me"}, "secret": "never-retain-me"}}
        with patch.object(live_qa.subprocess, "run", return_value=subprocess.CompletedProcess([], 1, json.dumps(response).encode())):
            with self.assertRaisesRegex(live_qa.Failure, "^gtk_capture_unavailable$") as raised:
                live_qa.gtk_apply(self.args, 2, set(self.originals))
        self.assertEqual("completed", raised.exception.details["operationState"])
        self.assertEqual(22, raised.exception.details["capture"]["attempts"])
        self.assertNotIn("never-retain-me", json.dumps(raised.exception.details))
        response["error"] = "credential-like-arbitrary-error"
        with patch.object(live_qa.subprocess, "run", return_value=subprocess.CompletedProcess([], 1, json.dumps(response).encode())):
            with self.assertRaisesRegex(live_qa.Failure, "^gtk_apply_failed$"):
                live_qa.gtk_apply(self.args, 2, set(self.originals))

    def test_restore_waits_for_timed_out_driver_operation_to_settle(self):
        self.args.execute = True
        self.args.gtk_driver = "/fixture/driver.py"
        self.args.ui_path = "/fixture/ui"
        events = []
        def timeout(args, profile, targets):
            events.append("driver-timeout")
            raise subprocess.TimeoutExpired("fixture", 1)
        def settle(args):
            events.append("idle")
        original_rpc = self.rpc
        def service(path, method, **params):
            if method == "apply":
                self.assertEqual("idle", events[-1])
            return original_rpc(path, method, **params)
        with patch.object(live_qa, "gtk_apply", side_effect=timeout), patch.object(live_qa, "wait_for_idle", side_effect=settle), \
                patch.object(self, "rpc", side_effect=service), self.assertRaisesRegex(live_qa.Failure, "live_sequence_failed_originals_restored"):
            self.invoke()
        self.assertIn("driver-timeout", events)
        self.assertEqual("idle", events[-1])
        self.assertFalse(self.applies)
        self.assertEqual(self.originals, self.current)

    def test_failed_independent_identity_check_restores_originals(self):
        self.args.execute = True
        failed_once = False
        def corrupted_probe(target, devices, home):
            nonlocal failed_once
            result = self.probe(target, devices, home)
            if len(self.applies) == 2 and not failed_once:
                failed_once = True
                result["accountIdSha256"] = "f"*64
            return result
        with self.assertRaisesRegex(live_qa.Failure, "live_sequence_failed_originals_restored"):
            self.invoke(corrupted_probe)
        self.assertTrue(failed_once)
        self.assertEqual(self.originals, self.current)

    def test_remote_refresh_token_blocks_before_any_apply(self):
        self.args.execute = True
        def unsafe_probe(target, devices, home):
            result = self.probe(target, devices, home)
            result["hasRefreshToken"] = True
            return result
        with self.assertRaisesRegex(live_qa.Failure, "remote_baseline_contains_refresh_token"):
            self.invoke(unsafe_probe)
        self.assertFalse(self.applies)

    def test_execute_refuses_desktop_cgroup_and_accepts_dedicated_service(self):
        cgroup = self.root / "cgroup"
        cgroup.write_text("0::/user.slice/user-1000.slice/user@1000.service/app.slice/app-codex.scope\n")
        with self.assertRaisesRegex(live_qa.Failure, "execute_requires_dedicated_systemd_user_service"):
            live_qa.ensure_independent_supervisor(str(cgroup))
        cgroup.write_text("0::/user.slice/user-1000.slice/user@1000.service/app.slice/codex-syncbar-live-qa-fixture.service\n")
        self.assertEqual("codex-syncbar-live-qa-fixture.service", live_qa.ensure_independent_supervisor(str(cgroup)))

    def test_desktop_observer_only_counts_same_home_root_processes(self):
        proc = self.root / "proc"
        proc.mkdir()
        for pid, executable, home, extra in [(100, "/usr/lib/chatgpt/ChatGPT", self.args.codex_home, ""),
                (101, "/usr/lib/chatgpt/ChatGPT", self.args.codex_home, "--type=renderer"),
                (102, "/usr/lib/chatgpt/ChatGPT", str(self.root / "other"), ""),
                (103, "/usr/bin/codex", self.args.codex_home, "")]:
            entry = proc / str(pid)
            entry.mkdir()
            (entry / "exe").symlink_to(executable)
            (entry / "cmdline").write_bytes((executable + "\0" + extra + "\0").encode())
            (entry / "environ").write_bytes(("CODEX_HOME=" + home + "\0SECRET=not-read\0").encode())
            (entry / "stat").write_text(f"{pid} (ChatGPT (fixture)) S " + "0 "*18 + str(pid*100) + " 0\n")
        self.assertEqual({"/usr/lib/chatgpt/ChatGPT": 1}, live_qa.desktop_state(self.args.codex_home, str(proc)))
        observed = live_qa.desktop_processes(self.args.codex_home, str(proc))
        self.assertEqual(10000, observed["/usr/lib/chatgpt/ChatGPT"][0]["startTimeTicks"])
        self.assertNotIn("SECRET", json.dumps(observed))

    def test_desktop_restart_requires_replaced_process_identity(self):
        executable = "/usr/lib/chatgpt/ChatGPT"
        before = {executable: [{"pid": 100, "startTimeTicks": 1, "identitySha256": "a"*64}]}
        # The same executable/count is insufficient if the original process persists.
        with self.assertRaisesRegex(live_qa.Failure, "desktop_restart_not_observed"):
            live_qa.verify_desktop_restart(before, before, {executable: 1})
        after = {executable: [{"pid": 100, "startTimeTicks": 2, "identitySha256": "b"*64}]}
        live_qa.verify_desktop_restart(before, after, {executable: 1})
        with self.assertRaisesRegex(live_qa.Failure, "desktop_restart_scope_mismatch"):
            live_qa.verify_desktop_restart(before, {}, {executable: 1})

    def test_each_forward_apply_records_distinct_desktop_restart(self):
        self.args.execute = True
        executable = "/usr/lib/chatgpt/ChatGPT"
        def processes(_home):
            generation = len(self.applies)
            return {executable: [{"pid": 100 + generation, "startTimeTicks": 1000 + generation,
                "identitySha256": hashlib.sha256(str(generation).encode()).hexdigest()}]}
        with patch.object(live_qa, "rpc", side_effect=self.rpc), patch.object(live_qa, "probe", side_effect=self.probe), \
                patch.object(live_qa, "desktop_state", return_value={executable: 1}), \
                patch.object(live_qa, "desktop_processes", side_effect=processes), \
                patch.object(live_qa, "wait_for_desktop_state", return_value=True), \
                patch.object(live_qa, "ensure_independent_supervisor", return_value="codex-syncbar-live-qa-fixture.service"), \
                contextlib.redirect_stdout(io.StringIO()):
            live_qa.run(self.args)
        events = json.loads((self.root / "evidence.json").read_text())["events"]
        restarts = [event for event in events if event["event"] == "desktop_restart_verified"]
        self.assertEqual([1, 2, 1], [event["profileId"] for event in restarts])
        for event in restarts:
            self.assertNotEqual(event["before"][executable][0]["identitySha256"], event["after"][executable][0]["identitySha256"])
        self.assertEqual(self.originals, self.current)

    def test_failed_desktop_restart_is_restored_through_service(self):
        self.args.execute = True
        desktop = {"/usr/lib/chatgpt/ChatGPT": 1}
        opened = []
        def service(socket_path, method, **params):
            if method == "open.codex":
                opened.append(True)
                desktop["/usr/lib/chatgpt/ChatGPT"] = 1
                return {"opened": True}
            result = self.rpc(socket_path, method, **params)
            if method == "apply" and len(self.applies) == 1:
                desktop.clear()
            return result
        with patch.object(live_qa, "rpc", side_effect=service), patch.object(live_qa, "probe", side_effect=self.probe), \
                patch.object(live_qa, "desktop_state", side_effect=lambda _home: dict(desktop)), \
                patch.object(live_qa, "desktop_processes", return_value={"/usr/lib/chatgpt/ChatGPT": [{"pid": 100, "startTimeTicks": 1, "identitySha256": "a"*64}]}), \
                patch.object(live_qa, "wait_for_desktop_state", side_effect=lambda _home, expected: desktop == expected), \
                patch.object(live_qa, "ensure_independent_supervisor", return_value="codex-syncbar-live-qa-fixture.service"), \
                contextlib.redirect_stdout(io.StringIO()), \
                self.assertRaisesRegex(live_qa.Failure, "live_sequence_failed_originals_restored"):
            live_qa.run(self.args)
        self.assertEqual([True], opened)
        self.assertEqual(self.originals, self.current)
        self.assertEqual({"/usr/lib/chatgpt/ChatGPT": 1}, desktop)
        self.assertTrue(json.loads((self.root / "evidence.json").read_text())["restored"])

    def test_missing_desktop_blocks_even_dry_run(self):
        with patch.object(live_qa, "rpc", side_effect=self.rpc), \
                patch.object(live_qa, "desktop_state", return_value={}), \
                self.assertRaisesRegex(live_qa.Failure, "running_scoped_desktop_required"):
            live_qa.run(self.args)
        self.assertFalse(self.applies)

    def test_existing_evidence_is_never_overwritten(self):
        destination = Path(self.args.evidence)
        destination.write_text("previous-run-evidence")
        with self.assertRaisesRegex(live_qa.Failure, "evidence_already_exists"):
            live_qa.run(self.args)
        self.assertEqual("previous-run-evidence", destination.read_text())

    def test_completion_waits_for_busy_lock_release_before_next_preview(self):
        self.args.execute = True
        original_rpc = self.rpc
        busy_samples = 0
        waits = []
        def service(path, method, **params):
            nonlocal busy_samples
            if method == "preview":
                self.assertEqual(0, busy_samples, "preview requested before mutation lock released")
            result = original_rpc(path, method, **params)
            if method == "apply":
                busy_samples = 2
            elif method == "snapshot" and busy_samples:
                busy_samples -= 1
                result["isBusy"] = True
            return result
        with patch.object(self, "rpc", side_effect=service), patch.object(live_qa.time, "sleep", side_effect=waits.append):
            self.invoke()
        self.assertEqual(8, len(waits))  # three forward applies and one changed SSH baseline
        self.assertEqual(self.originals, self.current)

    def test_gtk_driver_missing_capture_is_failure(self):
        driver = self.root / "driver.py"
        driver.write_text("# mocked")
        self.args.gtk_driver, self.args.ui_path = str(driver), str(self.root)
        response = {"ok": True, "operation": {"id": "gtk", "kind": "switch", "profileId": 2},
            "usageDisplay": {"updatedAt": datetime.now(timezone.utc).isoformat(), "windows": {}}}
        with patch.object(live_qa.subprocess, "run", return_value=subprocess.CompletedProcess([], 0, json.dumps(response).encode())), \
                self.assertRaisesRegex(live_qa.Failure, "gtk_apply_failed"):
            live_qa.gtk_apply(self.args, 2, set(self.originals))

    def test_desktop_scope_uses_only_packaged_app_server_descendant(self):
        proc = self.root / "proc-scoped"
        proc.mkdir()
        def add(pid, parent, executable, arguments, environment):
            entry = proc / str(pid)
            entry.mkdir()
            (entry / "exe").symlink_to(executable)
            (entry / "cmdline").write_bytes(arguments.encode())
            (entry / "environ").write_bytes(environment.encode())
            fields = ["S", str(parent)] + ["0"] * 17 + [str(pid*100), "0"]
            (entry / "stat").write_text(f"{pid} (fixture) " + " ".join(fields))
            return entry
        root_exe = "/usr/lib/chatgpt/ChatGPT"
        add(100, 1, root_exe, root_exe + "\0", "overwritten environment fragment\0")
        add(101, 100, root_exe, root_exe + " --type=renderer --other\0", "unreadable-like-fragment\0")
        child = add(102, 100, "/usr/lib/chatgpt/resources/codex", "codex\0app-server\0", "CODEX_HOME=" + self.args.codex_home + "\0")
        self.assertEqual({root_exe: 1}, live_qa.desktop_state(self.args.codex_home, str(proc)))
        # A separate packaged CLI cannot prove the root's home without ancestry.
        (child / "stat").write_text("102 (fixture) S 1 " + "0 "*17 + "10200 0")
        with self.assertRaisesRegex(live_qa.Failure, "desktop_scope_unproven"):
            live_qa.desktop_state(self.args.codex_home, str(proc))
        (child / "stat").write_text("102 (fixture) S 100 " + "0 "*17 + "10200 0")
        add(103, 100, "/usr/lib/chatgpt/resources/codex", "codex\0app-server\0", "CODEX_HOME=/another-home\0")
        with self.assertRaisesRegex(live_qa.Failure, "desktop_scope_ambiguous"):
            live_qa.desktop_state(self.args.codex_home, str(proc))
        (proc / "103" / "environ").write_bytes(b"overwritten fragments\0")
        with self.assertRaisesRegex(live_qa.Failure, "desktop_scope_unproven"):
            live_qa.desktop_state(self.args.codex_home, str(proc))
        # A network-listening CLI is not the desktop-owned app-server proof.
        (proc / "103" / "cmdline").write_bytes(b"codex\0app-server\0--listen\0tcp://localhost:1\0")
        self.assertEqual({root_exe: 1}, live_qa.desktop_state(self.args.codex_home, str(proc)))

    def test_official_protocol_probe_reports_only_hashes(self):
        home = self.root / "codex"
        home.mkdir(mode=0o700)
        auth_path = home / "auth.json"
        auth_path.write_text(json.dumps({"auth_mode": "chatgpt", "tokens": {
            "account_id": "fixture-workspace-id", "refresh_token": "fixture-secret-refresh"}}))
        auth_path.chmod(0o600)
        bin_path = self.root / "bin"
        bin_path.mkdir()
        cli = bin_path / "codex"
        cli.write_text('''#!/usr/bin/python3
import json, sys
for line in sys.stdin:
    value=json.loads(line)
    if value.get("method")=="initialize":
        print(json.dumps({"id":value["id"],"result":{}}),flush=True)
    if value.get("method")=="account/read":
        assert value["params"]["refreshToken"] is False
        print(json.dumps({"id":value["id"],"result":{"account":{"type":"chatgpt","email":"Fixture@example.test","planType":"plus"}}}),flush=True)
''')
        cli.chmod(0o700)
        result = subprocess.run(["/usr/bin/python3", "-I", "-c", live_qa.PROBE],
            env={**os.environ, "CODEX_HOME": str(home), "PATH": str(bin_path) + ":/usr/bin:/bin"},
            capture_output=True, timeout=10, text=True)
        self.assertEqual(0, result.returncode)
        parsed = json.loads(result.stdout)
        self.assertTrue(parsed["ok"])
        self.assertEqual(hashlib.sha256(b"fixture-workspace-id").hexdigest(), parsed["result"]["accountIdSha256"])
        self.assertEqual(hashlib.sha256(b"fixture@example.test").hexdigest(), parsed["result"]["emailSha256"])
        self.assertFalse(parsed["result"]["sdkWorkspaceIdExposed"])
        for raw_value in ("fixture-workspace-id", "fixture-secret-refresh", "Fixture@example.test"):
            self.assertNotIn(raw_value, result.stdout + result.stderr)
        cli.write_text("#!/usr/bin/python3\nfrom pathlib import Path\nPath(" + repr(str(self.root / "cli-started")) + ").touch()\n")
        rejected = subprocess.run(["/usr/bin/python3", "-I", "-c", live_qa.PROBE],
            env={**os.environ, "CODEX_HOME": str(home), "CODEX_SYNCBAR_QA_ACCESS_ONLY": "1", "PATH": str(bin_path) + ":/usr/bin:/bin"},
            capture_output=True, timeout=10, text=True)
        self.assertEqual(1, rejected.returncode)
        self.assertEqual("remote_baseline_contains_refresh_token", json.loads(rejected.stdout)["error"])
        self.assertFalse((self.root / "cli-started").exists())


if __name__ == "__main__":
    unittest.main()
