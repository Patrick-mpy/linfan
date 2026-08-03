// SPDX-License-Identifier: GPL-3.0-or-later

using LinFan.Core.Abstractions;
using LinFan.Hardware.Windows;

namespace LinFan.Daemon;

/// <summary>
/// Backend-Auswahl für <b>Windows</b>-Builds (LibreHardwareMonitorLib). Wird nur in
/// Windows-Builds kompiliert (siehe <c>LinFan.Daemon.csproj</c>); dort ist auch
/// <c>LinFan.Hardware.Windows</c> referenziert. Implementierung des Backends = Phase 2.
/// </summary>
internal static class BackendFactory
{
    public static (ISensorBackend Sensors, IFanController Fans) Create()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Dieser Build wurde für Windows erzeugt.");

        var backend = new WindowsLhmBackend();
        return (backend, backend);
    }
}
