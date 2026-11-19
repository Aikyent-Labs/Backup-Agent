#!/usr/bin/env bash
# Removes the CloudOrc Backup Agent from this machine.
#   sudo /opt/cloudorc-backup/share/uninstall.sh            keep identity + local state (re-install reconnects)
#   sudo /opt/cloudorc-backup/share/uninstall.sh --purge    also delete /var/lib/cloudorc-backup
# Backups already stored in CloudOrc are NOT touched.
set -euo pipefail
[[ "$(id -u)" -eq 0 ]] || { echo "Please run as root (sudo)." >&2; exit 3; }

systemctl disable --now cloudorc-backup-watchdog.service cloudorc-backup-agent.service 2>/dev/null || true
rm -f /etc/systemd/system/cloudorc-backup-agent.service /etc/systemd/system/cloudorc-backup-watchdog.service
systemctl daemon-reload 2>/dev/null || true
rm -f /usr/local/bin/cloudorc-backup
rm -rf /opt/cloudorc-backup
if [[ "${1:-}" == "--purge" ]]; then
  rm -rf /var/lib/cloudorc-backup
  echo "CloudOrc Backup Agent removed (including its identity and local state)."
else
  echo "CloudOrc Backup Agent removed. Local state kept in /var/lib/cloudorc-backup (use --purge to delete)."
fi
