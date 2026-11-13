#!/usr/bin/env bash
#
# Builds  LinFan-Setup-<version>-linux-x64.run  - a self-extracting installer (makeself) that bundles
# the self-contained linux-x64 build and runs packaging/linux/install-bin.sh on extraction. The Linux
# analogue of the Windows one-click .exe; no .NET runtime needed on the target. Needs: dotnet + makeself.
#
#   packaging/linux/build-run.sh <version> [out-dir]
#
set -euo pipefail

VERSION="${1:?usage: build-run.sh <version> [out-dir]}"
OUT="${2:-.}"
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

STAGE="$(mktemp -d)"
trap 'rm -rf "$STAGE"' EXIT

# The release-tarball layout, so install-bin.sh finds bin/ at ../../bin from packaging/linux/.
bash "$SCRIPT_DIR/stage-tarball.sh" "$STAGE" "$VERSION"
# Debug symbols stay in the tarball but not in the installer - they only make the download bigger.
find "$STAGE/bin" -name '*.pdb' -delete

echo "==> makeself self-extracting archive"
mkdir -p "$OUT"
RUN="$OUT/LinFan-Setup-${VERSION}-linux-x64.run"
makeself --nox11 "$STAGE" "$RUN" "LinFan $VERSION installer" ./packaging/linux/install-bin.sh
echo "built: $RUN"
