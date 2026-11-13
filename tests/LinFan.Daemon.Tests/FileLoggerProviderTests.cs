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
    public void ARecordPastTheCap_IsCut_SoTheFileCannotOvershootItsBoundByMore()
    {
        string dir = TempDir();
        try
        {
            string path = Path.Combine(dir, "linfan.log");
            using var provider = new FileLoggerProvider(path);
            ILogger log = provider.CreateLogger("Cat");

            log.LogInformation("Kurven: {Toggles}", new string('x', 10 * FileLoggerProvider.MaxRecordChars));

            string record = Assert.Single(File.ReadAllLines(path));
            Assert.True(record.Length < FileLoggerProvider.MaxRecordChars + 64, $"record has {record.Length} chars");
            Assert.EndsWith("Zeichen gekürzt]", record);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void ALongMessage_LeavesTheExceptionItsLines()
    {
        string dir = TempDir();
        try
        {
            string path = Path.Combine(dir, "linfan.log");
            using var provider = new FileLoggerProvider(path);
            ILogger log = provider.CreateLogger("Cat");

            log.LogWarning(new InvalidOperationException("the cause"), "Zuordnung: {Ids}", new string('x', 10 * FileLoggerProvider.MaxRecordChars));

            Assert.Contains("the cause", File.ReadAllText(path));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Cap_NeverSplitsASurrogatePair()
    {
        string text = new string('x', 9) + "\U0001F600" + "tail"; // the emoji's high surrogate is character 10

        string capped = FileLoggerProvider.Cap(text, 10);

        Assert.StartsWith(new string('x', 9) + " …", capped);
        Assert.Contains("[6 Zeichen gekürzt]", capped);
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

    [Fact]
    public void PeerText_CannotForgeARecord_OrReachTheFileRaw()
    {
        string dir = TempDir();
        try
        {
            string path = Path.Combine(dir, "linfan.log");
            using var provider = new FileLoggerProvider(path);
            ILogger log = provider.CreateLogger("Cat");

            log.LogWarning("Manuell aus: {Fan}", "x\n2026-01-01 00:00:00.000 [CRT] Forged: owned\0<0>\u001b]0;t\u0007");
            log.LogError(new InvalidOperationException("Unbekannter Lüfter: y\n2026-01-01 00:00:00.000 [CRT] Forged: again"),
                "Tick fehlgeschlagen");

            string[] lines = File.ReadAllLines(path);
            // Only the two real records start at column 0; every exception line is indented.
            Assert.Equal(2, lines.Count(l => l.Length > 0 && !char.IsWhiteSpace(l[0])));
            Assert.DoesNotContain(lines, l => l.StartsWith("2026-01-01", StringComparison.Ordinal));
            string text = File.ReadAllText(path);
            Assert.DoesNotContain('\0', text);
            Assert.DoesNotContain('\u001b', text);
            Assert.Contains("\\u000A2026-01-01", text); // the peer's line feed, visible and inert
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void LogFile_IsOwnerOnly()
    {
        if (OperatingSystem.IsWindows())
            return;
        string dir = TempDir();
        try
        {
            string path = Path.Combine(dir, "linfan.log");
            File.WriteAllText(path, "from before\n");
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
            using var provider = new FileLoggerProvider(path, maxBytes: 200);
            ILogger log = provider.CreateLogger("Cat");

            for (int i = 0; i < 20; i++)
                log.LogInformation("Zeile {N} mit etwas Text", i); // rolls over at least once

            const UnixFileMode ownerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            Assert.Equal(ownerOnly, File.GetUnixFileMode(path));
            Assert.Equal(ownerOnly, File.GetUnixFileMode(path + ".1"));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void ALinkPlantedAsTheLogFile_IsNeitherFollowedNorChanged()
    {
        if (OperatingSystem.IsWindows())
            return;
        string dir = TempDir();
        try
        {
            string victim = Path.Combine(dir, "victim");
            File.WriteAllText(victim, "untouched");
            const UnixFileMode before = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.OtherRead;
            File.SetUnixFileMode(victim, before);
            string path = Path.Combine(dir, "linfan.log");
            File.CreateSymbolicLink(path, victim);
            using var provider = new FileLoggerProvider(path);

            provider.CreateLogger("Cat").LogInformation("darf nicht im Linkziel landen");

            Assert.Equal("untouched", File.ReadAllText(victim));
            Assert.Equal(before, File.GetUnixFileMode(victim));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
