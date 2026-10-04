#!/usr/bin/env bash
set -euo pipefail
source "$(dirname "$0")/common.sh"
version=${SYNCBAR_VERSION:-1.0.0.28+ubuntu1}
if [[ ! "$version" =~ ^[0-9][A-Za-z0-9.+:~-]*$ ]]; then
    printf 'Invalid Debian package version.\n' >&2
    exit 1
fi
if [[ ${1:-} != --no-build ]]; then bash "$(dirname "$0")/build.sh"; fi
test -x "$SYNCBAR_OUTPUT/backend/CodexSyncBar.Backend"
stage=$(mktemp -d "${TMPDIR:-/tmp}/codex-syncbar-package.XXXXXXXX")
trap 'rm -rf "$stage"' EXIT
mkdir -p "$stage/DEBIAN" "$stage/usr/lib/codex-syncbar" "$stage/usr/bin" \
    "$stage/usr/share/applications" "$stage/usr/lib/systemd/user" \
    "$stage/usr/share/icons/hicolor/256x256/apps" "$stage/usr/share/doc/codex-syncbar"
cp -a "$SYNCBAR_OUTPUT/backend" "$SYNCBAR_OUTPUT/ui" "$stage/usr/lib/codex-syncbar/"
install -m 755 "$SYNCBAR_OUTPUT/prepare-session.py" "$stage/usr/lib/codex-syncbar/prepare-session.py"
find "$stage" -type d -name __pycache__ -prune -exec rm -rf {} +
install -m 755 "$SYNCBAR_REPO/Ubuntu/packaging/codex-syncbar" "$stage/usr/bin/codex-syncbar"
install -m 644 "$SYNCBAR_REPO/Ubuntu/packaging/codex-syncbar.desktop" "$stage/usr/share/applications/io.github.codexsyncbar.Ubuntu.desktop"
install -m 644 "$SYNCBAR_REPO/Ubuntu/packaging/codex-syncbar.service" "$stage/usr/lib/systemd/user/"
install -m 644 "$SYNCBAR_REPO/Resources/AppIcon.png" "$stage/usr/share/icons/hicolor/256x256/apps/codex-syncbar.png"
cat >"$stage/DEBIAN/control" <<EOF
Package: codex-syncbar
Version: $version
Architecture: amd64
Maintainer: Codex SyncBar contributors
Section: utils
Priority: optional
Depends: python3, python3-gi, gir1.2-gtk-4.0 (>= 4.14), gir1.2-adw-1 (>= 1.5), libsecret-tools | gir1.2-secret-1, nodejs, jq, bash, tar, openssh-client, xdg-utils, libc6, libgcc-s1, libstdc++6, libssl3t64, libicu78, zlib1g
Description: Native Ubuntu Codex account switcher
 Review current and planned accounts, usage and SSH devices before applying
 a coordinated switch. Includes its .NET runtime. Login autostart is off.
EOF
cat >"$stage/usr/share/doc/codex-syncbar/README" <<'EOF'
Built for Ubuntu 26.04 amd64 with GTK 4 and libadwaita.
Launch Codex SyncBar from the applications menu or run codex-syncbar.
The backend user service starts only when the application is explicitly opened.
No login autostart is installed or enabled by this package.
codex-syncbar --demo runs a separate synthetic demo without the real service.
Removal leaves account, browser and configuration data intact.
EOF
find "$stage" -type d -exec chmod 755 {} +
package="$SYNCBAR_OUTPUT/codex-syncbar_${version}_amd64.deb"
dpkg-deb --root-owner-group --build "$stage" "$package"
(cd "$SYNCBAR_OUTPUT" && sha256sum "$(basename "$package")") >"$package.sha256"
python3 "$SYNCBAR_REPO/Ubuntu/tests/test_package.py" "$package"
printf 'Package verified: %s\n' "$package"
