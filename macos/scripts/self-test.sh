#!/usr/bin/env bash
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$ROOT/macos"
export TMPDIR="$ROOT/.tmp" CLANG_MODULE_CACHE_PATH="$ROOT/.cache/clang"
export VOICEAEC_MODELS="$ROOT/macos/Vendor/models"
swift build -c release --product VoiceAecSelfTest --disable-sandbox \
  --cache-path "$ROOT/.cache/swiftpm" --config-path "$ROOT/.cache/swiftpm-config" \
  --security-path "$ROOT/.cache/swiftpm-security" --scratch-path "$ROOT/macos/.build-selftest"
BIN="$ROOT/macos/.build-selftest/$(uname -m)-apple-macosx/release/VoiceAecSelfTest"
"$BIN"
