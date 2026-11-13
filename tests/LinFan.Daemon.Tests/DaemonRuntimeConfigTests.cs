// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.Json;

namespace LinFan.Daemon.Tests;

/// <summary>
/// Regression for security audit run-2 #4: with .NET's flock-based FileShare emulation active, any local
/// user could block the root daemon's sysfs writes (including the fail-safe RestoreDefaults), hide a
/// temperature from the watchdog, or stop the daemon from starting by holding an advisory lock on a
/// world-readable file. The switch is a runtime option, so it is checked where it ships: the daemon's
/// runtimeconfig.json (copied next to the tests because they reference the daemon executable).
/// </summary>
public class DaemonRuntimeConfigTests
{
    [Fact]
    public void DaemonRuntimeConfig_DisablesFileLocking()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "LinFan.Daemon.runtimeconfig.json");
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));

        JsonElement properties = doc.RootElement.GetProperty("runtimeOptions").GetProperty("configProperties");

        Assert.True(properties.GetProperty("System.IO.DisableFileLocking").GetBoolean());
    }
}
