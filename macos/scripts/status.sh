#!/usr/bin/env bash
# Print the local panel API status (token is read from the served HTML).
set -euo pipefail
BASE="${1:-http://127.0.0.1:8765}"
HTML="$(curl -fsS "$BASE/")"
TOKEN="$(printf '%s' "$HTML" | sed -n 's/.*name="voiceaec-token" content="\([^"]*\)".*/\1/p' | head -1)"
curl -fsS -H "X-VoiceAec-Token: $TOKEN" "$BASE/api/status" | python3 -m json.tool
