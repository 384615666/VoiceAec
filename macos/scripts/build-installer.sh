#!/usr/bin/env bash
# Build a drag-to-Applications DMG and an install-to-/Applications PKG.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
APP="${APP:-$ROOT/dist/VoiceAec.app}"
OUT="${OUT:-$ROOT/dist}"
VERSION="$(/usr/libexec/PlistBuddy -c 'Print :CFBundleShortVersionString' "$APP/Contents/Info.plist")"
NAME="VoiceAec-${VERSION}-macOS-universal"
DMG="$OUT/$NAME.dmg"
PKG="$OUT/$NAME.pkg"
STAGE_DMG="$OUT/.installer-dmg"
STAGE_PKG="$OUT/.installer-pkg"

[ -d "$APP" ] || { echo "error: app not found: $APP" >&2; exit 1; }
mkdir -p "$OUT"
rm -rf "$STAGE_DMG" "$STAGE_PKG"
mkdir -p "$STAGE_DMG" "$STAGE_PKG/Applications"

echo "==> staging $NAME"
ditto "$APP" "$STAGE_DMG/VoiceAec.app"
ln -s /Applications "$STAGE_DMG/Applications"
cp "$ROOT/macos/Resources/INSTALL.txt" "$STAGE_DMG/安装说明.txt"
cp "$ROOT/macos/README.md" "$STAGE_DMG/README-macOS.md"
chmod 644 "$STAGE_DMG/安装说明.txt" "$STAGE_DMG/README-macOS.md"
cat > "$STAGE_DMG/Install BlackHole 2ch.webloc" <<'PLIST'
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict><key>URL</key><string>https://existential.audio/blackhole/</string></dict></plist>
PLIST
cp "$APP/Contents/Resources/VoiceAec.icns" "$STAGE_DMG/.VolumeIcon.icns"
SetFile -a C "$STAGE_DMG" 2>/dev/null || true

echo "==> creating DMG"
rm -f "$DMG" "$OUT/.VoiceAec-raw.dmg" "$OUT/.VoiceAec-rw.dmg"
MNT="$OUT/.dmg-mount"
detach_dmg() { hdiutil detach "$MNT" >/dev/null 2>&1 || true; }
trap 'detach_dmg' EXIT

# `hdiutil create -srcfolder` needs the diskimages helper, which some
# sandboxed/CI environments block. Build an HFS+ image, clear the FinderInfo
# detritus that HFS adds to signed files, then compress it.
hdiutil makehybrid -quiet -hfs -hfs-volume-name "VoiceAec $VERSION" \
  -o "$OUT/.VoiceAec-raw.dmg" "$STAGE_DMG"
hdiutil convert -quiet "$OUT/.VoiceAec-raw.dmg" -format UDRW -o "$OUT/.VoiceAec-rw.dmg"

rm -rf "$MNT"; mkdir -p "$MNT"
hdiutil attach -nobrowse -readwrite -mountpoint "$MNT" "$OUT/.VoiceAec-rw.dmg" >/dev/null
xattr -cr "$MNT/VoiceAec.app"
SetFile -a C "$MNT" 2>/dev/null || true
codesign --verify --deep --strict "$MNT/VoiceAec.app"
detach_dmg
hdiutil convert -quiet "$OUT/.VoiceAec-rw.dmg" -format UDZO -o "$DMG"
rm -f "$OUT/.VoiceAec-raw.dmg" "$OUT/.VoiceAec-rw.dmg"

rm -rf "$MNT"; mkdir -p "$MNT"
hdiutil attach -nobrowse -readonly -mountpoint "$MNT" "$DMG" >/dev/null
codesign --verify --deep --strict "$MNT/VoiceAec.app"
echo "DMG verified; contents:"
ls "$MNT" | sed 's/^/  /'
detach_dmg
rm -rf "$MNT"
trap - EXIT
ls -lh "$DMG"

echo "==> creating PKG"
ditto --norsrc --noextattr "$APP" "$STAGE_PKG/Applications/VoiceAec.app"
xattr -cr "$STAGE_PKG/Applications/VoiceAec.app"
SCRIPTS="$OUT/.installer-pkg-scripts"
rm -rf "$SCRIPTS"; mkdir -p "$SCRIPTS"
cat > "$SCRIPTS/postinstall" <<'POSTINSTALL'
#!/bin/bash
# Strip any FinderInfo/resource-fork detritus the package layer may add; it
# would otherwise make the ad-hoc code signature invalid.
xattr -cr /Applications/VoiceAec.app 2>/dev/null || true
exit 0
POSTINSTALL
chmod 755 "$SCRIPTS/postinstall"
pkgbuild --quiet --root "$STAGE_PKG" --identifier "xyz.lsjx.voiceaec.pkg" \
  --version "$VERSION" --install-location "/" --ownership recommended \
  --filter '.*\.DS_Store' --scripts "$SCRIPTS" "$PKG"
rm -rf "$SCRIPTS"
echo "PKG payload sample:"
pkgutil --payload-files "$PKG" | head -10
echo "PKG payload AppleDouble count: $(pkgutil --payload-files "$PKG" | grep -c '/\._' || true)"
if pkgutil --expand-full "$PKG" "$OUT/.pkg-verify" >/dev/null 2>&1; then
  codesign --verify --deep --strict "$OUT/.pkg-verify/Payload/Applications/VoiceAec.app"
  echo "PKG extracted app signature OK"
  rm -rf "$OUT/.pkg-verify"
fi
ls -lh "$PKG"
rm -rf "$STAGE_DMG" "$STAGE_PKG"
echo "==> wrote $DMG and $PKG"
echo "Note: the app/installer are ad-hoc signed and not notarized; see macos/README.md."
