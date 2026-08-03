// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using System.Text;
using LinFan.Core.Services;
using Microsoft.Extensions.Logging;

namespace LinFan.Daemon;

/// <summary>
/// Schlanker, size-capped Datei-Logger (nativ, keine Dependency) für die Diagnose — vor allem auf Windows,
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
                    _dirReady = true;
                }
                RollIfTooLarge();
                File.AppendAllText(_path, line + Environment.NewLine, Encoding.UTF8);
            }
            catch
            {
                // best-effort: Diagnose darf den Daemon nie reißen.
            }
        }
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

            string message = formatter(state, exception);
            var sb = new StringBuilder()
                .Append(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture))
                .Append(" [").Append(Short(logLevel)).Append("] ")
                .Append(_category).Append(": ").Append(message);
            if (exception is not null)
                sb.Append(Environment.NewLine).Append(exception);
            _provider.Append(sb.ToString());
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
