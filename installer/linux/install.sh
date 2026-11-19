#!/usr/bin/env bash
# CloudOrc Backup Agent - Linux installer (run from the extracted package: <pkg>/share/install.sh).
#
#   sudo ./share/install.sh [--code ABCD-1234] [--api-base https://api.example/] [--portal-url https://portal.example/]
#
# Installs to /opt/cloudorc-backup, data in /var/lib/cloudorc-backup, two systemd services:
#   cloudorc-backup-agent.service     the agent
#   cloudorc-backup-watchdog.service  keeps the agent healthy
# Re-running it upgrades in place and keeps the activation (identity lives in the data directory).
set -euo pipefail

PREFIX=/opt/cloudorc-backup
DATA=/var/lib/cloudorc-backup
UNIT_DIR=/etc/systemd/system
CODE="" API_BASE="" PORTAL_URL=""

while [[ $# -gt 0 ]]; do
  case "$1" in
    --code) CODE="${2:-}"; shift 2 ;;
    --code=*) CODE="${1#*=}"; shift ;;
    --api-base) API_BASE="${2:-}"; shift 2 ;;
    --api-base=*) API_BASE="${1#*=}"; shift ;;
    --portal-url) PORTAL_URL="${2:-}"; shift 2 ;;
    --portal-url=*) PORTAL_URL="${1#*=}"; shift ;;
    -h|--help) sed -n '2,12p' "$0"; exit 0 ;;
    *) echo "Unknown option: $1" >&2; exit 2 ;;
  esac
done

if [[ "$(id -u)" -ne 0 ]]; then
  echo "Please run as root (sudo)." >&2
  exit 3
fi

if ! command -v systemctl >/dev/null 2>&1 || [[ ! -d /run/systemd/system ]]; then
  echo "systemd is required (Ubuntu 20.04+, Debian 11+, RHEL/Rocky/Alma 8+, SUSE 15+)." >&2
  exit 4
fi

SRC="$(cd "$(dirname "$0")/.." && pwd)"
if [[ ! -x "$SRC/bin/cloudorc-backup" ]]; then
  echo "Package files not found next to this script ($SRC/bin/cloudorc-backup)." >&2
  exit 5
fi

echo "Installing CloudOrc Backup Agent $(cat "$SRC/VERSION" 2>/dev/null || echo) to $PREFIX ..."
systemctl stop cloudorc-backup-watchdog.service cloudorc-backup-agent.service 2>/dev/null || true

if [[ "$SRC" != "$PREFIX" ]]; then
  mkdir -p "$PREFIX"
  rm -rf "$PREFIX/bin.new" "$PREFIX/engines.new"
  cp -a "$SRC/bin" "$PREFIX/bin.new"
  cp -a "$SRC/engines" "$PREFIX/engines.new"
  rm -rf "$PREFIX/bin" "$PREFIX/engines"
  mv "$PREFIX/bin.new" "$PREFIX/bin"
  mv "$PREFIX/engines.new" "$PREFIX/engines"
  mkdir -p "$PREFIX/share"
  cp -a "$SRC/share/." "$PREFIX/share/"
  [[ -f "$SRC/VERSION" ]] && cp "$SRC/VERSION" "$PREFIX/VERSION"
fi

chown -R root:root "$PREFIX"
chmod -R go-w "$PREFIX"
install -d -m 0700 -o root -g root "$DATA"

install -m 0644 "$PREFIX/share/cloudorc-backup-agent.service" "$UNIT_DIR/cloudorc-backup-agent.service"
install -m 0644 "$PREFIX/share/cloudorc-backup-watchdog.service" "$UNIT_DIR/cloudorc-backup-watchdog.service"
ln -sf "$PREFIX/bin/cloudorc-backup" /usr/local/bin/cloudorc-backup

# SELinux (RHEL family): label the binaries as regular executables so systemd may start them.
if command -v restorecon >/dev/null 2>&1; then
  restorecon -R "$PREFIX" >/dev/null 2>&1 || true
fi

if [[ -n "$API_BASE" ]]; then
  args=(set-activation --api-base "$API_BASE")
  [[ -n "$PORTAL_URL" ]] && args+=(--portal-url "$PORTAL_URL")
  "$PREFIX/bin/cloudorc-backup" "${args[@]}"
fi

systemctl daemon-reload
systemctl enable --now cloudorc-backup-agent.service cloudorc-backup-watchdog.service

if [[ -n "$CODE" ]]; then
  "$PREFIX/bin/cloudorc-backup" activate "$CODE"
fi

echo
echo "CloudOrc Backup Agent installed."
if [[ -z "$CODE" ]]; then
  echo "Activate this machine with the code from the CloudOrc portal:"
  echo "  sudo cloudorc-backup activate <CODE>"
fi
echo "Status: sudo cloudorc-backup status"
