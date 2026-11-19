#!/bin/bash
# Removes the CloudOrc Backup Agent from this Mac. --purge also deletes its identity and local state.
# Backups already stored in CloudOrc are NOT touched.
set -euo pipefail
[[ "$(id -u)" -eq 0 ]] || { echo "Please run with sudo." >&2; exit 3; }
for label in com.cloudorc.backup.watchdog com.cloudorc.backup.agent; do
  launchctl bootout "system/$label" 2>/dev/null || true
  rm -f "/Library/LaunchDaemons/$label.plist"
done
rm -f /usr/local/bin/cloudorc-backup
rm -rf /Library/CloudOrc/Backup
pkgutil --forget com.cloudorc.backup.agent >/dev/null 2>&1 || true
if [[ "${1:-}" == "--purge" ]]; then
  rm -rf "/Library/Application Support/CloudOrcBackup" /Library/Logs/CloudOrcBackup
fi
echo "CloudOrc Backup Agent removed."
