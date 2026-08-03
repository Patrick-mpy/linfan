// SPDX-License-Identifier: GPL-3.0-or-later

namespace LinFan.Daemon;

/// <summary>
/// Gemeinsames Lauf-Gate der Suspend→treiben→Resume-Koordinatoren (Kalibrierung/Identifikation):
/// genau <b>ein</b> Lauf gleichzeitig, an das Host-(Shutdown-)Token gekoppelt, mit Abbruch-und-Warten
/// für den Shutdown. Kapselt die Sperr-/CTS-/Task-Mechanik an einer Stelle, statt sie (fehleranfällig)
/// in jedem Koordinator zu duplizieren. Koordinator-spezifischer Zustand, der atomar mit dem Beginn/Ende
/// eines Laufs geprüft/gesetzt werden muss (z. B. der Cooldown-Anker der Identifikation), läuft über die
/// <c>canStart</c>-/<c>underLock</c>-Rückrufe unter derselben Sperre.
/// </summary>
internal sealed class RunGate
{
    private readonly CancellationToken _hostToken;
    private readonly object _gate = new();
    private CancellationTokenSource? _cts;
    private Task? _task;

    public RunGate(CancellationToken hostToken) => _hostToken = hostToken;

    /// <summary>
    /// Läuft gerade ein Lauf? Liest den echten Lauf-Zustand (<c>_cts</c>), nicht einen Snapshot-Status —
    /// maßgeblich für die Exklusivität zwischen Kalibrierung und Identifikation.
    /// </summary>
    public bool IsRunning
    {
        get { lock (_gate) return _cts is not null; }
    }

    /// <summary>
    /// Versucht, einen Lauf zu beginnen. Unter der Sperre wird geprüft, dass keiner läuft und — falls
    /// angegeben — <paramref name="canStart"/> zustimmt (z. B. Cooldown). Erfolg ⇒ <paramref name="token"/>
    /// ist das an das Host-Token gekoppelte Lauf-Token; Fehlschlag ⇒ <c>false</c> und <c>default</c>.
    /// </summary>
    public bool TryBegin(out CancellationToken token, Func<bool>? canStart = null)
    {
        lock (_gate)
        {
            if (_cts is not null || (canStart is not null && !canStart()))
            {
                token = default;
                return false;
            }
            _cts = CancellationTokenSource.CreateLinkedTokenSource(_hostToken); // Shutdown bricht den Lauf ab
            token = _cts.Token;
            return true;
        }
    }

    /// <summary>Hinterlegt die laufende Task (direkt nach <see cref="TryBegin"/>), damit <see cref="StopAsync"/> auf sie warten kann.</summary>
    public void Attach(Task task)
    {
        lock (_gate) _task = task;
    }

    /// <summary>
    /// Bricht einen laufenden Lauf ab (ohne zu warten). Läuft keiner, wird <paramref name="whenIdle"/>
    /// noch unter der Sperre ausgeführt (z. B. um einen Abschluss-Status zu quittieren). Liefert, ob ein
    /// Lauf abgebrochen wurde.
    /// </summary>
    public bool Cancel(Action? whenIdle = null)
    {
        lock (_gate)
        {
            if (_cts is null)
            {
                whenIdle?.Invoke();
                return false;
            }
            _cts.Cancel();
            return true;
        }
    }

    /// <summary>Bricht ab und wartet auf das Ende (Daemon-Shutdown). Fehler werden geschluckt — der <c>RunAsync</c>-finally des Aufrufers sichert den Hardware-Zustand ab.</summary>
    public async Task StopAsync()
    {
        Task? task;
        lock (_gate)
        {
            _cts?.Cancel();
            task = _task;
        }
        if (task is not null)
        {
            try { await task.ConfigureAwait(false); }
            catch { /* Abbruch-/Fehlerpfade sind im RunAsync-finally des Aufrufers abgesichert */ }
        }
    }

    /// <summary>
    /// Beendet den Lauf: verwirft die CTS und gibt das Gate frei. <paramref name="underLock"/> läuft noch
    /// unter der Sperre — für Zustand, der atomar mit dem Lauf-Ende gesetzt werden muss (z. B. den
    /// Cooldown-Anker). Für den <c>finally</c>-Pfad gedacht.
    /// </summary>
    public void End(Action? underLock = null)
    {
        lock (_gate)
        {
            _cts?.Dispose();
            _cts = null;
            _task = null;
            underLock?.Invoke();
        }
    }
}
