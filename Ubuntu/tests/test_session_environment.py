#!/usr/bin/env python3
"""Private launcher environment tests with a fake service manager only."""
import importlib.util
import os
from pathlib import Path
import subprocess
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location('prepare_session', ROOT / 'Ubuntu/packaging/prepare_session.py')
session = importlib.util.module_from_spec(spec)
spec.loader.exec_module(session)


class SessionEnvironmentTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix='syncbar-session-test-')
        self.root = Path(self.temporary.name)
        self.calls = []
        self.pid = 0
        self.environment = {'XDG_RUNTIME_DIR': str(self.root), 'HOME': str(self.root / 'home'),
                            'CODEX_HOME': str(self.root / 'custom codex'), 'DISPLAY': ':8',
                            'WAYLAND_DISPLAY': 'wayland-fixture', 'DBUS_SESSION_BUS_ADDRESS': 'unix:path=/fixture',
                            'OPENAI_API_KEY': 'synthetic-secret-must-never-pass'}

    def tearDown(self):
        self.temporary.cleanup()

    def runner(self, command, **_):
        self.calls.append(command)
        return subprocess.CompletedProcess(command, 0, str(self.pid), '')

    def start(self, running=None):
        return session.start(self.environment, self.runner, lambda _: running or self.environment)

    def test_passes_only_allowlisted_context_to_private_app_file(self):
        self.start()
        directory = self.root / 'codex-syncbar'
        target = directory / 'session.env'
        self.assertEqual(0o700, directory.stat().st_mode & 0o777)
        self.assertEqual(0o600, target.stat().st_mode & 0o777)
        text = target.read_text()
        self.assertIn('WAYLAND_DISPLAY="wayland-fixture"', text)
        self.assertIn('CODEX_HOME="' + self.environment['CODEX_HOME'] + '"', text)
        self.assertNotIn('OPENAI_API_KEY', text)
        self.assertNotIn('synthetic-secret', text)
        self.assertEqual(['systemctl', '--user', 'start', 'codex-syncbar.service'], self.calls[-1])
        self.assertFalse(any('set-environment' in command for command in self.calls))

    def test_running_backend_with_different_home_is_not_restarted(self):
        self.pid = 12345
        with self.assertRaisesRegex(RuntimeError, 'another CODEX_HOME'):
            self.start({'HOME': str(self.root), 'CODEX_HOME': str(self.root / 'different')})
        self.assertEqual(1, len(self.calls))
        self.assertFalse((self.root / 'codex-syncbar/session.env').exists())

    def test_running_backend_same_home_reuses_existing_context(self):
        self.start()
        target = self.root / 'codex-syncbar/session.env'
        before = target.read_bytes()
        self.pid = 12345
        self.environment['DISPLAY'] = ':9'
        self.start()
        self.assertEqual(before, target.read_bytes())

    def test_symbolic_link_and_insecure_directory_are_rejected(self):
        directory = self.root / 'codex-syncbar'
        directory.symlink_to(self.root / 'elsewhere')
        with self.assertRaises(RuntimeError):
            self.start()
        directory.unlink()
        directory.mkdir(mode=0o755)
        directory.chmod(0o755)
        with self.assertRaises(RuntimeError):
            self.start()
        self.assertEqual([], self.calls)

    def test_control_characters_fail_before_service_call(self):
        self.environment['DISPLAY'] = ':0\nOPENAI_API_KEY=invalid'
        with self.assertRaises(RuntimeError):
            self.start()
        self.assertEqual([], self.calls)

    def test_quote_preserves_literal_metacharacters(self):
        self.assertEqual('"a\\"b\\\\c\\$d\\`e"', session.quote('a"b\\c$d`e'))


if __name__ == '__main__':
    unittest.main(verbosity=2)
