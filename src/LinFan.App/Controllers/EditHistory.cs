// SPDX-License-Identifier: GPL-3.0-or-later

namespace LinFan.App.Controllers;

/// <summary>
/// Undo/redo history of the curve editor: a stack of whole-configuration snapshots (<see cref="EditSnapshot"/>).
/// <para>
/// Only user edits are recorded - the controller reports them through <see cref="NoteEdit"/> and keeps its live
/// paths (daemon values, running profile, curve on/off) away from it. Live changes are instead folded into every
/// entry with <see cref="Transform"/>, the same way the dirty baseline is rebased, so undo never brings back a value
/// the daemon has since replaced.
/// </para>
/// <para>
/// Cost: rapid edits (a point drag fires per mouse sample, a stepper per keystroke) coalesce into one step while
/// they are less than <see cref="CoalesceWindow"/> apart. The state before a step comes from a cached snapshot of
/// the current state; the controller serializes at most once when a burst starts and once when it is closed
/// (<see cref="CloseBurst"/>, on the next live tick or before undo/redo/save/delete). A burst that starts before
/// the previous one could be closed cannot be told apart from it and is merged into it - a coarser step, never a
/// wrong state.
/// </para>
/// No timers and no dispatcher: time comes from the injected <see cref="TimeProvider"/>, so plain unit tests can
/// drive the coalescing.
/// </summary>
public sealed class EditHistory
{
    /// <summary>Most steps kept; the oldest one is dropped beyond this.</summary>
    public const int MaxSteps = 60;

    /// <summary>Edits closer together than this form one step.</summary>
    public static readonly TimeSpan CoalesceWindow = TimeSpan.FromMilliseconds(600);

    private readonly TimeProvider _clock;
    private readonly List<EditSnapshot> _undo = new();
    private readonly List<EditSnapshot> _redo = new();

    private string? _current;         // the current configuration as last captured (null before Reset)
    private bool _currentValid;       // false once an edit happened after that capture
    private EditSnapshot? _burstStart; // state before the running burst; null = no burst open
    private bool _burstPushed;        // the burst already put _burstStart on the undo stack
    private DateTimeOffset _lastEditAt;

    public EditHistory(TimeProvider? clock = null) => _clock = clock ?? TimeProvider.System;

    /// <summary>Raised whenever <see cref="CanUndo"/> or <see cref="CanRedo"/> may have changed.</summary>
    public event EventHandler? Changed;

    /// <summary>
    /// Whether there is anything to undo. A burst whose end state is not captured yet counts as well: it may
    /// hold a change even though its first edit did not.
    /// </summary>
    public bool CanUndo => _undo.Count > 0 || (_burstStart is not null && !_currentValid);

    public bool CanRedo => _redo.Count > 0;

    /// <summary>The captured current configuration, or <c>null</c> while edits since the capture are pending.</summary>
    public string? Current => _currentValid ? _current : null;

    /// <summary>
    /// Whether an open burst has been quiet for at least <see cref="CoalesceWindow"/>, so the live tick should
    /// close it (<see cref="CloseBurst"/>). The next edit would otherwise have to merge into it.
    /// </summary>
    public bool BurstIsQuiet => _burstStart is not null && _clock.GetUtcNow() - _lastEditAt >= CoalesceWindow;

    /// <summary>Forgets every step and starts over from <paramref name="current"/> (load, import, reset, assistant).</summary>
    public void Reset(string current)
    {
        _undo.Clear();
        _redo.Clear();
        _current = current;
        _currentValid = true;
        _burstStart = null;
        _burstPushed = false;
        OnChanged();
    }

    /// <summary>
    /// A user edit has just been applied. Opens a step unless the edit continues the running burst.
    /// <paramref name="capture"/> serializes the current state; it is only called when a new burst starts.
    /// </summary>
    /// <param name="where">The selection at the time of the edit - restored with the step.</param>
    public void NoteEdit(Func<string> capture, EditSelection where)
    {
        DateTimeOffset now = _clock.GetUtcNow();
        if (_burstStart is not null)
        {
            // Within the window: same step. After it, but with the burst's end never captured: the state
            // before this edit is unknown, so it can only join the burst.
            if (now - _lastEditAt < CoalesceWindow || !_currentValid)
            {
                _lastEditAt = now;
                bool wasValid = _currentValid;
                _currentValid = false;
                if (wasValid && !_burstPushed)
                    OnChanged(); // CanUndo now counts the open burst
                return;
            }
            _burstStart = null; // previous burst complete and captured
        }

        if (_current is null || !_currentValid)
            return; // not initialised yet, or a restore has not been recaptured: the state before is unknown

        var before = new EditSnapshot(_current, where);
        string after = capture();
        _burstStart = before;
        _lastEditAt = now;
        _current = after;
        _currentValid = true;
        _burstPushed = after != before.Config;
        if (_burstPushed)
            PushNewStep(before);
    }

