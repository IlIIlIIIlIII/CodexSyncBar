#!/usr/bin/env python3
"""Secret Service adapter contract tests with an in-memory provider only."""
import base64
import contextlib
import io
from pathlib import Path
import runpy
import sys
import types
import unittest
from unittest.mock import patch


ENTRY = Path(__file__).resolve().parents[1] / "CodexSyncBar.Ubuntu.Platform/Runtime/codex-syncbar-secret-tool"
LOOKUP = ["lookup", "application", "codex-syncbar", "purpose", "vault-key-v1"]
STORE = ["store", "--label=Codex SyncBar vault", *LOOKUP[1:]]


class SecretToolTests(unittest.TestCase):
    def setUp(self):
        self.value = None
        self.fail = False
        self.called = 0
        provider = types.SimpleNamespace(password_lookup_sync=self.lookup, password_store_sync=self.store, COLLECTION_DEFAULT="default")
        gi = types.ModuleType("gi")
        gi.require_version = lambda name, version: None
        repository = types.ModuleType("gi.repository")
        repository.Secret = provider
        self.modules = {"gi": gi, "gi.repository": repository}
        self.main = runpy.run_path(str(ENTRY), run_name="isolated_test")["main"]

    def lookup(self, schema, attributes, cancellable):
        self.called += 1
        self.assertEqual({"application": "codex-syncbar", "purpose": "vault-key-v1"}, attributes)
        if self.fail:
            raise RuntimeError("sensitive diagnostic must be suppressed")
        return self.value

    def store(self, schema, attributes, collection, label, value, cancellable):
        self.called += 1
        self.value = value
        return True

    def invoke(self, arguments, input_value=""):
        output, error = io.StringIO(), io.StringIO()
        with patch.dict(sys.modules, self.modules), patch("sys.stdin", io.StringIO(input_value)), contextlib.redirect_stdout(output), contextlib.redirect_stderr(error):
            code = self.main(arguments)
        return code, output.getvalue(), error.getvalue()

    def test_missing_item_is_distinct_from_service_error(self):
        self.assertEqual((1, "", ""), self.invoke(LOOKUP))
        self.fail = True
        code, output, error = self.invoke(LOOKUP)
        self.assertEqual(2, code)
        self.assertEqual("", output)
        self.assertEqual("Secret Service is unavailable or locked.\n", error)

    def test_store_uses_stdin_and_lookup_is_interoperable(self):
        value = base64.b64encode(b"x" * 32).decode()
        self.assertEqual((0, "", ""), self.invoke(STORE, value))
        self.assertEqual((0, value, ""), self.invoke(LOOKUP))

    def test_invalid_arguments_do_not_access_secret_service(self):
        self.assertEqual((64, "", ""), self.invoke(["lookup", "purpose", "unrelated"]))
        self.assertEqual(0, self.called)

    def test_invalid_key_is_not_stored_or_echoed(self):
        code, output, error = self.invoke(STORE, "fake-sensitive-input")
        self.assertEqual(2, code)
        self.assertEqual(0, self.called)
        self.assertNotIn("fake-sensitive-input", output + error)


if __name__ == "__main__":
    unittest.main()
