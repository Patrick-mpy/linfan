// SPDX-License-Identifier: GPL-3.0-or-later

using Microsoft.Extensions.Logging;

namespace LinFan.Ipc.Tests;

/// <summary>Sammelt Log-Einträge (Level + gerenderte Nachricht) für Assertions.</summary>
internal sealed class CapturingLogger : ILogger
{
    private readonly List<(LogLevel Level, string Message)> _entries = new();

    /// <summary>A copy: the server logs from its connection tasks while a test reads.</summary>
    public List<(LogLevel Level, string Message)> Entries
    {
        get
        {
            lock (_entries) return _entries.ToList();
        }
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        lock (_entries)
            _entries.Add((logLevel, formatter(state, exception)));
    }
}
