#!/bin/sh
# .deb / .rpm pre-remove: stop services on a real removal (an upgrade keeps them running).
set -e
case "$1" in
  upgrade|1) exit 0 ;;   # deb "upgrade" / rpm "1" = another version remains installed
esac
if [ -d /run/systemd/system ]; then
  systemctl disable --now cloudorc-backup-watchdog.service cloudorc-backup-agent.service >/dev/null 2>&1 || true
fi
