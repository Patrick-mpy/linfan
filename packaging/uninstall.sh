#!/usr/bin/env bash
#
# Removes the LinFan service + GUI launcher again. Call without sudo (elevates itself):
#
#   ./packaging/uninstall.sh
#
# The configuration under /etc/linfan is deliberately kept (delete manually if desired).
set -euo pipefail

PREFIX=/opt/linfan
CONFIG_DIR=/etc/linfan
UNIT=linfan-daemon.service

echo "==> stop & disable service"
sudo systemctl disable --now "$UNIT" 2>/dev/null || true

# Close the GUI too, otherwise it keeps running from the unlinked files below - with a tray icon and a
# menu entry that no longer exist. Same pattern as install.sh: only instances started from $PREFIX, so
# a dev GUI from a source tree is left alone.
GUI_MATCH="$PREFIX/[^ ]*LinFan\.App"
if pgrep -f "$GUI_MATCH" >/dev/null; then
  echo "==> close the running GUI"
  sudo pkill -f "$GUI_MATCH" || true
  for _ in $(seq 1 25); do pgrep -f "$GUI_MATCH" >/dev/null || break; sleep 0.2; done
  pgrep -f "$GUI_MATCH" >/dev/null && sudo pkill -9 -f "$GUI_MATCH" || true
fi

echo "==> remove files"
sudo rm -f "/etc/systemd/system/$UNIT" /usr/share/applications/linfan.desktop /usr/local/bin/linfan \
  /usr/share/icons/hicolor/scalable/apps/linfan.svg
sudo rm -rf "$PREFIX"
sudo systemctl daemon-reload

echo "Removed. Configuration stays under $CONFIG_DIR (if desired: sudo rm -rf $CONFIG_DIR)."
