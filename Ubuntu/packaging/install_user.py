#!/usr/bin/env python3
"""Install the built application for one user without sudo or starting services."""
import argparse
import json
import os
from pathlib import Path
import shlex
import shutil
import subprocess
import tempfile
import uuid

ROOT = Path(__file__).resolve().parents[2]
MARKER = 'Codex SyncBar managed'


def systemd_quote(value):
    return '"' + str(value).replace('%', '%%').replace('\\', '\\\\').replace('"', '\\"') + '"'


def desktop_quote(value):
    return '"' + str(value).replace('%', '%%').replace('\\', '\\\\').replace('"', '\\"').replace('`', '\\`').replace('$', '\\$') + '"'


def private_parents(path):
    for parent in [path, *path.parents]:
        if parent.is_symlink():
            raise RuntimeError(f'Refusing a symbolic link in the install destination: {parent}')
    missing = []
    current = path
    while not current.exists():
        missing.append(current)
        current = current.parent
    for directory in reversed(missing):
        directory.mkdir(mode=0o700)


def write_managed(path, text, mode):
    private_parents(path.parent)
    temporary = path.with_name('.' + path.name + '.' + uuid.uuid4().hex)
    try:
        with temporary.open('x') as stream:
            os.chmod(temporary, mode)
            stream.write(text)
        temporary.replace(path)
    finally:
        temporary.unlink(missing_ok=True)


def install(source, user_root, node=None, reload=True):
    source = source.resolve(strict=True)
    user_root = user_root.absolute()
    executable = source / 'backend/CodexSyncBar.Backend'
    if not executable.is_file() or not (source / 'ui/codex_syncbar/__main__.py').is_file():
        raise RuntimeError('Build the Ubuntu application before installing it.')
    app = user_root / '.local/share/codex-syncbar/app'
    launcher = user_root / '.local/bin/codex-syncbar'
    desktop = user_root / '.local/share/applications/io.github.codexsyncbar.Ubuntu.desktop'
    unit = user_root / '.config/systemd/user/codex-syncbar.service'
    icon = user_root / '.local/share/icons/hicolor/256x256/apps/codex-syncbar.png'
    for destination in (app, launcher, desktop, unit, icon):
        for ancestor in (destination, *destination.parents):
            if ancestor.is_symlink():
                raise RuntimeError(f'Refusing an existing symbolic link: {ancestor}')
    for destination in (launcher, desktop, unit):
        if destination.exists() and MARKER not in destination.read_text():
            raise RuntimeError(f'Refusing to overwrite an unmanaged file: {destination}')
    if app.exists():
        receipt = app / '.syncbar-install.json'
        if not receipt.is_file() or json.loads(receipt.read_text()).get('managedBy') != MARKER:
            raise RuntimeError(f'Refusing to overwrite an unmanaged application: {app}')
    if icon.exists() and not app.exists():
        raise RuntimeError(f'Refusing to overwrite an unmanaged icon: {icon}')
    private_parents(app.parent)
    stage = Path(tempfile.mkdtemp(prefix='.app-install-', dir=app.parent))
    previous = None
    try:
        shutil.copytree(source / 'backend', stage / 'backend', ignore=shutil.ignore_patterns('__pycache__'))
        shutil.copytree(source / 'ui', stage / 'ui', ignore=shutil.ignore_patterns('__pycache__'))
        shutil.copy2(ROOT / 'Ubuntu/packaging/codex-syncbar', stage / 'launch')
        shutil.copy2(ROOT / 'Ubuntu/packaging/prepare_session.py', stage / 'prepare-session.py')
        (stage / 'launch').chmod(0o755)
        if node:
            node = node.resolve(strict=True)
            result = subprocess.run([str(node), '--version'], capture_output=True, text=True, check=True)
            if not result.stdout.startswith('v'):
                raise RuntimeError('The selected Node.js executable did not report its version.')
            (stage / 'bin').mkdir()
            shutil.copy2(node, stage / 'bin/node')
            (stage / 'bin/node').chmod(0o755)
        (stage / '.syncbar-install.json').write_text(json.dumps({'managedBy': MARKER, 'schemaVersion': 1}))
        for directory, _, _ in os.walk(stage):
            os.chmod(directory, 0o700)
        if app.exists():
            previous = app.with_name('.app-previous-' + uuid.uuid4().hex)
            app.rename(previous)
        stage.rename(app)
        runtime_path = str(app / 'bin') + ':/usr/local/bin:/usr/bin:/bin'
        launcher_text = ('#!/bin/sh\n# ' + MARKER + ' launcher\n'
                         + 'export SYNCBAR_APP_DIR=' + shlex.quote(str(app)) + '\n'
                         + 'export PATH=' + shlex.quote(runtime_path) + ':"$PATH"\n'
                         + 'exec /bin/bash ' + shlex.quote(str(app / 'launch')) + ' "$@"\n')
        write_managed(launcher, launcher_text, 0o755)
        desktop_text = (ROOT / 'Ubuntu/packaging/codex-syncbar.desktop').read_text()
        desktop_text = desktop_text.replace('Exec=codex-syncbar', 'Exec=' + desktop_quote(launcher))
        write_managed(desktop, '# ' + MARKER + ' desktop entry\n' + desktop_text, 0o644)
        unit_text = ('# ' + MARKER + ' user unit\n[Unit]\nDescription=Codex SyncBar account controller\n'
                     'After=graphical-session.target\n\n[Service]\nType=simple\n'
                     'ExecStart=' + systemd_quote(app / 'backend/CodexSyncBar.Backend') + '\n'
                     'EnvironmentFile=-%t/codex-syncbar/session.env\n'
                     'Environment=' + systemd_quote('PATH=' + runtime_path) + '\n'
                     'Restart=on-failure\nRestartSec=3\nTimeoutStopSec=5min\nUMask=0077\n')
        write_managed(unit, unit_text, 0o644)
        private_parents(icon.parent)
        shutil.copy2(ROOT / 'Resources/AppIcon.png', icon)
        if reload and user_root == Path.home():
            subprocess.run(['systemctl', '--user', 'daemon-reload'], check=True)
    except Exception:
        if previous is not None and previous.exists() and not app.exists():
            previous.rename(app)
        raise
    finally:
        if stage.exists():
            shutil.rmtree(stage)
    # Keep an old managed installation recoverable after an upgrade.
    return {'application': str(app), 'launcher': str(launcher), 'desktop': str(desktop),
            'unit': str(unit), 'previousApplication': str(previous) if previous else None,
            'started': False, 'autostartEnabled': False}


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('--source', required=True, type=Path)
    parser.add_argument('--user-root', type=Path, default=Path.home(), help='Temporary user root for installer tests')
    parser.add_argument('--node', type=Path, help='Copy this existing Node.js executable for a rootless installation')
    parser.add_argument('--no-reload', action='store_true')
    arguments = parser.parse_args()
    print(json.dumps(install(arguments.source, arguments.user_root, arguments.node, not arguments.no_reload),
                     ensure_ascii=False, indent=2))
