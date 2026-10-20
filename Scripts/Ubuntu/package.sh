#!/usr/bin/env bash
set -euo pipefail
source "$(dirname "$0")/common.sh"
version=${SYNCBAR_VERSION:-1.0.0.28+ubuntu1}
if [[ ! "$version" =~ ^[0-9][A-Za-z0-9.+:~-]*$ ]]; then
    printf 'Invalid Debian package version.\n' >&2
    exit 1
fi
if [[ ${1:-} != --no-build ]]; then bash "$(dirname "$0")/build.sh"; fi
test -x "$SYNCBAR_OUTPUT/backend/ClaudeSyncBar.Backend"
stage=$(mktemp -d "${TMPDIR:-/tmp}/claude-syncbar-package.XXXXXXXX")
trap 'rm -rf "$stage"' EXIT
mkdir -p "$stage/DEBIAN" "$stage/usr/lib/claude-syncbar" "$stage/usr/bin" \
    "$stage/usr/share/applications" "$stage/usr/lib/systemd/user" \
    "$stage/usr/share/icons/hicolor/256x256/apps" "$stage/usr/share/doc/claude-syncbar"
cp -a "$SYNCBAR_OUTPUT/backend" "$SYNCBAR_OUTPUT/ui" "$stage/usr/lib/claude-syncbar/"
mkdir -p "$stage/usr/share/gnome-shell/extensions/claude-syncbar@griplabs.io"
cp -a "$SYNCBAR_OUTPUT/gnome/." "$stage/usr/share/gnome-shell/extensions/claude-syncbar@griplabs.io/"
install -m 755 "$SYNCBAR_OUTPUT/prepare-session.py" "$stage/usr/lib/claude-syncbar/prepare-session.py"
find "$stage" -type d -name __pycache__ -prune -exec rm -rf {} +
install -m 755 "$SYNCBAR_REPO/Ubuntu/packaging/claude-syncbar" "$stage/usr/bin/claude-syncbar"
install -m 644 "$SYNCBAR_REPO/Ubuntu/packaging/claude-syncbar.desktop" "$stage/usr/share/applications/io.github.claudesyncbar.Ubuntu.desktop"
install -m 644 "$SYNCBAR_REPO/Ubuntu/packaging/claude-syncbar.service" "$stage/usr/lib/systemd/user/"
install -m 644 "$SYNCBAR_REPO/Ubuntu/resources/claude-syncbar.png" "$stage/usr/share/icons/hicolor/256x256/apps/claude-syncbar.png"
cat >"$stage/DEBIAN/control" <<EOF
Package: claude-syncbar
Version: $version
Architecture: amd64
Maintainer: Claude SyncBar contributors
Section: utils
Priority: optional
Depends: python3, python3-gi, gir1.2-gtk-4.0 (>= 4.14), gir1.2-adw-1 (>= 1.5), libsecret-tools | gir1.2-secret-1, nodejs, jq, bash, tar, openssh-client, xdg-utils, libc6, libgcc-s1, libstdc++6, libssl3t64, libicu78, zlib1g
Description: Native Ubuntu Claude Code account switcher
 Review current and planned accounts, usage and SSH devices before applying
 a coordinated switch. Includes its .NET runtime. Login autostart is off.
EOF
cat >"$stage/usr/share/doc/claude-syncbar/README" <<'EOF'
Built for Ubuntu 26.04 amd64 with GTK 4 and libadwaita.
Launch Claude SyncBar from the applications menu or run claude-syncbar.
The backend user service starts only when the application is explicitly opened.
No login autostart is installed or enabled by this package.
On GNOME 50, enable claude-syncbar@griplabs.io in Extensions after logging in again
to use the weekly-usage tray popup. Opening it starts the backend on demand.
claude-syncbar --demo runs a separate synthetic demo without the real service.
Removal leaves account, browser and configuration data intact.
EOF
find "$stage" -type d -exec chmod 755 {} +
package="$SYNCBAR_OUTPUT/claude-syncbar_${version}_amd64.deb"
dpkg-deb --root-owner-group --build "$stage" "$package"
(cd "$SYNCBAR_OUTPUT" && sha256sum "$(basename "$package")") >"$package.sha256"
python3 "$SYNCBAR_REPO/Ubuntu/tests/test_package.py" "$package"
printf 'Package verified: %s\n' "$package"
