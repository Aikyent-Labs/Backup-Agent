#!/bin/sh
# .deb / .rpm post-install: data directory, permissions, start both services.
set -e
install -d -m 0700 -o root -g root /var/lib/cloudorc-backup
chown -R root:root /opt/cloudorc-backup
chmod -R go-w /opt/cloudorc-backup
if command -v restorecon >/dev/null 2>&1; then restorecon -R /opt/cloudorc-backup >/dev/null 2>&1 || true; fi
if [ -d /run/systemd/system ]; then
  systemctl daemon-reload
  systemctl enable cloudorc-backup-agent.service cloudorc-backup-watchdog.service >/dev/null 2>&1 || true
  systemctl restart cloudorc-backup-agent.service cloudorc-backup-watchdog.service || true
fi
echo "CloudOrc Backup Agent installed. Activate it with: sudo cloudorc-backup activate <CODE>"
