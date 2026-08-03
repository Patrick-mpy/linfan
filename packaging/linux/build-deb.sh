#!/usr/bin/env bash
#
# Builds  linfan_<version>_amd64.deb  from a self-contained linux-x64 publish (no .NET runtime
# required on the target). Needs: dotnet SDK + dpkg-deb (both present on the CI Debian image).
#
#   packaging/linux/build-deb.sh <version> [out-dir]
#
set -euo pipefail

VERSION="${1:?usage: build-deb.sh <version> [out-dir]}"
OUT="${2:-.}"
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO="$(cd "$SCRIPT_DIR/../.." && pwd)"

STAGE="$(mktemp -d)"
trap 'rm -rf "$STAGE"' EXIT
PKG="$STAGE/pkg"

echo "==> publish self-contained (Daemon + App) into /opt/linfan"
mkdir -p "$PKG/opt/linfan"
dotnet publish "$REPO/src/LinFan.Daemon" -c Release -r linux-x64 --self-contained \
  -p:Version="$VERSION" -p:PublishSingleFile=true -o "$PKG/opt/linfan"
dotnet publish "$REPO/src/LinFan.App" -c Release -r linux-x64 --self-contained \
  -p:Version="$VERSION" -p:PublishSingleFile=true -o "$PKG/opt/linfan"
# Native Debug-Symbole (falls vorhanden) raus — halten das Paket schlank.
find "$PKG/opt/linfan" -name '*.pdb' -delete

echo "==> assets (self-contained unit, GUI launcher, desktop entry, /usr/bin/linfan)"
install -m0755 "$SCRIPT_DIR/linfan-gui" "$PKG/opt/linfan/linfan-gui"
install -D -m0644 "$SCRIPT_DIR/linfan-daemon.service" "$PKG/lib/systemd/system/linfan-daemon.service"
install -D -m0644 "$REPO/packaging/linfan.desktop" "$PKG/usr/share/applications/linfan.desktop"
install -d "$PKG/usr/bin"
ln -sf /opt/linfan/linfan-gui "$PKG/usr/bin/linfan"

echo "==> DEBIAN control + maintainer scripts"
install -d "$PKG/DEBIAN"
sed "s/@VERSION@/$VERSION/" "$SCRIPT_DIR/debian/control" > "$PKG/DEBIAN/control"
for s in postinst prerm postrm; do
  install -m0755 "$SCRIPT_DIR/debian/$s" "$PKG/DEBIAN/$s"
done

echo "==> dpkg-deb build"
mkdir -p "$OUT"
DEB="$OUT/linfan_${VERSION}_amd64.deb"
dpkg-deb --root-owner-group --build "$PKG" "$DEB"
echo "built: $DEB"
