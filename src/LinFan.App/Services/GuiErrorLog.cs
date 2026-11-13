// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using LinFan.Core.Services;

namespace LinFan.App.Services;

/// <summary>
/// The GUI's error log: errors the GUI caught - an exception nobody handled, a snapshot it could not read - as plain
/// lines in <c>&lt;ApplicationData&gt;/linfan/logs/gui.log</c>, next to <c>ui.json</c>. Errors only, nothing per tick.
/// Native like the daemon's file log: capped at <see cref="DefaultMaxBytes"/> plus one rolled file (<c>gui.log.1</c>),
/// owner-only on Unix, never written through a link, foreign text escaped onto its record's line
/// (<see cref="UntrustedText.Escape"/>).
/// <para>
/// The same error within <see cref="RepeatWindow"/> is counted, not written again, and at most
/// <see cref="MaxRecordsPerWindow"/> records go out per window: a failure on every tick must neither fill the log nor
/// roll its first, most telling record away. Best effort - a write that fails is dropped, the log never raises an error
/// of its own.
/// </para>
/// </summary>
public sealed class GuiErrorLog
{
    public const long DefaultMaxBytes = 1024 * 1024;
    public const int MaxRecordsPerWindow = 20;
    public static readonly TimeSpan RepeatWindow = TimeSpan.FromMinutes(1);

    private const string ContinuationIndent = "    ";
    private const int MaxTrackedErrors = 256;
    private const UnixFileMode OwnerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    private static readonly Lazy<GuiErrorLog> LazyDefault = new(() => new GuiErrorLog(DefaultPath()));

    private readonly string _rolled;
    private readonly long _maxBytes;
    private readonly TimeProvider _clock;
    private readonly object _gate = new();
    private readonly Dictionary<string, Repeat> _repeats = new(StringComparer.Ordinal);
    private long? _windowStart; // monotonic timestamps: a clock change must not reopen or stall the windows
    private int _windowCount;
    private int _heldBack; // records the budget kept out since the last one written
    private bool _fileReady; // folder created and an older file restricted, once per process
    private bool _sessionNoted; // the current file starts with this process' header line

    /// <param name="clock">Time source of the repeat window and the timestamps (tests advance it by hand).</param>
    public GuiErrorLog(string filePath, TimeProvider? clock = null, long maxBytes = DefaultMaxBytes)
    {
        FilePath = filePath;
        _rolled = filePath + ".1";
        _clock = clock ?? TimeProvider.System;
        _maxBytes = maxBytes;
    }

    /// <summary>This user's GUI log (<see cref="DefaultPath"/>); nothing is created before the first error.</summary>
    public static GuiErrorLog Default => LazyDefault.Value;

    public string FilePath { get; }

    /// <summary>
    /// Beside the UI settings (<see cref="UiSettingsStore.DefaultPath"/>): the GUI runs as the user, not as the service.
    /// </summary>
    public static string DefaultPath() =>
        Path.Combine(Path.GetDirectoryName(UiSettingsStore.DefaultPath())!, "logs", "gui.log");

    /// <summary>
    /// Records an error. Returns whether it is new - neither a repeat within <see cref="RepeatWindow"/> nor held back by
    /// the budget - so a caller tells the user about new errors only. A write that fails still counts as new.
    /// </summary>
    public bool Error(string source, string message, Exception? exception = null)
    {
        lock (_gate)
        {
            long now = _clock.GetTimestamp();
            string key = string.Join("\u001F", source, message, exception?.GetType().FullName, exception?.Message);
            _repeats.TryGetValue(key, out Repeat? repeat);
            if (repeat is not null && _clock.GetElapsedTime(repeat.WrittenAt, now) < RepeatWindow)
            {
                repeat.Suppressed++;
                return false;
            }

            if (_windowStart is not { } start || _clock.GetElapsedTime(start, now) >= RepeatWindow)
            {
                _windowStart = now;
                _windowCount = 0;
            }
            if (_windowCount >= MaxRecordsPerWindow)
            {
                _heldBack++;
                return false;
            }
            _windowCount++;

            // The budget writes at most MaxRecordsPerWindow per window, so dropping the expired entries is enough.
            if (_repeats.Count >= MaxTrackedErrors)
            {
                foreach (string old in _repeats.Where(r => _clock.GetElapsedTime(r.Value.WrittenAt, now) >= RepeatWindow)
                             .Select(r => r.Key).ToList())
                    _repeats.Remove(old);
            }
            _repeats[key] = new Repeat(now);
            Append(Record("ERR", source, message, exception, Notes(repeat?.Suppressed ?? 0)));
            return true;
        }
    }

