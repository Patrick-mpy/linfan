// SPDX-License-Identifier: GPL-3.0-or-later

namespace LinFan.App.Controllers;

/// <summary>The pressure balance card of Settings › Airflow, ready to bind (built by <see cref="AirflowText.Balance"/>).</summary>
public sealed record AirflowBalance
{
    public static AirflowBalance Empty { get; } = new();

    /// <summary>At least one visible case fan with an intake or exhaust position - otherwise only the verdict shows.</summary>
    public bool HasCaseFans { get; init; }

    /// <summary>Intake share 0..100 - the width of the intake part of the bar.</summary>
    public double IntakePercent { get; init; } = 50;

    public string IntakeHead { get; init; } = "";
    public string ExhaustHead { get; init; } = "";
    public string IntakeValue { get; init; } = "";
    public string ExhaustValue { get; init; } = "";

    /// <summary>„nach Drehzahl" / „nach Anzahl".</summary>
    public string BasisText { get; init; } = "";
    public StatusTone BasisTone { get; init; } = StatusTone.Muted;
    public string BasisTooltip { get; init; } = "";

    public string VerdictText { get; init; } = "";
    public StatusTone VerdictTone { get; init; } = StatusTone.Muted;

    /// <summary>The bar for screen readers („Druckbilanz 60 % Einlass zu 40 % Auslass").</summary>
    public string AutomationName { get; init; } = "";
}
