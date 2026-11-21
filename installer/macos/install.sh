#!/bin/bash
# CloudOrc Backup Agent - macOS installer (run from the extracted package: <pkg>/share/install.sh;
# the .pkg runs it as its postinstall step).
#
#   sudo ./share/install.sh [--token CLB1.xxxx] [--api-base https://api.example/] [--portal-url https://portal.example/]
#   On a Mac that is already connected it only upgrades the program (never re-activates).
#
# Installs to /Library/CloudOrc/Backup, data in "/Library/Application Support/CloudOrcBackup",
# two launchd daemons: com.cloudorc.backup.agent and com.cloudorc.backup.watchdog.
set -euo pipefail

PREFIX=/Library/CloudOrc/Backup
DATA="/Library/Application Support/CloudOrcBackup"
CODE="" API_BASE="" PORTAL_URL=""
while [[ $# -gt 0 ]]; do
  case "$1" in
    --token) CODE="${2:-}"; shift 2 ;;
    --api-base) API_BASE="${2:-}"; shift 2 ;;
    --portal-url) PORTAL_URL="${2:-}"; shift 2 ;;
    *) echo "Unknown option: $1" >&2; exit 2 ;;
  esac
done
# Only the enrollment token from the portal (Backup > Computers > Add device) activates a computer.
if [[ -n "$CODE" && "${CODE:0:5}" != "CLB1." && "${CODE:0:5}" != "clb1." ]]; then
  echo "--token must be the enrollment token from the CloudOrc portal (it starts with CLB1.)." >&2
  exit 2
fi
[[ "$(id -u)" -eq 0 ]] || { echo "Please run with sudo." >&2; exit 3; }

SRC="$(cd "$(dirname "$0")/.." && pwd)"
for label in com.cloudorc.backup.watchdog com.cloudorc.backup.agent; do
  launchctl bootout "system/$label" 2>/dev/null || true
done

if [[ "$SRC" != "$PREFIX" ]]; then
  mkdir -p "$PREFIX"
  rm -rf "$PREFIX/bin" "$PREFIX/engines"
  cp -R "$SRC/bin" "$PREFIX/bin"
  cp -R "$SRC/engines" "$PREFIX/engines"
  mkdir -p "$PREFIX/share"
  cp -R "$SRC/share/." "$PREFIX/share/"
  [[ -f "$SRC/VERSION" ]] && cp "$SRC/VERSION" "$PREFIX/VERSION"
fi

chown -R root:wheel "$PREFIX"
chmod -R go-w "$PREFIX"
# Downloaded files carry the quarantine flag; the agent's own binaries are trusted by this installer.
xattr -dr com.apple.quarantine "$PREFIX" 2>/dev/null || true
mkdir -p "$DATA" /Library/Logs/CloudOrcBackup
chmod 0700 "$DATA"
mkdir -p /usr/local/bin
ln -sf "$PREFIX/bin/cloudorc-backup" /usr/local/bin/cloudorc-backup

for label in com.cloudorc.backup.agent com.cloudorc.backup.watchdog; do
  install -m 0644 -o root -g wheel "$PREFIX/share/$label.plist" "/Library/LaunchDaemons/$label.plist"
done

ALREADY_ACTIVATED=0
if [[ -f "$DATA/enrollment.json" ]]; then
  ALREADY_ACTIVATED=1
  CODE=""
  echo "This Mac is already connected to CloudOrc - upgrading the program only."
fi
case "$CODE" in CLB1.*|clb1.*) API_BASE="" ;; esac

if [[ -n "$API_BASE" && "$ALREADY_ACTIVATED" -eq 0 ]]; then
  args=(set-activation --api-base "$API_BASE")
  [[ -n "$PORTAL_URL" ]] && args+=(--portal-url "$PORTAL_URL")
  "$PREFIX/bin/cloudorc-backup" "${args[@]}"
fi

for label in com.cloudorc.backup.agent com.cloudorc.backup.watchdog; do
  launchctl bootstrap system "/Library/LaunchDaemons/$label.plist"
done

if [[ -n "$CODE" ]]; then
  "$PREFIX/bin/cloudorc-backup" activate "$CODE"
fi

cat <<'MSG'

CloudOrc Backup Agent installed.
IMPORTANT (macOS privacy): to back up Documents, Desktop, Mail and other protected folders, allow
  /Library/CloudOrc/Backup/bin/cloudorc-backup-worker  and  /Library/CloudOrc/Backup/engines/kopia
in System Settings > Privacy & Security > Full Disk Access (or push a PPPC profile through your MDM).
Activate:  sudo cloudorc-backup activate <TOKEN>      Status:  sudo cloudorc-backup status
MSG
