#!/usr/bin/env bash
# CloudOrc Backup Agent - one-line installer for Linux, served by the CloudOrc portal.
#
#   curl -fsSL https://<portal>/install/backup.sh | sudo bash -s -- --code ABCD-1234 \
#        --download-base https://<downloads>/backup-agent/1.0.0 --api-base https://<api>/
#
# Detects the CPU (x86_64 / aarch64), downloads the matching package, verifies its SHA-256 against
# the published checksum, then runs the package's own installer. Nothing is installed if the
# checksum does not match.
set -euo pipefail

DOWNLOAD_BASE="${CLOUDORC_DOWNLOAD_BASE:-}"
VERSION="${CLOUDORC_VERSION:-}"
PASS=()
while [[ $# -gt 0 ]]; do
  case "$1" in
    --download-base) DOWNLOAD_BASE="${2:-}"; shift 2 ;;
    --download-base=*) DOWNLOAD_BASE="${1#*=}"; shift ;;
    --version) VERSION="${2:-}"; shift 2 ;;
    --version=*) VERSION="${1#*=}"; shift ;;
    *) PASS+=("$1"); shift ;;
  esac
done

[[ "$(id -u)" -eq 0 ]] || { echo "Please run with sudo." >&2; exit 3; }
[[ -n "$DOWNLOAD_BASE" ]] || { echo "--download-base is required (copy the full command from the CloudOrc portal)." >&2; exit 2; }
[[ "$DOWNLOAD_BASE" == https://* ]] || { echo "--download-base must be an https:// URL." >&2; exit 2; }

case "$(uname -m)" in
  x86_64|amd64) RID=linux-x64 ;;
  aarch64|arm64) RID=linux-arm64 ;;
  *) echo "Unsupported CPU architecture: $(uname -m)" >&2; exit 4 ;;
esac

if [[ -z "$VERSION" ]]; then
  VERSION="$(curl -fsSL "$DOWNLOAD_BASE/VERSION" | tr -d '[:space:]')"
fi

FILE="cloudorc-backup-$VERSION-$RID.tar.gz"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

echo "Downloading CloudOrc Backup Agent $VERSION ($RID) ..."
curl -fsSL --retry 3 -o "$WORK/$FILE" "$DOWNLOAD_BASE/$FILE"
curl -fsSL --retry 3 -o "$WORK/$FILE.sha256" "$DOWNLOAD_BASE/$FILE.sha256"

EXPECTED="$(cut -d' ' -f1 < "$WORK/$FILE.sha256")"
ACTUAL="$(sha256sum "$WORK/$FILE" | cut -d' ' -f1)"
if [[ "$EXPECTED" != "$ACTUAL" ]]; then
  echo "Checksum mismatch - the download is corrupt or was tampered with. Nothing was installed." >&2
  exit 5
fi

mkdir -p "$WORK/pkg"
tar -xzf "$WORK/$FILE" -C "$WORK/pkg"
bash "$WORK/pkg/share/install.sh" "${PASS[@]}"
