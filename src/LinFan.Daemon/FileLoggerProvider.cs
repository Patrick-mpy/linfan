// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using System.Text;
using LinFan.Core.Services;
using Microsoft.Extensions.Logging;

namespace LinFan.Daemon;

/// <summary>
/// Schlanker, size-capped Datei-Logger (nativ, keine Dependency) für die Diagnose - vor allem auf Windows,
/// wo der Dienst nur ins Event-Log schreibt und keine Konsole hat. Schreibt Klartext-Zeilen unter
/// <c>&lt;configdir&gt;/logs/linfan.log</c> und rolliert bei Überschreiten der Größe auf <c>linfan.log.1</c>.
/// <para>
/// Best-effort: ein Schreib-/Rotations-Fehler reißt den Daemon NIE (Diagnose ist nie kritischer als der
/// Steuerpfad → <c>linfan-failsafe</c>). Ein <see cref="_gate"/> serialisiert die Schreibzugriffe.
/// </para>
/// </summary>
internal sealed class FileLoggerProvider : ILoggerProvider
{
    private const long DefaultMaxBytes = 1024 * 1024; // 1 MB je Datei

    /// <summary>
    /// Longest record written. The size check runs before each append, so this is what the file can overshoot its
    /// bound by - a record joins strings IPC peers sent, and without a cap one record could exceed the bound many times
    /// over. Far above a real one, stack traces included; the message alone gets half, so a long one leaves the
    /// exception's lines room.
    /// </summary>
    internal const int MaxRecordChars = 16 * 1024;

    /// <summary>Cuts <paramref name="text"/> to <paramref name="max"/> characters (never inside a surrogate pair) and says so.</summary>
    internal static string Cap(string text, int max)
    {
        if (text.Length <= max)
            return text;

        int cut = max;
        if (char.IsHighSurrogate(text[cut - 1]))
            cut--;
        return text[..cut] + $" … [{text.Length - cut} Zeichen gekürzt]";
    }

    private readonly string _path;
    private readonly string _rolled; // <path>.1
    private readonly long _maxBytes;
    private readonly LogLevel _minLevel;
    private readonly object _gate = new();
    private bool _dirReady;

    public FileLoggerProvider(string path, LogLevel minLevel = LogLevel.Information, long maxBytes = DefaultMaxBytes)
    {
        _path = path;
        _rolled = path + ".1";
        _minLevel = minLevel;
        _maxBytes = maxBytes;
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    public void Dispose() { }

    /// <summary>
    /// Ermittelt den Log-Pfad: <c>LINFAN_LOG</c> gewinnt (leer/<c>off</c>/<c>0</c>/<c>none</c> ⇒ deaktiviert,
    /// sonst expliziter Pfad); sonst <c>&lt;configdir&gt;/logs/linfan.log</c> neben der Konfiguration.
    /// <c>null</c> ⇒ Datei-Logging aus.
    /// </summary>
    public static string? ResolveLogPath()
    {
        string? env = Environment.GetEnvironmentVariable("LINFAN_LOG");
        if (env is not null)
        {
            string trimmed = env.Trim();
            if (trimmed.Length == 0 || trimmed is "0"
                || trimmed.Equals("off", StringComparison.OrdinalIgnoreCase)
                || trimmed.Equals("none", StringComparison.OrdinalIgnoreCase))
                return null;
            return trimmed;
        }

        try
        {
            string dir = Path.GetDirectoryName(JsonConfigStore.DefaultPath())!;
            return Path.Combine(dir, "logs", "linfan.log");
        }
        catch
        {
            return null; // Pfad nicht auflösbar → kein Datei-Logging (nicht kritisch)
        }
    }

    private bool IsEnabled(LogLevel level) => level != LogLevel.None && level >= _minLevel;

    private void Append(string line)
    {
        lock (_gate)
        {
            try
            {
                if (!_dirReady)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                    RestrictExistingFile();
                    _dirReady = true;
                }
                RollIfTooLarge();
                if (IsLink(_path))
                    return;
                byte[] bytes = Encoding.UTF8.GetBytes(line + Environment.NewLine);
                using var stream = new FileStream(_path, OwnerOnly(FileMode.Append));
                stream.Write(bytes);
            }
            catch
            {
                // best-effort: Diagnose darf den Daemon nie reißen.
            }
        }
    }

    /// <summary>
    /// The log holds what IPC peers sent and is no one else's business: a new file is created owner-only (the root
    /// daemon's under /etc/linfan would otherwise be world-readable with the default umask).
    /// </summary>
    private static FileStreamOptions OwnerOnly(FileMode mode)
    {
        var options = new FileStreamOptions { Mode = mode, Access = FileAccess.Write, Share = FileShare.Read };
        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        return options;
    }

    /// <summary>A log file from before the owner-only mode keeps its old mode until it rolls over - fix it once.</summary>
    private void RestrictExistingFile()
    {
        if (OperatingSystem.IsWindows())
            return;
        foreach (string file in new[] { _path, _rolled })
        {
            try
            {
                if (File.Exists(file) && !IsLink(file))
                    File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
            catch
            {
                // best-effort, like every other step here
            }
        }
    }

    /// <summary>
    /// A link where the log should be is never followed: a root daemon run with a user's HOME (the dev setup) writes
    /// into a folder that user owns, and a planted link would let it change and fill any file. The log then stays
    /// silent rather than write through it.
    /// </summary>
    private static bool IsLink(string path)
    {
        try { return new FileInfo(path).LinkTarget is not null; }
        catch { return true; } // cannot tell - do not risk it
    }

    /// <summary>Größe erreicht ⇒ aktuelle Datei nach <c>.1</c> rollen (die vorige <c>.1</c> entfällt). Best-effort.</summary>
    private void RollIfTooLarge()
    {
        try
        {
            var fi = new FileInfo(_path);
            if (!fi.Exists || fi.Length < _maxBytes)
                return;
            if (File.Exists(_rolled))
                File.Delete(_rolled);
            File.Move(_path, _rolled);
        }
        catch
        {
            // Rotation best-effort; im Zweifel weiterschreiben.
        }
    }

    private sealed class FileLogger : ILogger
    {
        private const string ContinuationIndent = "    ";

        private readonly FileLoggerProvider _provider;
        private readonly string _category;

        public FileLogger(FileLoggerProvider provider, string category)
        {
            _provider = provider;
            _category = category;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => _provider.IsEnabled(logLevel);

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
                return;

            // One record starts one line and nothing else does: the message is escaped onto that line, and every line
            // of an exception (its message may quote a peer's string) is indented - an IPC peer cannot forge a record.
            string message = Cap(UntrustedText.Escape(formatter(state, exception)), MaxRecordChars / 2);
            var sb = new StringBuilder()
                .Append(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture))
                .Append(" [").Append(Short(logLevel)).Append("] ")
                .Append(_category).Append(": ").Append(message);
            if (exception is not null)
            {
                foreach (string line in exception.ToString().Split('\n'))
                    sb.Append(Environment.NewLine).Append(ContinuationIndent).Append(UntrustedText.Escape(line.TrimEnd('\r')));
            }
            _provider.Append(Cap(sb.ToString(), MaxRecordChars));
        }

        private static string Short(LogLevel level) => level switch
        {
            LogLevel.Trace => "TRC",
            LogLevel.Debug => "DBG",
            LogLevel.Information => "INF",
            LogLevel.Warning => "WRN",
            LogLevel.Error => "ERR",
            LogLevel.Critical => "CRT",
            _ => "???",
        };
    }
}
