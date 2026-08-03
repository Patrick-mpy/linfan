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

echo "==> remove files"
sudo rm -f "/etc/systemd/system/$UNIT" /usr/share/applications/linfan.desktop /usr/local/bin/linfan
sudo rm -rf "$PREFIX"
sudo systemctl daemon-reload

echo "Removed. Configuration stays under $CONFIG_DIR (if desired: sudo rm -rf $CONFIG_DIR)."
