#!/usr/bin/env python3
"""Inspect a built package without installing it or running its application."""
import pathlib
import subprocess
import sys
import tempfile


def verify(package):
    with tempfile.TemporaryDirectory(prefix='syncbar-package-check-') as temporary:
        root = pathlib.Path(temporary)
        subprocess.run(['dpkg-deb', '--extract', package, str(root)], check=True)
        metadata = subprocess.check_output(['dpkg-deb', '--field', package], text=True)
        assert 'Architecture: amd64' in metadata
        for dependency in ['python3-gi', 'gir1.2-gtk-4.0', 'gir1.2-adw-1', 'libsecret-tools', 'nodejs']:
            assert dependency in metadata, dependency
        backend = root / 'usr/lib/codex-syncbar/backend'
        for name in ['CodexSyncBar.Backend', 'libcoreclr.so', 'libhostfxr.so', 'libhostpolicy.so']:
            assert (backend / name).is_file(), name
        for name in ['gpt-switch', 'usage-summary.mjs', 'codex-syncbar-askpass', 'codex-syncbar-linux-askpass',
                     'codex-syncbar-secret-tool']:
            assert (backend / 'Runtime' / name).is_file(), name
            assert (backend / 'Runtime' / name).stat().st_mode & 0o111, name
        assert (root / 'usr/lib/codex-syncbar/ui/codex_syncbar/__main__.py').is_file()
        assert (root / 'usr/lib/codex-syncbar/prepare-session.py').is_file()
        assert (root / 'usr/bin/codex-syncbar').stat().st_mode & 0o111
        unit = (root / 'usr/lib/systemd/user/codex-syncbar.service').read_text()
        assert '[Install]\n' not in unit
        assert 'EnvironmentFile=-%t/codex-syncbar/session.env' in unit
        assert not list(root.glob('**/autostart/*'))
        control = subprocess.check_output(['dpkg-deb', '--ctrl-tarfile', package])
        # There are no maintainer scripts that could enable/start a service.
        import io
        import tarfile
        with tarfile.open(fileobj=io.BytesIO(control)) as archive:
            assert not any(pathlib.PurePosixPath(item.name).name in
                           {'preinst', 'postinst', 'prerm', 'postrm'} for item in archive)
        assert not list(root.glob('**/auth.json'))
    print('Package structure, bundled runtime and default-off startup verified.')


if __name__ == '__main__':
    verify(sys.argv[1])
