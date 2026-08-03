#!/usr/bin/env bash
#
# Builds  LinFan-Setup-<version>-linux-x64.run  — a self-extracting installer (makeself) that bundles
# the self-contained linux-x64 build and runs packaging/linux/install-bin.sh on extraction. The Linux
# analogue of the Windows one-click .exe; no .NET runtime needed on the target. Needs: dotnet + makeself.
#
#   packaging/linux/build-run.sh <version> [out-dir]
#
set -euo pipefail

VERSION="${1:?usage: build-run.sh <version> [out-dir]}"
OUT="${2:-.}"
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO="$(cd "$SCRIPT_DIR/../.." && pwd)"

STAGE="$(mktemp -d)"
trap 'rm -rf "$STAGE"' EXIT

# Mirror the release-tarball layout so install-bin.sh finds bin/ at ../../bin from packaging/linux/.
echo "==> publish self-contained (Daemon + App) into bin/"
mkdir -p "$STAGE/bin" "$STAGE/packaging"
dotnet publish "$REPO/src/LinFan.Daemon" -c Release -r linux-x64 --self-contained \
  -p:Version="$VERSION" -p:PublishSingleFile=true -o "$STAGE/bin"
dotnet publish "$REPO/src/LinFan.App" -c Release -r linux-x64 --self-contained \
  -p:Version="$VERSION" -p:PublishSingleFile=true -o "$STAGE/bin"
find "$STAGE/bin" -name '*.pdb' -delete

echo "==> stage packaging/ (installer + assets + uninstaller)"
cp -r "$SCRIPT_DIR" "$STAGE/packaging/linux"
cp "$REPO/packaging/linfan.desktop" "$STAGE/packaging/linfan.desktop"
cp "$REPO/packaging/uninstall.sh" "$STAGE/packaging/uninstall.sh"
chmod +x "$STAGE/packaging/linux/install-bin.sh"

echo "==> makeself self-extracting archive"
mkdir -p "$OUT"
RUN="$OUT/LinFan-Setup-${VERSION}-linux-x64.run"
makeself --nox11 "$STAGE" "$RUN" "LinFan $VERSION installer" ./packaging/linux/install-bin.sh
echo "built: $RUN"