    /// <summary>
    /// Ends the running burst: captures its end state if edits after the last capture are pending, and keeps the
    /// step only if the configuration really changed (identical snapshots are dropped - selecting a profile, for
    /// instance, rebuilds the editor without changing anything).
    /// </summary>
    public void CloseBurst(Func<string> capture)
    {
        if (_burstStart is not { } start)
            return;
        _burstStart = null;
        if (_currentValid)
            return; // single-edit burst, decided when it started

        string after = capture();
        bool changed = after != start.Config;
        if (changed && !_burstPushed)
            PushNewStep(start);
        else if (!changed && _burstPushed)
            _undo.RemoveAt(_undo.Count - 1); // nothing else is pushed while a burst is open
        _current = after;
        _currentValid = true;
        OnChanged();
    }

    /// <summary>
    /// Records a step that is always its own (delete, discard all, add, ...): <paramref name="before"/> → <paramref name="after"/>.
    /// The caller closes a running burst first, so <paramref name="before"/> is exact.
    /// </summary>
    public void Record(EditSnapshot before, string after)
    {
        _burstStart = null;
        if (after != before.Config)
            PushNewStep(before);
        _current = after;
        _currentValid = true;
    }

    /// <summary>
    /// Steps back: <paramref name="current"/> goes onto the redo stack, the returned snapshot is what the editor has
    /// to restore. Entries that equal the current state (a live change made them identical) are skipped. The caller
    /// closes a running burst first and reports the restored state through <see cref="Recapture"/>.
    /// </summary>
    public EditSnapshot? Undo(EditSnapshot current) => Step(_undo, _redo, current);

    /// <summary>Counterpart of <see cref="Undo"/>.</summary>
    public EditSnapshot? Redo(EditSnapshot current) => Step(_redo, _undo, current);

    /// <summary>The editor has been rebuilt to <paramref name="current"/> (after undo/redo) - the new capture.</summary>
    public void Recapture(string current)
    {
        _burstStart = null;
        _current = current;
        _currentValid = true;
    }

    /// <summary>
    /// Applies a live change (daemon-adopted PWM limits, curve on/off, running profile) to every entry, the open
    /// burst's start and the capture - the history counterpart of rebasing the dirty baseline.
    /// </summary>
    public void Transform(Func<string, string> apply)
    {
        for (int i = 0; i < _undo.Count; i++)
            _undo[i] = _undo[i] with { Config = apply(_undo[i].Config) };
        for (int i = 0; i < _redo.Count; i++)
            _redo[i] = _redo[i] with { Config = apply(_redo[i].Config) };
        if (_burstStart is not null)
            _burstStart = _burstStart with { Config = apply(_burstStart.Config) };
        if (_current is not null)
            _current = apply(_current);
    }

    private EditSnapshot? Step(List<EditSnapshot> from, List<EditSnapshot> to, EditSnapshot current)
    {
        _burstStart = null;
        EditSnapshot? target = null;
        while (from.Count > 0 && target is null)
        {
            EditSnapshot candidate = from[^1];
            from.RemoveAt(from.Count - 1);
            if (candidate.Config != current.Config)
                target = candidate;
        }
        if (target is not null)
        {
            to.Add(current);
            Trim(to);
            _currentValid = false; // until the caller recaptures the restored state
        }
        OnChanged();
        return target;
    }

    private void PushNewStep(EditSnapshot before)
    {
        _undo.Add(before);
        Trim(_undo);
        _redo.Clear(); // a new edit makes the undone steps unreachable
        OnChanged();
    }

    private static void Trim(List<EditSnapshot> stack)
    {
        if (stack.Count > MaxSteps)
            stack.RemoveRange(0, stack.Count - MaxSteps);
    }

    private void OnChanged() => Changed?.Invoke(this, EventArgs.Empty);
}
