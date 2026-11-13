// SPDX-License-Identifier: GPL-3.0-or-later

using LinFan.App.Services;

namespace LinFan.App.Tests;

/// <summary>
/// The GUI's error log: one record per error on its own line (foreign text escaped), owner-only, never through a link,
/// capped with one rolled file, and quiet about repeats - an error on every tick must not fill it. The record of an
/// error that ends the process gets through regardless.
/// </summary>
public sealed class GuiErrorLogTests : IDisposable
{
    private const UnixFileMode OwnerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"linfan-guilog-{Guid.NewGuid():N}");
    private readonly ManualClock _clock = new();

    private string LogPath => Path.Combine(_dir, "logs", "gui.log");

    private GuiErrorLog NewLog(long maxBytes = GuiErrorLog.DefaultMaxBytes) => new(LogPath, _clock, maxBytes);

    private string[] Lines() => File.ReadAllLines(LogPath);

    /// <summary>The lines that start an error record - the indented lines of an exception belong to the record above.</summary>
    private string[] ErrorLines() =>
        Lines().Where(l => !l.StartsWith(' ') && l.Contains(" [ERR] ", StringComparison.Ordinal)).ToArray();

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    private static Exception Thrown(string message)
    {
        try
        {
            throw new InvalidOperationException(message);
        }
        catch (InvalidOperationException ex)
        {
            return ex;
        }
    }

    [Fact]
    public void DefaultPath_LiesBesideTheUiSettings()
    {
        string settingsDir = Path.GetDirectoryName(UiSettingsStore.DefaultPath())!;

        Assert.Equal(Path.Combine(settingsDir, "logs", "gui.log"), GuiErrorLog.DefaultPath());
    }

    [Fact]
    public void NothingIsCreated_BeforeTheFirstError()
    {
        _ = NewLog();

        Assert.False(Directory.Exists(_dir));
    }

    [Fact]
    public void AnError_IsOneRecord_WithItsSource_AndTheIndentedException()
    {
        GuiErrorLog log = NewLog();

        Assert.True(log.Error("Snapshot", "could not apply", Thrown("boom")));

        string[] lines = Lines();
        Assert.Contains(" [INF] LinFan GUI ", lines[0]); // the build that wrote what follows
        string record = Assert.Single(ErrorLines());
        Assert.EndsWith("[ERR] Snapshot: could not apply", record);
        Assert.Contains(lines, l => l.StartsWith("    System.InvalidOperationException: boom", StringComparison.Ordinal));
        Assert.All(lines.Skip(2), l => Assert.StartsWith("    ", l)); // the trace stays inside the record
    }

    [Fact]
    public void ForeignText_CannotStartARecordOfItsOwn()
    {
        GuiErrorLog log = NewLog();

        log.Error("Snapshot", "service 1.0\n2026-01-01 12:00:00.000 [ERR] forged", Thrown("x\r\n[ERR] forged too\u001b[2J"));

        Assert.Single(ErrorLines());
        Assert.DoesNotContain(Lines(), l => l.StartsWith("2026-01-01 12:00:00.000 [ERR] forged", StringComparison.Ordinal));
        Assert.DoesNotContain(Lines(), l => l.Contains('\u001b'));
        Assert.Contains("\\u000A", File.ReadAllText(LogPath)); // escaped, still readable
    }

    [Fact]
    public void TheFile_IsOwnerOnly()
    {
        if (OperatingSystem.IsWindows())
            return; // ACLs there; the profile folder is the user's own

        NewLog().Error("Snapshot", "x");

        Assert.Equal(OwnerOnly, File.GetUnixFileMode(LogPath));
    }

    [Fact]
    public void AnOlderFile_WithAWiderMode_IsNarrowedOnTheFirstWrite()
    {
        if (OperatingSystem.IsWindows())
            return;
        // E.g. left by a GUI started through sudo with a wider umask.
        Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
        File.WriteAllText(LogPath, "old\n");
        File.WriteAllText(LogPath + ".1", "older\n");
        File.SetUnixFileMode(LogPath, OwnerOnly | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        File.SetUnixFileMode(LogPath + ".1", OwnerOnly | UnixFileMode.GroupRead | UnixFileMode.OtherRead);

        NewLog().Error("Snapshot", "x");

        Assert.Equal(OwnerOnly, File.GetUnixFileMode(LogPath));
        Assert.Equal(OwnerOnly, File.GetUnixFileMode(LogPath + ".1"));
    }

    [Fact]
    public void ALinkWhereTheLogShouldBe_IsNotFollowed()
    {
        if (OperatingSystem.IsWindows())
            return; // creating links needs a privilege there
        Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
        string target = Path.Combine(_dir, "someone-elses-file");
        File.WriteAllText(target, "untouched\n");
        File.CreateSymbolicLink(LogPath, target);

        Assert.True(NewLog().Error("Snapshot", "x")); // still new - the user is told all the same

        Assert.Equal("untouched\n", File.ReadAllText(target));
    }

    [Fact]
    public void TheSameError_WithinAMinute_IsCounted_NotWrittenAgain()
    {
        GuiErrorLog log = NewLog();

        Assert.True(log.Error("Snapshot", "could not apply", Thrown("boom")));
        _clock.Advance(TimeSpan.FromSeconds(1));
        Assert.False(log.Error("Snapshot", "could not apply", Thrown("boom")));
        _clock.Advance(TimeSpan.FromSeconds(1));
        Assert.False(log.Error("Snapshot", "could not apply", Thrown("boom")));
        Assert.Single(ErrorLines());

        _clock.Advance(GuiErrorLog.RepeatWindow);
        Assert.True(log.Error("Snapshot", "could not apply", Thrown("boom")));

        string[] records = ErrorLines();
        Assert.Equal(2, records.Length);
        Assert.Contains("(repeated 2 times since its last record)", records[1]);
    }

    [Fact]
    public void AnotherError_IsWritten_EvenRightAfterTheFirst()
    {
        GuiErrorLog log = NewLog();

        Assert.True(log.Error("Snapshot", "could not apply", Thrown("boom")));
        Assert.True(log.Error("Snapshot", "could not apply", Thrown("other")));

        Assert.Equal(2, ErrorLines().Length);
    }

    [Fact]
    public void ManyDifferentErrors_AreCappedPerMinute_AndTheNextRecordSaysHowManyWereLeftOut()
    {
        GuiErrorLog log = NewLog();

        for (int i = 0; i < GuiErrorLog.MaxRecordsPerWindow; i++)
            Assert.True(log.Error("Task", $"error {i}"));
        Assert.False(log.Error("Task", "one too many"));
        Assert.False(log.Error("Task", "and another"));
        Assert.Equal(GuiErrorLog.MaxRecordsPerWindow, ErrorLines().Length);

        _clock.Advance(GuiErrorLog.RepeatWindow);
        Assert.True(log.Error("Task", "later"));

        Assert.Contains("(2 further errors not recorded, rate limit)", ErrorLines()[^1]);
    }

    [Fact]
    public void AFatalError_IsWritten_PastTheBudgetAndTheRepeatWindow()
    {
        GuiErrorLog log = NewLog();
        for (int i = 0; i < GuiErrorLog.MaxRecordsPerWindow; i++)
            log.Error("Task", $"error {i}");
        Assert.False(log.Error("Process", "the end", Thrown("boom"))); // the budget is spent

        log.Fatal("Process", "the end", Thrown("boom"));
        log.Fatal("Process", "the end", Thrown("boom"));

        string[] fatal = Lines().Where(l => l.Contains(" [FTL] Process: the end", StringComparison.Ordinal)).ToArray();
        Assert.Equal(2, fatal.Length);
    }

    [Fact]
    public void AFullLog_RollsOver_IntoOneOlderFile_AndTheNewOneStartsWithTheHeader()
    {
        GuiErrorLog log = NewLog(maxBytes: 200);

        log.Error("Snapshot", new string('a', 300));
        log.Error("Snapshot", "second");

        Assert.True(File.Exists(LogPath + ".1"));
        Assert.Contains(new string('a', 300), File.ReadAllText(LogPath + ".1"));
        string[] lines = Lines();
        Assert.Contains(" [INF] LinFan GUI ", lines[0]); // the new file names the build as well
        Assert.EndsWith("[ERR] Snapshot: second", Assert.Single(ErrorLines()));
        if (!OperatingSystem.IsWindows())
            Assert.Equal(OwnerOnly, File.GetUnixFileMode(LogPath));
    }

    [Fact]
    public void TheHeader_IsWrittenOncePerFile()
    {
        GuiErrorLog log = NewLog();

        log.Error("Snapshot", "first");
        log.Error("Snapshot", "second");

        Assert.Single(Lines(), l => l.Contains(" [INF] LinFan GUI ", StringComparison.Ordinal));
    }

    [Fact]
    public void AnUnwritableLog_IsNoErrorOfItsOwn()
    {
        Directory.CreateDirectory(_dir);
        string blocker = Path.Combine(_dir, "logs"); // a file where the folder should be
        File.WriteAllText(blocker, "");

        bool recorded = NewLog().Error("Snapshot", "x");

        Assert.True(recorded); // new all the same - the user is told about it
    }
}
