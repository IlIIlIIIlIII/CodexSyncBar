#!/usr/bin/python3
"""Verify the real Shell widgets on a private bus/display, never the user's desktop."""
import argparse
import os
from pathlib import Path
import subprocess
import tempfile
import zipfile

ROOT = Path(__file__).resolve().parents[3]
parser = argparse.ArgumentParser()
parser.add_argument('--output', type=Path, default=ROOT / 'docs/ubuntu/qa-evidence/tray/native')
parser.add_argument('--size', default='1280x900')
parser.add_argument('--text-scale', default='1')
parser.add_argument('--live-readonly', action='store_true')
args = parser.parse_args()
args.output.mkdir(parents=True, exist_ok=True)
with tempfile.TemporaryDirectory(prefix='syncbar-tray-qa-') as temp:
    root = Path(temp)
    runtime = root / 'runtime'
    runtime.mkdir(mode=0o700)
    archive = root / 'codex-syncbar@griplabs.io.shell-extension.zip'
    with zipfile.ZipFile(archive, 'w') as zipped:
        for path in (ROOT / 'Ubuntu/gnome').iterdir():
            if path.is_file():
                zipped.write(path, path.name)
    env = dict(os.environ, XDG_RUNTIME_DIR=str(runtime), LIBGL_ALWAYS_SOFTWARE='1',
               SYNCBAR_QA_OUTPUT=str(args.output.resolve()), SYNCBAR_QA_SIZE=args.size,
               SYNCBAR_QA_TEXT_SCALE=args.text_scale,
               SYNCBAR_QA_SOCKET=str(Path(os.environ.get('XDG_RUNTIME_DIR', f'/run/user/{os.getuid()}')) / 'codex-syncbar/control.sock') if args.live_readonly else str(root / 'demo.sock'),
               SYNCBAR_QA_LIVE=str(int(args.live_readonly)))
    env.pop('DBUS_SESSION_BUS_ADDRESS', None)
    backend = None
    try:
        if not args.live_readonly:
            backend_log = (args.output / 'backend.log').open('w')
            backend = subprocess.Popen([str(ROOT / 'dist/ubuntu/backend/CodexSyncBar.Backend'), '--demo', '--socket', str(root / 'demo.sock')],
                                       env=env, stdout=backend_log, stderr=subprocess.STDOUT)
        command = ['dbus-run-session', '--', 'gnome-shell-test-tool', '--headless', '--disable-animations',
                   '--wrap', f'python3 {Path(__file__).with_name("compositor.py")}',
                   '--extension', str(archive), str(Path(__file__).with_name('scenes.js'))]
        with (args.output / 'shell.log').open('w') as log:
            result = subprocess.run(command, env=env, stdout=log, stderr=subprocess.STDOUT, timeout=100)
        transcript = (args.output / 'shell.log').read_text()
        print('\n'.join(line for line in transcript.splitlines() if 'SYNCBAR_' in line or 'JS ERROR' in line or 'Error:' in line))
        print('Native GNOME log:', args.output / 'shell.log')
        raise SystemExit(result.returncode)
    finally:
        if backend:
            backend.terminate()
            backend.wait(timeout=10)
            backend_log.close()
