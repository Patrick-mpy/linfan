#!/usr/bin/env bash
#
# Installs LinFan from a SELF-CONTAINED build (no .NET SDK/runtime needed on the target). Used by:
#   - the release tarball  (packaging/install.sh delegates here when it finds a bundled bin/),
#   - the .run installer   (makeself runs this after extracting bin/ + packaging/).
# For a source checkout use packaging/install.sh (it builds and installs the framework-dependent build).
#
# Call without sudo — the script elevates the privileged steps itself (or runs as-is when already root):
#   ./packaging/linux/install-bin.sh
#
# Linux/ThinkPad prerequisite: load thinkpad_acpi with fan_control=1, else the daemon runs read-only.
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# Layout (tarball + .run): bin/ at the archive root, packaging/linux/ alongside it.
BIN_DIR="${1:-$SCRIPT_DIR/../../bin}"
PREFIX=/opt/linfan
CONFIG_DIR=/etc/linfan
UNIT=linfan-daemon.service

[ -x "$BIN_DIR/LinFan.Daemon" ] || {
  echo "ERROR: self-contained binaries not found in '$BIN_DIR' (expected LinFan.Daemon)." >&2
  echo "       Are you in a source checkout? Then use packaging/install.sh instead." >&2
  exit 1
}

SUDO=""
[ "$(id -u)" -ne 0 ] && SUDO="sudo"

echo "==> stop a possibly running service first (safe upgrade — ramps fans back to hardware auto)"
$SUDO systemctl stop "$UNIT" 2>/dev/null || true

# Same hazard for the GUI: it memory-maps its binaries from $PREFIX, so replacing them under a
# running instance makes it segfault mid-flight (apport dialog). Close it; relaunched/announced
# after the install. Matches both the self-contained apphost and a previous source install.
GUI_MATCH="$PREFIX/[^ ]*LinFan\.App"
GUI_WAS_RUNNING=
if pgrep -f "$GUI_MATCH" >/dev/null; then
  GUI_WAS_RUNNING=1
  echo "==> close the running GUI (it must not run while its files are replaced)"
  $SUDO pkill -f "$GUI_MATCH" || true
  for _ in $(seq 1 25); do pgrep -f "$GUI_MATCH" >/dev/null || break; sleep 0.2; done
  pgrep -f "$GUI_MATCH" >/dev/null && $SUDO pkill -9 -f "$GUI_MATCH" || true
fi

echo "==> install self-contained binaries to $PREFIX"
$SUDO install -d "$PREFIX"
# --remove-destination unlinks each target before writing (dpkg-style): a plain cp truncates the
# old inode in place and corrupts the mapped image of anything still running from it.
$SUDO cp -rT --remove-destination "$BIN_DIR" "$PREFIX"
# cp folgt der umask (bei Root oft 0077) → die Binaries würden 0700 root-only, und die unprivilegierte
# GUI scheiterte mit "Permission denied" beim App-Start. Explizit welt-les-/ausführbar machen (dpkg
# macht das beim .deb automatisch; hier müssen wir es selbst tun).
$SUDO chmod -R a+rX "$PREFIX"
$SUDO install -m0755 "$SCRIPT_DIR/linfan-gui" "$PREFIX/linfan-gui"
$SUDO ln -sf "$PREFIX/linfan-gui" /usr/local/bin/linfan   # terminal command: just 'linfan'
# Ship the uninstaller so a .run/tarball install can be removed without the repo checkout.
[ -f "$SCRIPT_DIR/../uninstall.sh" ] && $SUDO install -m0755 "$SCRIPT_DIR/../uninstall.sh" "$PREFIX/uninstall.sh" || true

echo "==> systemd unit (self-contained) + app-menu entry"
$SUDO install -m0644 "$SCRIPT_DIR/linfan-daemon.service" "/etc/systemd/system/$UNIT"
$SUDO install -m0644 "$SCRIPT_DIR/../linfan.desktop" /usr/share/applications/linfan.desktop
$SUDO install -D -m0644 "$SCRIPT_DIR/../linfan.svg" /usr/share/icons/hicolor/scalable/apps/linfan.svg
# Refresh the desktop caches, otherwise some environments keep showing the previous icon until re-login.
# Best-effort: the tools are missing on minimal systems, and a stale cache is cosmetic.
$SUDO gtk-update-icon-cache -qtf /usr/share/icons/hicolor 2>/dev/null || true
$SUDO update-desktop-database /usr/share/applications 2>/dev/null || true
$SUDO systemctl daemon-reload

echo "==> configuration ($CONFIG_DIR)"
$SUDO install -d "$CONFIG_DIR"
if ! $SUDO test -f "$CONFIG_DIR/config.json"; then
  for src in /root/.config/linfan/config.json "$HOME/.config/linfan/config.json"; do
    if $SUDO test -f "$src"; then
      echo "    adopting existing configuration: $src"
      $SUDO cp "$src" "$CONFIG_DIR/config.json"
      break
    fi
  done
fi

echo "==> IPC access group 'linfan' (restricts the daemon socket to its members)"
RUN_USER="${SUDO_USER:-$(id -un)}"
$SUDO groupadd -f --system linfan
NEED_RELOGIN=
if [ "$RUN_USER" != "root" ] && ! id -nG "$RUN_USER" | tr ' ' '\n' | grep -qx linfan; then
  echo "    adding $RUN_USER to group 'linfan'"
  $SUDO usermod -aG linfan "$RUN_USER"
  NEED_RELOGIN=1
fi

echo "==> enable & (re)start service"
$SUDO systemctl enable "$UNIT"
$SUDO systemctl restart "$UNIT"
sleep 1
$SUDO systemctl --no-pager --full status "$UNIT" || true

if [ -n "$GUI_WAS_RUNNING" ]; then
  # When the whole script runs as root (sudo ./linfan.run) the GUI must not be relaunched from
  # here — it would run privileged and without the desktop session's display environment.
  if [ "$(id -u)" -eq 0 ]; then
    echo "NOTE: the GUI was closed for the upgrade — start it again via 'linfan' or the app menu."
  else
    echo "==> restart the GUI"
    setsid "$PREFIX/linfan-gui" >/dev/null 2>&1 </dev/null &
  fi
fi

cat <<EOF

Done.
  GUI:     'linfan' in the terminal, or 'LinFan' in the app menu
  Logs:    journalctl -u $UNIT -f
  Config:  $CONFIG_DIR/config.json   (daemon is the sole writer; the GUI saves via IPC)
  Remove:  sudo $PREFIX/uninstall.sh
EOF

if [ -n "$NEED_RELOGIN" ]; then
  echo "NOTE: $RUN_USER was added to the 'linfan' group — log out/in (or 'newgrp linfan') so the GUI can reach the socket without sudo."
fi
