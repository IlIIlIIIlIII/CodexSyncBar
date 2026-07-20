#!/usr/bin/env bash

set -euo pipefail

ROOT=$(cd "$(dirname "$0")/.." && pwd)
OUTPUT_DIR="$ROOT/docs/images"
TEMP_HOME=$(mktemp -d "${TMPDIR:-/tmp}/codex-syncbar-readme.XXXXXX")

cleanup() {
  rm -rf "$TEMP_HOME"
}
trap cleanup EXIT

mkdir -p "$OUTPUT_DIR"
cd "$ROOT"
swift build
BIN_DIR=$(swift build --show-bin-path)
APP="$BIN_DIR/CodexSyncBar"

HOME="$TEMP_HOME" "$APP" \
  --readme-demo=popover \
  --readme-output="$OUTPUT_DIR/readme-popover.png"
HOME="$TEMP_HOME" "$APP" \
  --readme-demo=settings \
  --readme-output="$OUTPUT_DIR/readme-settings.png"

test -s "$OUTPUT_DIR/readme-popover.png"
test -s "$OUTPUT_DIR/readme-settings.png"
sips -g pixelWidth -g pixelHeight \
  "$OUTPUT_DIR/readme-popover.png" \
  "$OUTPUT_DIR/readme-settings.png"
