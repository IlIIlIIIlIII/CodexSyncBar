#!/usr/bin/env python3
"""URI adaptation regressions; never invokes desktop automation or credentials."""

import importlib.util
import json
from pathlib import Path
import unittest
import tempfile
from unittest.mock import patch

source = Path(__file__).resolve().parents[1] / "Scripts/Windows/node-repl-wsl-bridge.py"
spec = importlib.util.spec_from_file_location("bridge", source)
bridge = importlib.util.module_from_spec(spec)
spec.loader.exec_module(bridge)


class BridgeTests(unittest.TestCase):
    def setUp(self):
        bridge.to_windows_uri.cache_clear()

    def test_unc_preserves_space_unicode_and_literal_percent(self):
        self.assertEqual(
            bridge.windows_path_uri("\\\\wsl.localhost\\Ubuntu-26.04\\home\\한 글\\100%"),
            "file://wsl.localhost/Ubuntu-26.04/home/%ED%95%9C%20%EA%B8%80/100%25",
        )

    def test_drive_path(self):
        self.assertEqual(bridge.windows_path_uri("C:\\Users\\A B"), "file:///C:/Users/A%20B")

    def test_invalid_windows_paths_rejected(self):
        for path in ("relative", "/home/test", "C:relative", "\\\\server"):
            with self.subTest(path=path), self.assertRaises(ValueError):
                bridge.windows_path_uri(path)

    def test_existing_windows_and_remote_uris_preserved(self):
        for uri in ("file:///C:/A%20B", "file://wsl.localhost/Ubuntu/home/a", "file://server/share/a", "https://example.com/a"):
            with self.subTest(uri=uri), patch.object(bridge.subprocess, "run") as run:
                self.assertEqual(bridge.to_windows_uri(uri), uri)
                run.assert_not_called()

    def test_wslpath_receives_decoded_path_as_one_argument(self):
        with patch.object(bridge.subprocess, "run") as run:
            run.return_value.stdout = "\\\\wsl.localhost\\Ubuntu-26.04\\home\\a b;$()\n"
            mapped = bridge.to_windows_uri("file:///home/a%20b%3B%24%28%29")
            self.assertEqual(run.call_args.args[0], ["/usr/bin/wslpath", "-w", "/home/a b;$()"])
            self.assertEqual(mapped, "file://wsl.localhost/Ubuntu-26.04/home/a%20b%3B%24%28%29")

    def test_malformed_path_fails_closed(self):
        for uri in ("file:///home/a?query", "file:///home/a#fragment", "file:///home/%00", "file:relative"):
            with self.subTest(uri=uri), self.assertRaises(ValueError):
                bridge.to_windows_uri(uri)

    def test_permissions_and_tool_arguments_preserved(self):
        state = {
            "sandboxCwd": "file:///home/project",
            "permissionProfile": {
                "type": "custom", "network": {"enabled": False},
                "fileSystem": {"rules": [
                    {"path": "file:///home/project", "access": "read"},
                    {"path": "file:///home/project/secrets", "access": "none"},
                ]},
            },
            "useLegacyLandlock": True,
        }
        payload = {"jsonrpc": "2.0", "id": 42, "method": "tools/call", "params": {
            "name": "js", "arguments": {"code": 'nodeRepl.write("file:///unchanged")'},
            "_meta": {bridge.META_KEY: state, "unrelated": {"path": "file:///unchanged"}},
        }}
        changed = json.loads(bridge.translate_request(json.dumps(payload).encode(), lambda uri: uri.replace("file:///", "file://wsl.localhost/Ubuntu/")))
        actual = changed["params"]["_meta"].pop(bridge.META_KEY)
        expected_other = payload["params"]["_meta"].pop(bridge.META_KEY)
        self.assertEqual(changed, payload)
        rules = actual["permissionProfile"]["fileSystem"]["rules"]
        self.assertEqual([r["access"] for r in rules], ["read", "none"])
        self.assertFalse(actual["permissionProfile"]["network"]["enabled"])
        self.assertTrue(actual["useLegacyLandlock"])
        self.assertEqual(actual["sandboxCwd"], "file://wsl.localhost/Ubuntu/home/project")
        self.assertEqual(expected_other, state)

    def test_non_tool_messages_byte_identical(self):
        for raw in (b'{"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}\n', b'{"id":1,"result":{"uri":"file:///home/a"}}\n'):
            self.assertIs(bridge.translate_request(raw), raw)

    def test_missing_metadata_is_not_synthesized(self):
        raw = b'{"id":2,"method":"tools/call","params":{"name":"js","arguments":{}}}\n'
        self.assertIs(bridge.translate_request(raw), raw)

    def test_identical_metadata_byte_identical(self):
        raw = json.dumps({"method": "tools/call", "params": {"_meta": {bridge.META_KEY: {"sandboxCwd": "file:///C:/work", "permissionProfile": {"type": "disabled"}}}}}).encode()
        self.assertIs(bridge.translate_request(raw), raw)

    def test_app_provided_runtime_is_read_fresh(self):
        with tempfile.TemporaryDirectory() as folder:
            for version in ("old-runtime", "new-runtime"):
                runtime = Path(folder) / version
                runtime.mkdir()
                (runtime / "node_repl.exe").touch()
                with patch.dict(bridge.os.environ, {"NODE_REPL_NODE_PATH": str(runtime / "node.exe")}):
                    self.assertEqual(bridge.runtime_server_path(), str(runtime / "node_repl.exe"))

    def test_missing_or_incorrect_runtime_fails_closed(self):
        for node in ("", "relative/node.exe", "/nonexistent/node.exe", "/path/bridge.py"):
            with self.subTest(node=node), patch.dict(bridge.os.environ, {"NODE_REPL_NODE_PATH": node}), self.assertRaises(ValueError):
                bridge.runtime_server_path()


if __name__ == "__main__":
    unittest.main(verbosity=2)
