# Changelog

Alle nennenswerten Änderungen an LinFan, je Release zusammengefasst. Format angelehnt an
[Keep a Changelog](https://keepachangelog.com/de/1.1.0/); versioniert nach SemVer,
Quelle der Wahrheit ist der Git-Tag `vX.Y.Z`.

Offene Punkte werden als GitHub-Issues geführt.

## [0.1.0] - 2026-08-03

Erstes öffentliches Release — Lüftersteuerung für **Linux** (im Fokus) und **Windows**,
**macOS** als Best-Effort. Lizenz: GPL-3.0-or-later.

### Hinzugefügt

- **Daemon + GUI, sauber getrennt**: ein privilegierter Daemon (systemd / Windows-Dienst) regelt
  die Lüfter nach Temperatur-Kurven (Interpolation, Hysterese, Clamping); die Avalonia-GUI läuft
  ohne Root und spricht ihn über lokales IPC an (Unix-Socket bzw. Named Pipe).
- **Fail-Safe eingebaut**: ein Watchdog führt bei Übertemperatur, Fehlern oder Beenden auf
  Volllast bzw. zurück in die Hardware-Automatik.
- **Onboarding mit Kalibrierung**: pro Lüfter werden PWM→RPM-Kennlinie und Anlaufpunkt gemessen;
  eine automatische Tacho-Kopplung ordnet jedem Lüfter seinen Drehzahl-Sensor empirisch zu.
- **GUI**: Live-Dashboard (Temperaturen, Drehzahlen), Kurven-Editor mit Profilen,
  Gehäuse-Ansicht mit Lüfter-Positionen, Tray-Betrieb, Backup/Restore der Konfiguration,
  Oberfläche auf Deutsch und Englisch.
- **Hardware-Backends**: Linux `sysfs`/hwmon, Windows LibreHardwareMonitor, macOS IOKit/SMC
  (auf Apple Silicon validiert; Lesen ohne Root, Steuern als Root — Dienst-Integration folgt).
- **Update-Hinweis**: die GUI meldet neuere GitHub-Releases (nur Hinweis, kein Auto-Download,
  abschaltbar).
- **Pakete**: Linux-Tarball plus `.deb`-/`.run`-Installer, Windows-ZIP und
  Ein-Klick-Installer (`.exe`).
