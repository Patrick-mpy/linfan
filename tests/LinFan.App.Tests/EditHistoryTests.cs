// SPDX-License-Identifier: GPL-3.0-or-later

using LinFan.App.Controllers;
using Xunit;

namespace LinFan.App.Tests;

/// <summary>
/// The history on its own, with strings standing in for configurations: coalescing, redo clearing, the cap, dropping
/// no-op steps and applying live changes to every entry.
/// </summary>
public sealed class EditHistoryTests
{
    private static readonly EditSelection Here = new("p1", "c1", CurveTabPane.Curve);

    /// <summary>A stand-in editor: <see cref="State"/> is the "configuration", <see cref="Edit"/> applies an edit and reports it.</summary>
    private sealed class Driver
    {
        public ManualClock Clock { get; } = new();
        public EditHistory History { get; }
        public string State { get; private set; } = "0";
        public int Captures { get; private set; }

        public Driver()
        {
            History = new EditHistory(Clock);
            History.Reset(State);
        }

        private string Capture()
        {
            Captures++;
            return State;
        }

        /// <summary>A change that bypasses <see cref="EditHistory.NoteEdit"/> (an own step, recorded by the caller).</summary>
        public void Apply(string state) => State = state;

        public void Edit(string state, int afterMs = 0)
        {
            Clock.AdvanceMs(afterMs);
            State = state;
            History.NoteEdit(Capture, Here);
        }

        /// <summary>The live tick: closes the burst once it went quiet.</summary>
        public void Tick(int afterMs)
        {
            Clock.AdvanceMs(afterMs);
            if (History.BurstIsQuiet)
                History.CloseBurst(Capture);
        }

        public bool Undo()
        {
            History.CloseBurst(Capture);
            EditSnapshot? target = History.Undo(new EditSnapshot(State, Here));
            if (target is null)
                return false;
            State = target.Config;
            History.Recapture(State);
            return true;
        }

        public bool Redo()
        {
            History.CloseBurst(Capture);
            EditSnapshot? target = History.Redo(new EditSnapshot(State, Here));
            if (target is null)
                return false;
            State = target.Config;
            History.Recapture(State);
            return true;
        }
    }

    [Fact]
    public void RapidEdits_FormOneStep()
    {
        var d = new Driver();
        d.Edit("4");
        d.Edit("45", afterMs: 150); // a stepper commits per keystroke: „4", then „45"
        d.Tick(1000);

        Assert.True(d.Undo());
        Assert.Equal("0", d.State);
        Assert.False(d.History.CanUndo);
    }

    [Fact]
    public void EditsSeparatedByAQuietTick_AreSeparateSteps()
    {
        var d = new Driver();
        d.Edit("a");
        d.Edit("ab", afterMs: 100);
        d.Tick(700);               // quiet for 600 ms → closed
        d.Edit("abc", afterMs: 50); // a new step even before the next tick

        Assert.True(d.Undo());
        Assert.Equal("ab", d.State);
        Assert.True(d.Undo());
        Assert.Equal("0", d.State);
    }

    [Fact]
    public void SingleEdits_AreExactSteps_WithoutATick()
    {
        // A burst of one edit is captured when it starts, so the next one can be told apart right away.
        var d = new Driver();
        d.Edit("a");
        d.Edit("b", afterMs: 700);

        Assert.True(d.Undo());
        Assert.Equal("a", d.State);
    }

    [Fact]
    public void BurstThatWasNeverClosed_MergesWithTheNextOne()
    {
        // The state between the two bursts was never captured - joining them is coarse, but never wrong.
        var d = new Driver();
        d.Edit("a");
        d.Edit("ab", afterMs: 100);
        d.Edit("abc", afterMs: 700);

        Assert.True(d.Undo());
        Assert.Equal("0", d.State);
    }

    [Fact]
    public void Serializes_OncePerBurstStart_AndOnceToCloseIt()
    {
        var d = new Driver();
        int before = d.Captures;
        for (int i = 0; i < 50; i++)
            d.Edit($"drag{i}", afterMs: 16); // a point drag: one edit per mouse sample

        Assert.Equal(before + 1, d.Captures);
        d.Tick(1000);
        Assert.Equal(before + 2, d.Captures);
    }

    [Fact]
    public void NewEdit_ClearsRedo()
    {
        var d = new Driver();
        d.Edit("a");
        d.Tick(1000);
        Assert.True(d.Undo());
        Assert.True(d.History.CanRedo);

        d.Edit("x", afterMs: 1000);

        Assert.False(d.History.CanRedo);
        Assert.False(d.Redo());
    }

    [Fact]
    public void UndoThenRedo_RoundTrips()
    {
        var d = new Driver();
        d.Edit("a");
        d.Edit("b", afterMs: 700);

        Assert.True(d.Undo());
        Assert.True(d.Undo());
        Assert.Equal("0", d.State);
        Assert.True(d.Redo());
        Assert.Equal("a", d.State);
        Assert.True(d.Redo());
        Assert.Equal("b", d.State);
        Assert.False(d.Redo());
    }

    [Fact]
    public void IdenticalSnapshots_AreDropped()
    {
        // e.g. a drag that ends where it started: the step changes nothing and must not exist.
        var d = new Driver();
        d.Edit("a");
        d.Edit("0", afterMs: 100);
        d.Tick(1000);

        Assert.False(d.History.CanUndo);
    }

    [Fact]
    public void History_IsCappedAtMaxSteps()
    {
        var d = new Driver();
        for (int i = 1; i <= EditHistory.MaxSteps + 10; i++)
            d.Edit($"s{i}", afterMs: 1000);

        int undone = 0;
        while (d.Undo())
            undone++;

        Assert.Equal(EditHistory.MaxSteps, undone);
        Assert.Equal("s10", d.State); // the oldest ten steps fell off
    }

    [Fact]
    public void Record_IsAlwaysItsOwnStep()
    {
        var d = new Driver();
        d.Edit("a");
        d.History.CloseBurst(() => d.State);

        d.Apply("a-deleted");
        d.History.Record(new EditSnapshot("a", Here), d.State); // same instant as the edit

        Assert.True(d.Undo());
        Assert.Equal("a", d.State);
    }

    [Fact]
    public void Transform_AppliesToEveryEntry_AndUndoSkipsEntriesItMadeEqual()
    {
        var d = new Driver();
        d.Edit("min40-a");
        d.Edit("min40-ab", afterMs: 700);
        d.History.CloseBurst(() => d.State);

        // Live: the daemon replaced min40 by min96 - in the editor and in every step.
        d.History.Transform(s => s.Replace("min40", "min96", StringComparison.Ordinal));
        string live = d.State.Replace("min40", "min96", StringComparison.Ordinal);

        Assert.Equal("min96-ab", live);
        EditSnapshot? target = d.History.Undo(new EditSnapshot(live, Here));
        Assert.Equal("min96-a", target!.Config); // undo never brings the replaced value back
    }

    [Fact]
    public void Reset_ForgetsEverything()
    {
        var d = new Driver();
        d.Edit("a");
        d.Edit("b", afterMs: 700);
        d.Undo();

        d.History.Reset("fresh");

        Assert.False(d.History.CanUndo);
        Assert.False(d.History.CanRedo);
    }

    [Fact]
    public void Changed_FiresWhenCanUndoFlips()
    {
        var d = new Driver();
        int fired = 0;
        d.History.Changed += (_, _) => fired++;

        d.Edit("a");

        Assert.True(fired > 0);
        Assert.True(d.History.CanUndo);
    }
}
