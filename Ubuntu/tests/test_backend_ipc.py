#!/usr/bin/env python3
"""Exercise the real socket host with a synthetic, credential-free demo service.

This is automated integration QA, not proof of live OAuth, SSH or desktop E2E.
Production coordinator/platform contracts run separately in the .NET test suite.
"""
import argparse
import json
import os
from pathlib import Path
import socket
import stat
import subprocess
import tempfile
import time
import unittest
import uuid


BACKEND = None


class BackendIpcTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.temporary = tempfile.TemporaryDirectory(prefix='syncbar-ipc-')
        cls.root = Path(cls.temporary.name)
        cls.socket_path = cls.root / 'run/control.sock'
        for name in ('run', 'home', 'state', 'codex'):
            (cls.root / name).mkdir(mode=0o700)
        cls.marker = cls.root / 'codex/auth.json'
        cls.marker.write_text('{"synthetic_sentinel":"unchanged"}')
        cls.marker.chmod(0o600)
        cls.environment = dict(os.environ, HOME=str(cls.root / 'home'),
                               XDG_DATA_HOME=str(cls.root / 'home/data'),
                               XDG_CONFIG_HOME=str(cls.root / 'home/config'),
                               XDG_RUNTIME_DIR=str(cls.root / 'run'),
                               CODEX_HOME=str(cls.root / 'codex'),
                               CODEX_SYNCBAR_STATE_ROOT=str(cls.root / 'state'))
        cls.log = (cls.root / 'backend.log').open('w+')
        cls.process = subprocess.Popen([BACKEND, '--demo', '--socket', str(cls.socket_path)],
                                       env=cls.environment, stdout=cls.log, stderr=cls.log)
        deadline = time.monotonic() + 15
        while not cls.socket_path.is_socket():
            if cls.process.poll() is not None or time.monotonic() >= deadline:
                cls.log.seek(0)
                raise RuntimeError('Demo backend failed to start: ' + cls.log.read())
            time.sleep(0.025)

    @classmethod
    def tearDownClass(cls):
        cls.process.terminate()
        try:
            cls.process.wait(timeout=10)
        except subprocess.TimeoutExpired:
            cls.process.kill()
            cls.process.wait(timeout=5)
        cls.log.close()
        cls.temporary.cleanup()

    def call(self, method, parameters=None, *, ok=True):
        identifier = uuid.uuid4().hex
        request = {'id': identifier, 'method': method, 'params': parameters or {}}
        with socket.socket(socket.AF_UNIX, socket.SOCK_STREAM) as client:
            client.settimeout(15)
            client.connect(str(self.socket_path))
            client.sendall(json.dumps(request).encode() + b'\n')
            with client.makefile('rb') as reader:
                response = json.loads(reader.readline(4 * 1024 * 1024))
        self.assertEqual(identifier, response['id'])
        self.assertEqual(ok, response['ok'], response.get('error'))
        return response.get('result') if ok else response['error']

    def wait_operation(self, operation):
        deadline = time.monotonic() + 15
        while operation['state'] not in ('completed', 'failed', 'recoveryRequired', 'cancelled'):
            self.assertLess(time.monotonic(), deadline, 'operation did not complete')
            time.sleep(0.02)
            operation = self.call('operation', {'operationId': operation['id']})
        return operation

    def test_01_socket_is_private_and_snapshot_has_no_credentials(self):
        self.assertEqual(0o600, stat.S_IMODE(self.socket_path.stat().st_mode))
        self.assertEqual(os.getuid(), self.socket_path.stat().st_uid)
        snapshot = self.call('snapshot')
        self.assertTrue(snapshot['isDemo'])
        self.assertGreaterEqual(len(snapshot['accounts']), 2)
        forbidden = {'accessToken', 'refreshToken', 'idToken', 'access_token', 'refresh_token',
                     'id_token', 'privateKey', 'protectedAuth'}
        def inspect(value):
            if isinstance(value, dict):
                self.assertFalse(forbidden.intersection(value))
                for item in value.values():
                    inspect(item)
            elif isinstance(value, list):
                for item in value:
                    inspect(item)
        inspect(snapshot)

    def test_02_preview_is_read_only_and_switch_roundtrip_is_verified(self):
        initial = self.call('snapshot')
        original = initial['activeProfileId']
        target = next(account['id'] for account in initial['accounts']
                      if account['id'] != original and not account['needsLogin'])
        for selected in (target, original):
            before = self.call('snapshot')
            preview = self.call('preview', {'profileId': selected})
            self.assertTrue(preview['canApply'], preview.get('blockingReason'))
            self.assertEqual(selected, preview['profileId'])
            self.assertEqual(before['activeProfileId'], self.call('snapshot')['activeProfileId'])
            request_id = uuid.uuid4().hex
            parameters = {'previewId': preview['previewId'], 'requestId': request_id}
            operation = self.call('apply', parameters)
            duplicate = self.call('apply', parameters)
            self.assertEqual(operation['id'], duplicate['id'])
            completed = self.wait_operation(operation)
            self.assertEqual('completed', completed['state'], completed)
            after = self.call('snapshot')
            self.assertEqual(selected, after['activeProfileId'])
            for device in after['devices']:
                if device['enabled']:
                    self.assertEqual(selected, device['currentProfileId'])
            disabled_before = {d['id']: d['currentProfileId'] for d in before['devices'] if not d['enabled']}
            disabled_after = {d['id']: d['currentProfileId'] for d in after['devices'] if not d['enabled']}
            self.assertEqual(disabled_before, disabled_after)

    def test_03_invalid_method_and_oversized_requests_do_not_kill_service(self):
        error = self.call('not-a-real-method', ok=False)
        self.assertTrue(error['code'])
        with socket.socket(socket.AF_UNIX, socket.SOCK_STREAM) as client:
            client.settimeout(5)
            client.connect(str(self.socket_path))
            try:
                client.sendall(b'x' * 65538 + b'\n')
                self.assertEqual(b'', client.recv(1))
            except (ConnectionResetError, BrokenPipeError):
                pass
        self.assertTrue(self.call('snapshot')['isDemo'])

    def test_04_second_daemon_cannot_replace_live_socket(self):
        contender = subprocess.run([BACKEND, '--demo', '--socket', str(self.socket_path)],
                                   env=self.environment, capture_output=True, timeout=10)
        self.assertNotEqual(0, contender.returncode)
        self.assertTrue(self.call('snapshot')['isDemo'])

    def test_05_demo_requires_explicit_socket(self):
        result = subprocess.run([BACKEND, '--demo'], env=self.environment,
                                capture_output=True, timeout=10)
        self.assertNotEqual(0, result.returncode)

    def test_06_existing_regular_socket_path_is_not_deleted(self):
        existing = self.root / 'run/existing-file'
        existing.write_text('do not replace')
        existing.chmod(0o600)
        result = subprocess.run([BACKEND, '--demo', '--socket', str(existing)],
                                env=self.environment, capture_output=True, timeout=10)
        self.assertNotEqual(0, result.returncode)
        self.assertEqual('do not replace', existing.read_text())

    def test_07_stale_preview_cannot_apply_after_configuration_changes(self):
        initial = self.call('snapshot')
        selected = initial['accounts'][-1]
        preview = self.call('preview', {'profileId': selected['id']})
        self.call('account.rename', {'profileId': selected['id'], 'alias': 'QA'})
        error = self.call('apply', {'previewId': preview['previewId'], 'requestId': uuid.uuid4().hex}, ok=False)
        self.assertEqual('stale_preview', error['code'])
        self.assertEqual(initial['activeProfileId'], self.call('snapshot')['activeProfileId'])
        self.call('account.rename', {'profileId': selected['id'], 'alias': selected['alias']})

    def test_08_unreachable_devices_block_apply(self):
        initial = self.call('snapshot')
        self.call('demo.configure', {'offline': True})
        try:
            preview = self.call('preview', {'profileId': initial['accounts'][-1]['id']})
            self.assertFalse(preview['canApply'])
            error = self.call('apply', {'previewId': preview['previewId'], 'requestId': uuid.uuid4().hex}, ok=False)
            self.assertEqual('devices_unavailable', error['code'])
            self.assertEqual(initial['activeProfileId'], self.call('snapshot')['activeProfileId'])
        finally:
            self.call('demo.configure', {'offline': False})

    def test_09_failed_apply_reports_rollback_and_recovery_blocks_new_switch(self):
        initial = self.call('snapshot')
        target = next(a['id'] for a in initial['accounts'] if a['id'] != initial['activeProfileId'])
        self.call('demo.configure', {'failApply': True, 'failRecovery': False})
        try:
            preview = self.call('preview', {'profileId': target})
            result = self.wait_operation(self.call('apply', {'previewId': preview['previewId'], 'requestId': uuid.uuid4().hex}))
            self.assertEqual('failed', result['state'])
            self.assertTrue(all(t['state'] == 'restored' for t in result['targets']))
            self.assertEqual(initial['activeProfileId'], self.call('snapshot')['activeProfileId'])
            self.call('demo.configure', {'failRecovery': True})
            preview = self.call('preview', {'profileId': target})
            result = self.wait_operation(self.call('apply', {'previewId': preview['previewId'], 'requestId': uuid.uuid4().hex}))
            self.assertEqual('recoveryRequired', result['state'])
            next_preview = self.call('preview', {'profileId': target})
            error = self.call('apply', {'previewId': next_preview['previewId'], 'requestId': uuid.uuid4().hex}, ok=False)
            self.assertEqual('recovery_required', error['code'])
            self.wait_operation(self.call('recovery.retry'))
            self.assertEqual(initial['activeProfileId'], self.call('snapshot')['activeProfileId'])
        finally:
            self.call('demo.configure', {'failApply': False, 'failRecovery': False})

    def test_10_disabled_device_is_excluded_and_keeps_its_account(self):
        initial = self.call('snapshot')
        disabled = next(d['id'] for d in initial['devices'] if d['kind'] == 'ssh')
        target = next(a['id'] for a in initial['accounts'] if a['id'] != initial['activeProfileId'])
        self.call('demo.configure', {'disabledDeviceId': disabled})
        try:
            preview = self.call('preview', {'profileId': target})
            self.assertFalse(next(t for t in preview['targets'] if t['id'] == disabled)['included'])
            result = self.wait_operation(self.call('apply', {'previewId': preview['previewId'], 'requestId': uuid.uuid4().hex}))
            self.assertEqual('completed', result['state'])
            self.assertNotIn(disabled, [t['id'] for t in result['targets']])
            after = self.call('snapshot')
            self.assertEqual(next(d['currentProfileId'] for d in initial['devices'] if d['id'] == disabled),
                             next(d['currentProfileId'] for d in after['devices'] if d['id'] == disabled))
        finally:
            self.call('demo.configure', {'disabledDeviceId': '', 'localProfileId': initial['activeProfileId'],
                                         'remoteProfileId': initial['activeProfileId']})

    def test_99_demo_left_auth_and_state_untouched(self):
        self.assertEqual('{"synthetic_sentinel":"unchanged"}', self.marker.read_text())
        self.assertEqual([], list((self.root / 'state').iterdir()))
        self.assertEqual([], list((self.root / 'home').iterdir()))


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('--backend', required=True)
    arguments, remaining = parser.parse_known_args()
    BACKEND = str(Path(arguments.backend).resolve(strict=True))
    unittest.main(argv=[__file__, *remaining], verbosity=2)
