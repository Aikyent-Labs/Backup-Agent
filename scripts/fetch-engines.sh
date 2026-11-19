#!/usr/bin/env bash
# Downloads the pinned backup engine for one RID, verifies the archive SHA-256 from engines.lock.json,
# extracts the binary and writes engines.manifest.json with the SHA-256 of the binary itself (the
# worker re-checks it before every run and refuses a modified binary).
#
#   scripts/fetch-engines.sh <rid> <output-dir>
set -euo pipefail

RID="${1:?usage: fetch-engines.sh <rid> <output-dir>}"
DEST="${2:?usage: fetch-engines.sh <rid> <output-dir>}"
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
LOCK="$ROOT/engines/engines.lock.json"
CACHE="${ENGINE_CACHE:-$ROOT/engines/.cache}"
mkdir -p "$DEST" "$CACHE"

read -r NAME VERSION URL SHA EXE < <(python3 - "$LOCK" "$RID" <<'PY'
import json, sys
lock, rid = sys.argv[1], sys.argv[2]
for e in json.load(open(lock))["engines"]:
    if e.get("rid") == rid and e.get("url"):
        print(e["name"], e["version"], e["url"], e["sha256"], e["exe"]); break
else:
    sys.exit(f"no engine pinned for {rid}")
PY
)

ARCHIVE="$CACHE/$(basename "$URL")"
if [[ ! -f "$ARCHIVE" ]]; then
  echo "--> download $URL"
  curl -fsSL --retry 3 -o "$ARCHIVE.part" "$URL"
  mv "$ARCHIVE.part" "$ARCHIVE"
fi

ACTUAL=$( (sha256sum "$ARCHIVE" 2>/dev/null || shasum -a 256 "$ARCHIVE") | cut -d' ' -f1)
if [[ "$ACTUAL" != "$SHA" ]]; then
  echo "SHA-256 mismatch for $ARCHIVE: expected $SHA, got $ACTUAL - refusing to package it." >&2
  rm -f "$ARCHIVE"
  exit 3
fi

WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT
case "$ARCHIVE" in
  *.zip) unzip -q "$ARCHIVE" -d "$WORK" ;;
  *.tar.gz) tar -xzf "$ARCHIVE" -C "$WORK" ;;
esac
FOUND="$(find "$WORK" -type f -name "$EXE" | head -n1)"
[[ -n "$FOUND" ]] || { echo "$EXE not found inside $ARCHIVE" >&2; exit 4; }
cp "$FOUND" "$DEST/$EXE"
[[ "$EXE" == *.exe ]] || chmod 0755 "$DEST/$EXE"

BIN_SHA=$( (sha256sum "$DEST/$EXE" 2>/dev/null || shasum -a 256 "$DEST/$EXE") | cut -d' ' -f1)
cat > "$DEST/engines.manifest.json" <<JSON
{
  "engines": [
    { "name": "$NAME", "version": "$VERSION", "file": "$EXE", "sha256": "$BIN_SHA" }
  ]
}
JSON
echo "--> $NAME $VERSION ($RID) verified and pinned: $BIN_SHA"
