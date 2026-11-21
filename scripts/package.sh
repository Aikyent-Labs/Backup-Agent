#!/usr/bin/env bash
# Builds the CloudOrc Backup Agent install tree + archive for ONE runtime identifier.
#
#   scripts/package.sh <rid> <version>        rid: win-x64 | linux-x64 | linux-arm64 | osx-x64 | osx-arm64
#
# Output (dist/):
#   dist/<rid>/cloudorc-backup/bin/        agent, worker, watchdog, updater + ONE shared .NET runtime
#   dist/<rid>/cloudorc-backup/engines/    kopia + engines.manifest.json (SHA-256 pinned)
#   dist/<rid>/cloudorc-backup/share/      service files + install/uninstall scripts for that OS
#   dist/cloudorc-backup-<version>-<rid>.tar.gz (+ .sha256)   Linux/macOS package AND self-update package
#
# The Windows setup.exe is built from dist/win-x64/cloudorc-backup by installer/windows (Inno Setup).
set -euo pipefail

RID="${1:?usage: package.sh <rid> <version>}"
VERSION="${2:?usage: package.sh <rid> <version>}"
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
DOTNET="${DOTNET:-dotnet}"
OUT="$ROOT/dist/$RID/cloudorc-backup"
BIN="$OUT/bin"

case "$RID" in
  win-x64|linux-x64|linux-arm64|osx-x64|osx-arm64) ;;
  *) echo "Unsupported RID: $RID" >&2; exit 2 ;;
esac

echo "==> Packaging CloudOrc Backup Agent $VERSION for $RID"
rm -rf "$ROOT/dist/$RID"
mkdir -p "$BIN" "$OUT/engines" "$OUT/share"

# Every program is published self-contained into the SAME folder: they share one runtime copy.
# Not single-file and not trimmed: plain, signed-friendly files that antivirus products do not flag.
for project in CloudOrc.Backup.Agent CloudOrc.Backup.Worker CloudOrc.Backup.Watchdog CloudOrc.Backup.Updater; do
  echo "--> publish $project"
  "$DOTNET" publish "$ROOT/src/$project/$project.csproj" -c Release -r "$RID" --self-contained true \
    -p:Version="$VERSION" -p:PublishSingleFile=false -p:PublishTrimmed=false -p:DebugType=none \
    -p:GenerateDocumentationFile=false -o "$BIN" -nologo -v quiet
done
# The programs share bin/: every DLL must be the version all of them expect.
pwsh -NoProfile -File "$ROOT/scripts/check-shared-bin.ps1" -Bin "$BIN"

# ---- backup engine (kopia) for this RID, SHA-256 pinned
"$ROOT/scripts/fetch-engines.sh" "$RID" "$OUT/engines"

# ---- OS integration files
case "$RID" in
  linux-*)
    cp "$ROOT/installer/linux/cloudorc-backup-agent.service" "$ROOT/installer/linux/cloudorc-backup-watchdog.service" "$OUT/share/"
    cp "$ROOT/installer/linux/install.sh" "$ROOT/installer/linux/uninstall.sh" "$OUT/share/"
    chmod 0755 "$OUT/share/"*.sh
    ;;
  osx-*)
    cp "$ROOT/installer/macos/com.cloudorc.backup.agent.plist" "$ROOT/installer/macos/com.cloudorc.backup.watchdog.plist" "$OUT/share/"
    cp "$ROOT/installer/macos/install.sh" "$ROOT/installer/macos/uninstall.sh" "$OUT/share/"
    chmod 0755 "$OUT/share/"*.sh
    ;;
esac

if [[ "$RID" != win-* ]]; then
  chmod 0755 "$BIN/cloudorc-backup" "$BIN/cloudorc-backup-worker" "$BIN/cloudorc-backup-watchdog" "$BIN/cloudorc-backup-updater"
fi
echo "$VERSION" > "$OUT/VERSION"

# ---- archive (also the self-update package on Linux/macOS)
ARCHIVE="$ROOT/dist/cloudorc-backup-$VERSION-$RID.tar.gz"
tar -C "$OUT" --owner=0 --group=0 -czf "$ARCHIVE" . 2>/dev/null || tar -C "$OUT" -czf "$ARCHIVE" .
( cd "$ROOT/dist" && { sha256sum "$(basename "$ARCHIVE")" 2>/dev/null || shasum -a 256 "$(basename "$ARCHIVE")"; } > "$(basename "$ARCHIVE").sha256" )

SIZE=$(du -sh "$OUT" | cut -f1)
echo "==> $ARCHIVE ($(du -h "$ARCHIVE" | cut -f1) compressed, $SIZE installed)"
cat "$ARCHIVE.sha256"
