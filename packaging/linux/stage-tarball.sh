#!/usr/bin/env bash
#
# Stages the Linux release layout - the one place that decides what the tarball (and the .run built from the same
# tree) carries. Self-contained single-file linux-x64 build of Daemon + App in bin/, packaging/ with the app icon
# next to the desktop entry, README, LICENSE and CHANGELOG at the root. install-bin.sh expects exactly this.
# Needs: dotnet SDK.
#
#   packaging/linux/stage-tarball.sh <stage-dir> [version]
#
# Without a version the build keeps the one from Directory.Build.props (internal dev builds).
#
set -euo pipefail

STAGE="${1:?usage: stage-tarball.sh <stage-dir> [version]}"
VERSION="${2:-}"
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO="$(cd "$SCRIPT_DIR/../.." && pwd)"

PUBLISH_ARGS=(-c Release -r linux-x64 --self-contained -p:PublishSingleFile=true -o "$STAGE/bin")
if [ -n "$VERSION" ]; then
  PUBLISH_ARGS+=(-p:Version="$VERSION")
fi

echo "==> publish self-contained (Daemon + App) into $STAGE/bin"
mkdir -p "$STAGE"
dotnet publish "$REPO/src/LinFan.Daemon" "${PUBLISH_ARGS[@]}"
dotnet publish "$REPO/src/LinFan.App" "${PUBLISH_ARGS[@]}"

echo "==> stage packaging/, README, LICENSE, CHANGELOG"
cp -r "$REPO/packaging" "$REPO/README.md" "$REPO/LICENSE" "$REPO/CHANGELOG.md" "$STAGE/"
# The archive has no src/ tree - the app icon goes next to the desktop entry, where install-bin.sh looks for it.
cp "$REPO/src/LinFan.App/Assets/linfan-icon.svg" "$STAGE/packaging/linfan.svg"
chmod +x "$STAGE/packaging/linux/install-bin.sh"
