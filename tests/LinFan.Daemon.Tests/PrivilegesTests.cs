// SPDX-License-Identifier: GPL-3.0-or-later

using LinFan.Daemon;
using Xunit;

namespace LinFan.Daemon.Tests;

/// <summary>
/// Prüft, dass die Privileg-Erkennung plattform-korrekt ist — insbesondere, dass sie auf
/// Windows/macOS nicht hart auf Linux verdrahtet ist (sonst liefe der Daemon dort still im Dry-Run).
/// </summary>
public class PrivilegesTests
{
    [Fact]
    public void ElevationTerm_MatchesPlatform()
    {
        string expected = OperatingSystem.IsWindows() ? "Administrator" : "Root";
        Assert.Equal(expected, Privileges.ElevationTerm);
    }

    [Fact]
    public void IsElevated_RunsOnThisPlatform_WithoutThrowing()
    {
        // Der konkrete Wert hängt davon ab, ob der Test-Runner erhöht läuft — entscheidend ist nur,
        // dass die OS-Verzweigung auf der aktuellen Plattform durchläuft (kein P/Invoke-/CA1416-Fehler).
        Exception? ex = Record.Exception(() => Privileges.IsElevated());
        Assert.Null(ex);
    }
}
