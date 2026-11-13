// SPDX-License-Identifier: GPL-3.0-or-later

using LinFan.Core.Services;

namespace LinFan.App.Controllers;

/// <summary>
/// What the "Controlled by" column says about a fan: its label and tooltip in the UI language, the tone, and the curve
/// a chip opens (<see cref="CurveId"/>, only while a curve drives or holds the fan). The daemon's state behind it stays
/// internal - the view binds the texts, not a Core type.
/// </summary>
public sealed record FanControl
{
    internal FanControl(FanControlState? state, string label, string tooltip, ControlTone tone, string? curveId = null)
    {
        State = state;
        Label = label;
        Tooltip = tooltip;
        Tone = tone;
        CurveId = curveId;
    }

    /// <summary>The daemon's state; <c>null</c> = unknown.</summary>
    internal FanControlState? State { get; }

    public string Label { get; }
    public string Tooltip { get; }
    public ControlTone Tone { get; }
    public string? CurveId { get; }

    /// <summary>Shown as a chip that opens the curve.</summary>
    public bool OpensCurve => CurveId is not null;
}
