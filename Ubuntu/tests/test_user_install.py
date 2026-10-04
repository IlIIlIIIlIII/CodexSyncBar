#!/usr/bin/env python3
"""Use disposable roots to verify installation; never changes this user's desktop."""
import importlib.util
from pathlib import Path
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location('install_user', ROOT / 'Ubuntu/packaging/install_user.py')
installer = importlib.util.module_from_spec(spec)
spec.loader.exec_module(installer)


class UserInstallTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix='syncbar-install-test-')
        self.root = Path(self.temporary.name)
        self.source = self.root / 'source'
        self.user = self.root / 'user'
        (self.source / 'backend').mkdir(parents=True)
        (self.source / 'backend/CodexSyncBar.Backend').write_text('fixture backend v1')
        (self.source / 'ui/codex_syncbar').mkdir(parents=True)
        (self.source / 'ui/codex_syncbar/__main__.py').write_text('# fixture UI\n')
        self.user.mkdir()

    def tearDown(self):
        self.temporary.cleanup()

    def install(self):
        return installer.install(self.source, self.user, reload=False)

    def test_fresh_install_is_private_and_startup_stays_off(self):
        sentinel = self.user / '.codex/auth.json'
        sentinel.parent.mkdir()
        sentinel.write_text('unchanged synthetic auth')
        result = self.install()
        self.assertFalse(result['started'])
        self.assertFalse(result['autostartEnabled'])
        self.assertNotIn('[Install]', Path(result['unit']).read_text())
        self.assertEqual(0o700, Path(result['application']).parent.stat().st_mode & 0o777)
        self.assertFalse((self.user / '.config/autostart').exists())
        self.assertEqual('unchanged synthetic auth', sentinel.read_text())

    def test_existing_unmanaged_launcher_is_preserved(self):
        launcher = self.user / '.local/bin/codex-syncbar'
        launcher.parent.mkdir(parents=True)
        launcher.write_text('my own utility')
        with self.assertRaisesRegex(RuntimeError, 'unmanaged'):
            self.install()
        self.assertEqual('my own utility', launcher.read_text())
        self.assertFalse((self.user / '.local/share/codex-syncbar/app').exists())

    def test_existing_unmanaged_app_is_preserved(self):
        app = self.user / '.local/share/codex-syncbar/app'
        app.mkdir(parents=True)
        (app / 'mine').write_text('preserve')
        with self.assertRaisesRegex(RuntimeError, 'unmanaged'):
            self.install()
        self.assertEqual('preserve', (app / 'mine').read_text())

    def test_symlink_destination_is_rejected(self):
        elsewhere = self.root / 'elsewhere'
        elsewhere.mkdir()
        (self.user / '.local').symlink_to(elsewhere, target_is_directory=True)
        with self.assertRaisesRegex(RuntimeError, 'symbolic'):
            self.install()
        self.assertEqual([], list(elsewhere.iterdir()))

    def test_managed_upgrade_keeps_old_application_for_recovery(self):
        self.install()
        (self.source / 'backend/CodexSyncBar.Backend').write_text('fixture backend v2')
        result = self.install()
        self.assertEqual('fixture backend v2', (Path(result['application']) / 'backend/CodexSyncBar.Backend').read_text())
        self.assertEqual('fixture backend v1', (Path(result['previousApplication']) / 'backend/CodexSyncBar.Backend').read_text())


if __name__ == '__main__':
    unittest.main(verbosity=2)
