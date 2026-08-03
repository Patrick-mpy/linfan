// SPDX-License-Identifier: GPL-3.0-or-later

using LinFan.Core.Abstractions;

namespace LinFan.Daemon;

/// <summary>
/// Fallback für Builds auf Betriebssystemen ohne Hardware-Backend (weder Linux noch Windows
/// noch macOS) — hält den Build überall lauffähig. <see cref="DaemonHost"/> fängt die
/// <see cref="PlatformNotSupportedException"/> sauber ab.
/// </summary>
internal static class BackendFactory
{
    public static (ISensorBackend Sensors, IFanController Fans) Create() =>
        throw new PlatformNotSupportedException(
            "Kein Hardware-Backend für dieses Betriebssystem. Unterstützt: Linux, Windows (Phase 2), macOS (Phase 3).");
}
