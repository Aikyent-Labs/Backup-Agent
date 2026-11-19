#!/bin/bash
# Builds CloudOrcBackup-<version>-<rid>.pkg from dist/<rid>/cloudorc-backup (run on macOS).
#
#   installer/macos/build-pkg.sh <osx-x64|osx-arm64> <version>
#
# Optional signing / notarization (needs an Apple Developer ID - without it macOS Gatekeeper warns):
#   APPLE_APP_IDENTITY="Developer ID Application: <Company> (TEAMID)"
#   APPLE_INSTALLER_IDENTITY="Developer ID Installer: <Company> (TEAMID)"
#   APPLE_NOTARY_PROFILE=<notarytool keychain profile>
set -euo pipefail

RID="${1:?usage: build-pkg.sh <rid> <version>}"
VERSION="${2:?usage: build-pkg.sh <rid> <version>}"
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
TREE="$ROOT/dist/$RID/cloudorc-backup"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

[[ -x "$TREE/bin/cloudorc-backup" ]] || { echo "Run scripts/package.sh $RID $VERSION first." >&2; exit 2; }

# Hardened-runtime code signing of every Mach-O file (agent, worker, watchdog, updater, runtime, kopia).
if [[ -n "${APPLE_APP_IDENTITY:-}" ]]; then
  ENT="$ROOT/installer/macos/entitlements.plist"
  find "$TREE/bin" "$TREE/engines" -type f \( -perm -111 -o -name '*.dylib' \) -print0 |
    xargs -0 -I{} codesign --force --timestamp --options runtime --entitlements "$ENT" --sign "$APPLE_APP_IDENTITY" "{}"
fi

mkdir -p "$WORK/root/Library/CloudOrc/Backup" "$WORK/scripts"
cp -R "$TREE/." "$WORK/root/Library/CloudOrc/Backup/"
cat > "$WORK/scripts/postinstall" <<'POST'
#!/bin/bash
exec /Library/CloudOrc/Backup/share/install.sh
POST
chmod 0755 "$WORK/scripts/postinstall"

PKG="$ROOT/dist/CloudOrcBackup-$VERSION-$RID.pkg"
pkgbuild --root "$WORK/root" --scripts "$WORK/scripts" --identifier com.cloudorc.backup.agent \
  --version "$VERSION" --install-location / "$WORK/component.pkg"

if [[ -n "${APPLE_INSTALLER_IDENTITY:-}" ]]; then
  productbuild --package "$WORK/component.pkg" --sign "$APPLE_INSTALLER_IDENTITY" "$PKG"
else
  productbuild --package "$WORK/component.pkg" "$PKG"
  echo "WARNING: unsigned package - Gatekeeper will warn. Set APPLE_INSTALLER_IDENTITY to sign it." >&2
fi

if [[ -n "${APPLE_NOTARY_PROFILE:-}" && -n "${APPLE_INSTALLER_IDENTITY:-}" ]]; then
  xcrun notarytool submit "$PKG" --keychain-profile "$APPLE_NOTARY_PROFILE" --wait
  xcrun stapler staple "$PKG"
fi

shasum -a 256 "$PKG" | tee "$PKG.sha256"
