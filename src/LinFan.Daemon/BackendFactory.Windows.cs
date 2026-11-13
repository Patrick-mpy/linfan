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

        // Same condition as the control loop's dry-run (ControlLoopService): a non-elevated instance must neither
        // judge nor touch the crash markers of a live daemon. Fixed machine-wide folder, deliberately independent of
        // LINFAN_CONFIG: every writing LinFan process must see every other one's marker.
        string? markerDirectory = Privileges.IsElevated()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "linfan")
            : null;
        var backend = new WindowsLhmBackend(markerDirectory);
        return (backend, backend);
    }
}