    /// <summary>
    /// The record of an error that ends the process - written whatever the repeat window and the budget say: it is the
    /// one record that explains why the GUI is gone.
    /// </summary>
    public void Fatal(string source, string message, Exception? exception = null)
    {
        lock (_gate)
            Append(Record("FTL", source, message, exception, Notes(0)));
    }

    private string Notes(int repeats)
    {
        var notes = new StringBuilder();
        if (repeats > 0)
            notes.Append(CultureInfo.InvariantCulture, $" (repeated {repeats} times since its last record)");
        if (_heldBack > 0)
            notes.Append(CultureInfo.InvariantCulture, $" ({_heldBack} further errors not recorded, rate limit)");
        _heldBack = 0;
        return notes.ToString();
    }

    private string Record(string level, string source, string message, Exception? exception, string notes)
    {
        // One record starts one line and nothing else does: exception messages may quote what the daemon sent.
        var sb = new StringBuilder()
            .Append(Now()).Append(" [").Append(level).Append("] ").Append(UntrustedText.Escape(source)).Append(": ")
            .Append(UntrustedText.Escape(message)).Append(notes);
        if (exception is not null)
        {
            foreach (string line in exception.ToString().Split('\n'))
            {
                sb.Append(Environment.NewLine).Append(ContinuationIndent)
                  .Append(UntrustedText.Escape(line.TrimEnd('\r')));
            }
        }
        return sb.ToString();
    }

    private string Now() => _clock.GetLocalNow().ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);

    private void Append(string record)
    {
        try
        {
            if (!_fileReady)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                RestrictExistingFiles();
                _fileReady = true;
            }
            if (RollIfTooLarge())
                _sessionNoted = false;
            if (IsLink(FilePath))
                return;

            var text = new StringBuilder();
            if (!_sessionNoted)
            {
                // At the top of every file this process writes, so each record can be matched to the build behind it.
                text.Append(Now()).Append(" [INF] LinFan GUI ").Append(UntrustedText.Escape(AppVersion.InformationalRaw))
                    .Append(" on ").Append(UntrustedText.Escape(RuntimeInformation.OSDescription)).Append(Environment.NewLine);
            }
            text.Append(record).Append(Environment.NewLine);
            using var stream = new FileStream(FilePath, OwnerOnlyAppend());
            stream.Write(Encoding.UTF8.GetBytes(text.ToString()));
            _sessionNoted = true;
        }
        catch
        {
            // best effort: a log that cannot be written must not become the next error
        }
    }

    /// <summary>Stack traces and what the daemon sent are nobody else's business - like the daemon's log.</summary>
    private static FileStreamOptions OwnerOnlyAppend()
    {
        var options = new FileStreamOptions { Mode = FileMode.Append, Access = FileAccess.Write, Share = FileShare.Read };
        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = OwnerOnly;
        return options;
    }

    /// <summary>
    /// A file from before - an older build, or a GUI started through sudo with this user's HOME and a wider umask - keeps
    /// its mode until it rolls over: narrow it once.
    /// </summary>
    private void RestrictExistingFiles()
    {
        if (OperatingSystem.IsWindows())
            return;
        foreach (string file in new[] { FilePath, _rolled })
        {
            try
            {
                if (File.Exists(file) && !IsLink(file) && File.GetUnixFileMode(file) != OwnerOnly)
                    File.SetUnixFileMode(file, OwnerOnly);
            }
            catch
            {
                // best effort, like every other step here
            }
        }
    }

    /// <summary>
    /// A link where the log should be is never followed: a GUI started through sudo with this user's HOME writes into a
    /// folder the user owns, and a planted link would let it append to any file. The log then stays silent instead.
    /// </summary>
    private static bool IsLink(string path)
    {
        try { return new FileInfo(path).LinkTarget is not null; }
        catch { return true; } // cannot tell - do not risk it
    }

    /// <summary>Moves a full log to <c>.1</c> (a link there is replaced, not followed). Returns whether it did.</summary>
    private bool RollIfTooLarge()
    {
        try
        {
            var file = new FileInfo(FilePath);
            if (!file.Exists || file.Length < _maxBytes)
                return false;
            File.Move(FilePath, _rolled, overwrite: true);
            return true;
        }
        catch
        {
            return false; // keep writing to the current file rather than lose the record
        }
    }

    private sealed class Repeat(long writtenAt)
    {
        public long WrittenAt { get; } = writtenAt;
        public int Suppressed { get; set; }
    }
}
