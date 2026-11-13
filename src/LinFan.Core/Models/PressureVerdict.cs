// SPDX-License-Identifier: GPL-3.0-or-later

namespace LinFan.Core.Models;

/// <summary>
/// How the case pressure reads, from the intake share of the case fans' flow weight (see
/// <see cref="AirflowTuneResult.IntakeShare"/>): the GUI turns each value into a sentence and a tone.
/// </summary>
public enum PressureVerdict
{
    /// <summary>No case fan with an intake or exhaust position.</summary>
    Unknown,

    /// <summary>Exhaust only - strong negative pressure.</summary>
    OnlyExhaust,

    /// <summary>Intake only - strong positive pressure.</summary>
    OnlyIntake,

    /// <summary>Clearly more exhaust than intake.</summary>
    Negative,

    /// <summary>Intake and exhaust about even.</summary>
    Balanced,

    /// <summary>Somewhat more intake - the usual goal (keeps dust out).</summary>
    SlightPositive,

    /// <summary>Far more intake - warm air tends to build up.</summary>
    StrongPositive,

    /// <summary>
    /// An open bench or a laptop (<see cref="AirflowTuneResult.CaseType"/> tells which): there is no case volume whose
    /// pressure could be balanced.
    /// </summary>
    NotApplicable,

    /// <summary>A server with only its fan wall: it moves the air inside the case, nothing brings air in or out.</summary>
    FanWallOnly,
}
