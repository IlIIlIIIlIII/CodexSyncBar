#!/usr/bin/env bash

set -euo pipefail

ROOT=$(cd "$(dirname "$0")" && pwd)
APP_NAME="Codex SyncBar"
BUILD_DIR="$ROOT/.build/release"
STAGE_DIR="$ROOT/dist"
APP="$STAGE_DIR/$APP_NAME.app"
OUTPUT_DIR="${1:-$ROOT/../../outputs}"

cd "$ROOT"
if [ "${CODEX_SYNCBAR_UNIVERSAL:-0}" = 1 ]; then
  ARM_SCRATCH="$ROOT/.build/release-arm64"
  INTEL_SCRATCH="$ROOT/.build/release-x86_64"
  UNIVERSAL_DIR="$ROOT/.build/release-universal"
  swift build -c release --triple arm64-apple-macosx13.0 --scratch-path "$ARM_SCRATCH"
  swift build -c release --triple x86_64-apple-macosx13.0 --scratch-path "$INTEL_SCRATCH"
  ARM_BIN=$(swift build -c release --triple arm64-apple-macosx13.0 --scratch-path "$ARM_SCRATCH" --show-bin-path)
  INTEL_BIN=$(swift build -c release --triple x86_64-apple-macosx13.0 --scratch-path "$INTEL_SCRATCH" --show-bin-path)
  rm -rf "$UNIVERSAL_DIR"
  mkdir -p "$UNIVERSAL_DIR"
  lipo -create \
    "$ARM_BIN/CodexSyncBar" \
    "$INTEL_BIN/CodexSyncBar" \
    -output "$UNIVERSAL_DIR/CodexSyncBar"
  BUILD_DIR="$UNIVERSAL_DIR"
else
  swift build -c release
fi

rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources" "$OUTPUT_DIR"
cp "$BUILD_DIR/CodexSyncBar" "$APP/Contents/MacOS/CodexSyncBar"
cp "$ROOT/Resources/Info.plist" "$APP/Contents/Info.plist"
cp "$ROOT/Resources/AppIcon.icns" "$APP/Contents/Resources/AppIcon.icns"
cp "$ROOT/Support/gpt-switch" "$APP/Contents/Resources/gpt-switch"
cp "$ROOT/Support/codex-syncbar-askpass" "$APP/Contents/Resources/codex-syncbar-askpass"
cp "$ROOT/Support/usage-summary.mjs" "$APP/Contents/Resources/usage-summary.mjs"
chmod 755 "$APP/Contents/MacOS/CodexSyncBar"
chmod 755 "$APP/Contents/Resources/gpt-switch"
chmod 700 "$APP/Contents/Resources/codex-syncbar-askpass"
chmod 755 "$APP/Contents/Resources/usage-summary.mjs"

# Keep Swift's linker-signed flag when applying the bundle's ad-hoc resource seal.
# This lets the executable keep running when launched directly on Macs without a
# configured Apple code-signing identity.
codesign --force --deep --sign - --timestamp=none --options 0x20000 "$APP"
codesign --verify --deep --strict --verbose=2 "$APP"

rm -rf "$OUTPUT_DIR/$APP_NAME.app" "$OUTPUT_DIR/$APP_NAME.zip"
ditto "$APP" "$OUTPUT_DIR/$APP_NAME.app"
ditto -c -k --sequesterRsrc --keepParent "$APP" "$OUTPUT_DIR/$APP_NAME.zip"

printf '%s\n' "$OUTPUT_DIR/$APP_NAME.app"
printf '%s\n' "$OUTPUT_DIR/$APP_NAME.zip"
