#!/usr/bin/env bash

set -euo pipefail

ROOT=$(cd "$(dirname "$0")/.." && pwd)
PACKAGE_ROOT="$ROOT/Support/cursor-sdk-runtime"
BUILD_ROOT="$ROOT/.build/cursor-sdk-runtime"
ARCHIVE="$BUILD_ROOT/cursor-sdk-runtime.tar.gz"
MANIFEST="$BUILD_ROOT/cursor-sdk-runtime.manifest"
FORMAT_MARKER="$BUILD_ROOT/archive-format"
SDK_VERSION="1.0.28"
ARCHIVE_FORMAT="portable-no-xattrs-v1"

command -v npm >/dev/null 2>&1 || {
  printf '%s\n' "npm is required to package the Cursor SDK runtime" >&2
  exit 1
}
command -v node >/dev/null 2>&1 || {
  printf '%s\n' "Node.js is required to validate the Cursor SDK runtime" >&2
  exit 1
}

lock_hash=$(/usr/bin/shasum -a 256 "$PACKAGE_ROOT/package-lock.json" | /usr/bin/awk '{print $1}')
expected_marker="lock_sha256=$lock_hash"
if [ -f "$ARCHIVE" ] && [ -f "$MANIFEST" ] && [ -f "$FORMAT_MARKER" ] && \
   /usr/bin/grep -Fqx "sdk_version=$SDK_VERSION" "$MANIFEST" && \
   /usr/bin/grep -Fqx "$expected_marker" "$MANIFEST" && \
   /usr/bin/grep -Fqx "$ARCHIVE_FORMAT" "$FORMAT_MARKER"; then
  printf '%s\n%s\n' "$ARCHIVE" "$MANIFEST"
  exit 0
fi

mkdir -p "$ROOT/.build"
stage=$(mktemp -d "$ROOT/.build/cursor-sdk-runtime-stage.XXXXXX")
archive_new="$ROOT/.build/.cursor-sdk-runtime.$$.tar.gz"
manifest_new="$ROOT/.build/.cursor-sdk-runtime.$$.manifest"
format_marker_new="$ROOT/.build/.cursor-sdk-runtime.$$.format"
cleanup() {
  rm -rf "$stage"
  rm -f "$archive_new" "$manifest_new" "$format_marker_new"
}
trap cleanup EXIT HUP INT TERM

cp "$PACKAGE_ROOT/package.json" "$stage/package.json"
cp "$PACKAGE_ROOT/package-lock.json" "$stage/package-lock.json"
(
  cd "$stage"
  npm ci --force --ignore-scripts --no-audit --no-fund
)

# npm creates only this convenience symlink. The bridge resolves every SDK
# binary through the platform package itself, so omit it and keep the runtime
# archive free of links before it is installed on macOS or Linux.
rm -rf "$stage/node_modules/.bin"

node - "$stage/node_modules" "$SDK_VERSION" <<'NODE'
const fs = require("node:fs");
const path = require("node:path");
const root = process.argv[2];
const version = process.argv[3];
for (const name of [
  "sdk",
  "sdk-darwin-arm64",
  "sdk-darwin-x64",
  "sdk-linux-arm64",
  "sdk-linux-x64",
]) {
  const metadata = JSON.parse(fs.readFileSync(path.join(root, "@cursor", name, "package.json"), "utf8"));
  if (metadata.name !== `@cursor/${name}` || metadata.version !== version) process.exit(65);
}
NODE

COPYFILE_DISABLE=1 /usr/bin/tar --no-xattrs -czf "$archive_new" -C "$stage" node_modules
archive_hash=$(/usr/bin/shasum -a 256 "$archive_new" | /usr/bin/awk '{print $1}')
printf '%s\n%s\n%s\n%s\n' \
  'schema_version=1' \
  "sdk_version=$SDK_VERSION" \
  "$expected_marker" \
  "archive_sha256=$archive_hash" >"$manifest_new"
chmod 600 "$archive_new" "$manifest_new"
printf '%s\n' "$ARCHIVE_FORMAT" >"$format_marker_new"
chmod 600 "$format_marker_new"
mkdir -p "$BUILD_ROOT"
mv -f "$archive_new" "$ARCHIVE"
mv -f "$manifest_new" "$MANIFEST"
mv -f "$format_marker_new" "$FORMAT_MARKER"

printf '%s\n%s\n' "$ARCHIVE" "$MANIFEST"
