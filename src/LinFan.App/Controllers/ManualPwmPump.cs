// SPDX-License-Identifier: GPL-3.0-or-later

namespace LinFan.App.Controllers;

/// <summary>
/// Gedrosselte Coalescing-Pumpe für manuelle PWM-Stellwerte. Der Slider feuert kontinuierlich
/// (~ein Event je PWM-Integer); ungedrosselt flutet ein einziger Zug ~180 IPC-Befehle an den Daemon
/// (Pipe-/Log-Last) — die Hardware ist daemon-seitig ohnehin tick-coalesced. Die Pumpe hält maximal
/// EINEN Send in der Luft und sendet je Intervall nur den jeweils neuesten Stellwert; der Endwert wird
/// garantiert zuletzt gesendet. Läuft auf dem UI-Thread (Avalonia-SynchronizationContext) — kein Lock nötig.
/// <para>
/// Wird von allen manuellen Steuer-Flächen geteilt (Dashboard, Geräte-Tab, Onboarding, Positions-Modal),
/// damit der Slider-Flut-Schutz an genau einer Stelle lebt. Der Send-Callback ist spät bindbar
/// (<see cref="Send"/>), da die Steuerbefehle erst nach dem Erzeugen der Zeile injiziert werden.
/// </para>
/// </summary>
internal sealed class ManualPwmPump
{
    private readonly string _fanId;
    private int _pendingPwm = -1;
    private int _lastSentPwm = -1;
    private bool _sending;
    private bool _active = true; // false → ein laufender Durchlauf sendet nichts Neues mehr (Stop)
    private Task _pumpTask = Task.CompletedTask;

    public ManualPwmPump(string fanId) => _fanId = fanId;

    /// <summary>Spät gebundener IPC-Send (fanId, pwm). Ohne Bindung bleibt die Pumpe ein No-Op.</summary>
    public Func<string, byte, Task>? Send { get; set; }

    /// <summary>Mindestabstand zwischen zwei Sends während eines Zugs. Test-Naht: in Tests auf <c>Zero</c>.</summary>
    public TimeSpan Throttle { get; set; } = TimeSpan.FromMilliseconds(150);

    /// <summary>Läuft, solange die Pumpe noch sendet — Test-Naht zum deterministischen Abwarten.</summary>
    public Task Completion => _pumpTask;

    /// <summary>Meldet einen neuen Zielwert (roher PWM 0–255) an und startet die Pumpe bei Bedarf.</summary>
    public void Set(byte pwm)
    {
        _active = true;
        _pendingPwm = pwm;
        Start();
    }

    /// <summary>Hält die Pumpe an: ein laufender Durchlauf sendet nichts Neues mehr (z. B. beim Verlassen).</summary>
    public void Stop()
    {
        _active = false;
        _pendingPwm = -1;
        _lastSentPwm = -1;
    }

    private void Start()
    {
        if (_sending || Send is null || _pendingPwm == _lastSentPwm)
            return;
        _pumpTask = PumpAsync();
    }

    private async Task PumpAsync()
    {
        var send = Send;
        if (send is null)
            return;

        _sending = true;
        try
        {
            while (_active && _pendingPwm != _lastSentPwm)
            {
                int pwm = _pendingPwm;
                _lastSentPwm = pwm;
                await send(_fanId, (byte)pwm);
                if (Throttle > TimeSpan.Zero)
                    await Task.Delay(Throttle);
            }
        }
        finally
        {
            _sending = false;
        }
    }
}
