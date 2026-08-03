// SPDX-License-Identifier: GPL-3.0-or-later

using Microsoft.Extensions.Logging;
using Xunit;

namespace LinFan.Daemon.Tests;

public class FileLoggerProviderTests
{
    private static string TempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "linfan-logtest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void Writes_InformationLine_WithLevelAndCategory()
    {
        string dir = TempDir();
        try
        {
            string path = Path.Combine(dir, "sub", "linfan.log"); // Unterordner wird best-effort angelegt
            using var provider = new FileLoggerProvider(path);
            ILogger log = provider.CreateLogger("MyCategory");

            log.LogInformation("hallo welt");

            string text = File.ReadAllText(path);
            Assert.Contains("hallo welt", text);
            Assert.Contains("[INF]", text);
            Assert.Contains("MyCategory", text);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Debug_IsFiltered_AtDefaultMinLevel()
    {
        string dir = TempDir();
        try
        {
            string path = Path.Combine(dir, "linfan.log");
            using var provider = new FileLoggerProvider(path); // minLevel = Information
            ILogger log = provider.CreateLogger("Cat");

            log.LogDebug("nur debug");
            log.LogInformation("sichtbar");

            string text = File.Exists(path) ? File.ReadAllText(path) : "";
            Assert.DoesNotContain("nur debug", text);
            Assert.Contains("sichtbar", text);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Rotates_WhenExceedingCap()
    {
        string dir = TempDir();
        try
        {
            string path = Path.Combine(dir, "linfan.log");
            using var provider = new FileLoggerProvider(path, maxBytes: 200);
            ILogger log = provider.CreateLogger("Cat");

            for (int i = 0; i < 50; i++)
                log.LogInformation("Zeile mit etwas Text Nummer {N}", i);

            Assert.True(File.Exists(path + ".1"), "rollierte Datei sollte existieren");
            Assert.True(new FileInfo(path).Length < 1000, "aktuelle Datei sollte nach Rotation klein bleiben");
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Write_NeverThrows_OnUnwritablePath()
    {
        string dir = TempDir();
        try
        {
            // 'afile' ist eine Datei, kein Ordner → das Anlegen des Log-Verzeichnisses schlägt fehl.
            string blocker = Path.Combine(dir, "afile");
            File.WriteAllText(blocker, "x");
            using var provider = new FileLoggerProvider(Path.Combine(blocker, "linfan.log"));
            ILogger log = provider.CreateLogger("Cat");

            Exception? ex = Record.Exception(() => log.LogInformation("darf nicht werfen"));
            Assert.Null(ex);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
