#!/usr/bin/env bash
# Download the LocalVQE GGUF weights used by VoiceAec.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
DEST="${1:-$ROOT/macos/Vendor/models}"
mkdir -p "$DEST"

FILES=(
  "localvqe-v1.2-1.3M-f32.gguf"
  "localvqe-v1.3-4.8M-f32.gguf"
  "localvqe-v1.4-aec-200K-f32.gguf"
)
if [ -n "${HF_ENDPOINT:-}" ]; then
  MIRRORS=("$HF_ENDPOINT")
else
  MIRRORS=("https://hf-mirror.com" "https://huggingface.co")
fi

for file in "${FILES[@]}"; do
  target="$DEST/$file"
  if [ -f "$target" ] && [ "$(stat -f%z "$target")" -gt 1000000 ]; then
    echo "==> $file already present"
    continue
  fi
  ok=0
  for mirror in "${MIRRORS[@]}"; do
    url="$mirror/LocalAI-io/LocalVQE/resolve/main/$file"
    echo "==> downloading $file from $mirror"
    if curl -fL --retry 3 --connect-timeout 20 -o "$target.part" "$url"; then
      mv "$target.part" "$target"
      ok=1
      break
    fi
  done
  if [ "$ok" -ne 1 ]; then
    echo "error: failed to download $file; set HF_ENDPOINT or download it manually into $DEST" >&2
    exit 1
  fi
  ls -lh "$target"
done
