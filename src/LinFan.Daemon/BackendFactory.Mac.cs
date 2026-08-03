// SPDX-License-Identifier: GPL-3.0-or-later

using LinFan.Core.Abstractions;
using LinFan.Hardware.Mac;

namespace LinFan.Daemon;

/// <summary>
/// Backend-Auswahl für <b>macOS</b>-Builds (IOKit/SMC). Wird nur in macOS-Builds kompiliert
/// (siehe <c>LinFan.Daemon.csproj</c>); dort ist auch <c>LinFan.Hardware.Mac</c> referenziert.
/// Implementierung des Backends = Phase 3 (auf Apple Silicon oft nur Best-Effort/read-only).
/// </summary>
internal static class BackendFactory
{
    public static (ISensorBackend Sensors, IFanController Fans) Create()
    {
        if (!OperatingSystem.IsMacOS())
            throw new PlatformNotSupportedException("Dieser Build wurde für macOS erzeugt.");

        var backend = new MacSmcBackend();
        return (backend, backend);
    }
}
