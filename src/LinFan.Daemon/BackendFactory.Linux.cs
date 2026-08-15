// SPDX-License-Identifier: GPL-3.0-or-later

using LinFan.Core.Abstractions;
using LinFan.Hardware.Linux;

namespace LinFan.Daemon;

/// <summary>
/// Backend-Auswahl für <b>Linux</b>-Builds (sysfs/hwmon). Pro Ziel-OS wird genau eine
/// <c>BackendFactory.*.cs</c> kompiliert und nur das passende <c>LinFan.Hardware.*</c>-Projekt
/// referenziert (siehe <c>LinFan.Daemon.csproj</c>) - so zieht der Linux-Build nie die
/// Windows-only-NuGet <c>LibreHardwareMonitorLib</c>.
/// </summary>
internal static class BackendFactory
{
    public static (ISensorBackend Sensors, IFanController Fans) Create()
    {
        if (!OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("Dieser Build wurde für Linux erzeugt.");

        // Eine Instanz erfüllt beide Rollen (gemeinsamer hwmon-Scan).
        var backend = new LinuxHwmonBackend();
        return (backend, backend);
    }
}
