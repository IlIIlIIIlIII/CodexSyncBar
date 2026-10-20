"""SSH alias discovery and OpenSSH resolution without remote connections."""
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'ui'))
from claude_syncbar.ssh_config import list_hosts, device_from_host


class SshConfigTests(unittest.TestCase):
    def test_includes_aliases_patterns_duplicates_and_cycles(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            (root / 'conf.d').mkdir()
            config = root / 'config'
            config.write_text('Host ml ML other # comment\nHost * !skip prod?\nInclude "conf.d/*"\nHost=last\nHost = final\n')
            (root / 'conf.d/hosts').write_text('Host "laptop"\nInclude config\n')
            self.assertEqual(['ml', 'other', 'laptop', 'last', 'final'], list_hosts(config))

    def test_missing_config(self):
        self.assertEqual([], list_hosts('/tmp/syncbar-no-such-config/config'))

    def test_invalid_config_reports_location(self):
        with tempfile.TemporaryDirectory() as directory:
            config = Path(directory) / 'config'
            config.write_text('Host "unterminated\n')
            with self.assertRaisesRegex(ValueError, 'config:1'):
                list_hosts(config)

    def test_openssh_resolves_defaults_preserving_alias_and_disabled_state(self):
        with tempfile.TemporaryDirectory() as directory:
            config = Path(directory) / 'config'
            config.write_text('Host example\n HostName 192.0.2.1\n User tester\n Port 2222\n IdentityFile ~/.ssh/example\nHost *\n User fallback\n Port 22\n')
            draft = device_from_host('example', config)
            self.assertEqual('example', draft['host'])
            self.assertEqual('tester', draft['username'])
            self.assertEqual(2222, draft['port'])
            self.assertEqual('openSSHConfig', draft['authentication'])
            self.assertFalse(draft['enabled'])
            self.assertNotIn('id', draft)

    def test_options_cannot_be_host(self):
        with patch('claude_syncbar.ssh_config.subprocess.run') as run:
            with self.assertRaises(ValueError):
                device_from_host('-oProxyCommand=bad')
            run.assert_not_called()

    def test_failed_resolution_has_actionable_error(self):
        with patch('claude_syncbar.ssh_config.subprocess.run', side_effect=OSError):
            with self.assertRaisesRegex(ValueError, 'OpenSSH'):
                device_from_host('example')


if __name__ == '__main__':
    unittest.main()
