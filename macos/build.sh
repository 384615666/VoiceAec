#!/usr/bin/env bash
# Build VoiceAec.app for macOS (universal by default).
#
# Usage:
#   ./macos/build.sh                 # release, x86_64 + arm64, ad-hoc signed
#   CONFIG=debug ./macos/build.sh    # debug, host arch
#   UNIVERSAL=0 ./macos/build.sh     # host arch only
#   CODESIGN_IDENTITY="Developer ID Application: ..." ./macos/build.sh
#   ZIP=1 ./macos/build.sh           # also make a distributable zip
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
MACOS="$ROOT/macos"
CONFIG="${CONFIG:-release}"
UNIVERSAL="${UNIVERSAL:-1}"
SIGN_ID="${CODESIGN_IDENTITY:--}"
ZIP="${ZIP:-0}"
APP="$ROOT/dist/VoiceAec.app"

export TMPDIR="$ROOT/.tmp"
export CLANG_MODULE_CACHE_PATH="$ROOT/.cache/clang"
mkdir -p "$TMPDIR" "$CLANG_MODULE_CACHE_PATH" "$ROOT/.cache/swiftpm" \
         "$ROOT/.cache/swiftpm-config" "$ROOT/.cache/swiftpm-security" "$ROOT/dist"

cd "$MACOS"
command -v swift >/dev/null || { echo "error: Swift toolchain not found (xcode-select --install)" >&2; exit 1; }
command -v codesign >/dev/null || { echo "error: codesign not found" >&2; exit 1; }

if [ ! -f "$MACOS/Vendor/liblocalvqe.dylib" ]; then
  echo "==> liblocalvqe.dylib missing; building it"
  "$MACOS/scripts/build-localvqe.sh"
fi
if ! ls "$MACOS/Vendor/models"/*.gguf >/dev/null 2>&1; then
  echo "==> models missing; downloading"
  "$MACOS/scripts/fetch-models.sh"
fi

SWIFT_FLAGS=(
  --disable-sandbox
  --cache-path "$ROOT/.cache/swiftpm"
  --config-path "$ROOT/.cache/swiftpm-config"
  --security-path "$ROOT/.cache/swiftpm-security"
)
BUILD_FLAGS=(-c "$CONFIG" "${SWIFT_FLAGS[@]}")
HOST_ARCH="$(uname -m)"
BIN=""

if [ "$UNIVERSAL" = "1" ]; then
  echo "==> building x86_64 $CONFIG"
  swift build "${BUILD_FLAGS[@]}" --product VoiceAec --triple x86_64-apple-macosx13.0 --scratch-path "$MACOS/.build-x64"
  echo "==> building arm64 $CONFIG"
  swift build "${BUILD_FLAGS[@]}" --product VoiceAec --triple arm64-apple-macosx13.0 --scratch-path "$MACOS/.build-arm64"
  BIN="$MACOS/.build/VoiceAec-universal"
  mkdir -p "$MACOS/.build"
  lipo -create "$MACOS/.build-x64/x86_64-apple-macosx/$CONFIG/VoiceAec" \
              "$MACOS/.build-arm64/arm64-apple-macosx/$CONFIG/VoiceAec" -output "$BIN"
else
  echo "==> building $HOST_ARCH $CONFIG"
  swift build "${BUILD_FLAGS[@]}" --product VoiceAec --triple "$HOST_ARCH-apple-macosx13.0" --scratch-path "$MACOS/.build-host"
  BIN="$MACOS/.build-host/$HOST_ARCH-apple-macosx/$CONFIG/VoiceAec"
fi
[ -x "$BIN" ] || { echo "error: build output not found: $BIN" >&2; exit 1; }
lipo -info "$BIN"

# Keep the macOS copy of the panel in sync with the shared one.
cp "$ROOT/wwwroot/index.html" "$MACOS/Resources/wwwroot/index.html"

echo "==> assembling $APP"
rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Frameworks" \
         "$APP/Contents/Resources/wwwroot" "$APP/Contents/Resources/models"
cp "$BIN" "$APP/Contents/MacOS/VoiceAec"
chmod +x "$APP/Contents/MacOS/VoiceAec"
cp "$MACOS/Vendor/liblocalvqe.dylib" "$APP/Contents/Frameworks/"
cp "$MACOS/Resources/Info.plist" "$APP/Contents/Info.plist"
cp "$MACOS/Resources/VoiceAec.icns" "$APP/Contents/Resources/"
cp "$MACOS/Resources/macos.css" "$MACOS/Resources/macos.js" "$APP/Contents/Resources/"
cp "$MACOS/Resources/wwwroot/index.html" "$APP/Contents/Resources/wwwroot/"
cp "$MACOS/Vendor/models/"*.gguf "$APP/Contents/Resources/models/"
cp "$ROOT/LICENSE" "$APP/Contents/Resources/LICENSE"
cp "$ROOT/THIRD-PARTY-NOTICES.md" "$APP/Contents/Resources/"
printf 'APPL????' > "$APP/Contents/PkgInfo"

echo "==> signing with ${SIGN_ID}"
SIGN_ARGS=(--force --sign "$SIGN_ID")
if [ "$SIGN_ID" != "-" ]; then
  SIGN_ARGS+=(--timestamp)
fi
codesign "${SIGN_ARGS[@]}" "$APP/Contents/Frameworks/liblocalvqe.dylib"
codesign "${SIGN_ARGS[@]}" --deep --options runtime \
  --entitlements "$MACOS/Resources/VoiceAec.entitlements" "$APP"
codesign --verify --deep --strict --verbose=1 "$APP"

echo
echo "Built: $APP"
du -sh "$APP"
echo "Launch: open \"$APP\""
echo
echo "First run:"
echo "  1. Allow Microphone, then Screen Recording (System Settings → Privacy & Security)."
echo "  2. Install BlackHole 2ch: https://existential.audio/blackhole/"
echo "  3. Restart VoiceAec, pick BlackHole 2ch as Output, and BlackHole 2ch as the mic in Discord/QQ."

if [ "$ZIP" = "1" ]; then
  RELEASE="$ROOT/dist/VoiceAec-macOS-universal"
  echo "==> creating $RELEASE.zip"
  rm -rf "$RELEASE"
  mkdir -p "$RELEASE"
  ditto "$APP" "$RELEASE/VoiceAec.app"
  cp "$MACOS/README.md" "$RELEASE/README-macOS.md" 2>/dev/null || true
  COPYFILE_DISABLE=1 ditto -c -k --norsrc --noextattr --keepParent "$RELEASE" "$ROOT/dist/VoiceAec-macOS-universal.zip"
  ls -lh "$ROOT/dist/VoiceAec-macOS-universal.zip"
fi
