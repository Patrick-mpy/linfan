// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;

namespace LinFan.Daemon;

/// <summary>
/// Rechte-Erkennung. PWM-Schreiben braucht überall erhöhte Rechte (Linux/macOS: Root/effektive
/// UID 0, Windows: Mitglied der Administratoren-Gruppe mit erhöhtem Token). Ohne sie läuft der
/// Regel-Loop im Dry-Run. Ohne plattform-korrekte Erkennung liefe der Daemon auf Windows/macOS
/// sonst stillschweigend im Dry-Run, obwohl er steuern dürfte.
/// </summary>
internal static class Privileges
{
    /// <summary>True, wenn der Prozess Hardware schreiben darf (Root bzw. Administrator).</summary>
    public static bool IsElevated()
    {
        if (OperatingSystem.IsWindows())
            return IsWindowsAdministrator();

        // Linux & macOS: effektive UID 0 = Root.
        return geteuid() == 0;
    }

    /// <summary>OS-passende Bezeichnung der nötigen Rechte (für Meldungen): „Root" bzw. „Administrator".</summary>
    public static string ElevationTerm => OperatingSystem.IsWindows() ? "Administrator" : "Root";

    [SupportedOSPlatform("windows")]
    private static bool IsWindowsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    [DllImport("libc")]
    private static extern uint geteuid();
}
