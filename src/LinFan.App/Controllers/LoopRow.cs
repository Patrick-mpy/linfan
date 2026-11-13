// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LinFan.App.Localization;
using LinFan.Core.Models;
using LinFan.Core.Services;

namespace LinFan.App.Controllers;

/// <summary>
/// One control loop of the running profile in the overview: source sensor(s) → curve → fans. Reads the live curve row
/// (<see cref="CurveEditRow"/> from the daemon's set) and the fan rows; <see cref="Refresh"/> recomputes the input and
/// output each tick and keeps the switch in line with the daemon. The switch and "edit" delegate to the controller.
/// </summary>
public sealed partial class LoopRow : ObservableObject
{
    /// <summary>Samples of the mini curve: one per °C from 0 to 100, so steps and splines render as they evaluate.</summary>
    public const int SampleCount = 101;

    /// <summary>The curve's output at 0, 1 … 100 °C - what a mini chart draws.</summary>
    public static double[] Sample(Curve curve) =>
        Enumerable.Range(0, SampleCount).Select(t => CurveEngine.Evaluate(curve, t)).ToArray();

    /// <summary>
    /// How many ticks the switch keeps a toggle the daemon has not confirmed yet - its config may still be the old one for
    /// a tick or two. Without a confirmation by then the daemon's state wins: a lost command shows instead of hiding.
    /// </summary>
    private const int MaxPendingTicks = 3;

    private readonly Action<bool> _onToggle;
    private readonly Action _onEdit;
    private Curve _coreCurve = null!;
    private bool _applying;      // the switch is set from the daemon's state, not by the user
    private bool? _pendingEnabled;
    private int _pendingTicks;

    /// <param name="curve">The curve as the daemon runs it (a row of <c>Editor.LiveCurves</c>).</param>
    /// <param name="fans">Every fan the curve drives - hidden ones included: they follow the curve all the same.</param>
    /// <param name="history">The input's rolling history, kept by the controller per curve id so it survives rebuilds.</param>
    public LoopRow(CurveEditRow curve, IReadOnlyList<FanRow> fans, ObservableCollection<double> history,
                   Action<bool> onToggle, Action onEdit)
    {
        History = history;
        _onToggle = onToggle;
        _onEdit = onEdit;
        _curve = curve;
        _fans = fans;
        _enabled = curve.Enabled;
        Rebind(curve, fans);
    }

    /// <summary>The live curve row - replaced by <see cref="Rebind"/> when the daemon's set is rebuilt.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Name), nameof(Summary), nameof(SwitchName))]
    private CurveEditRow _curve;

    [ObservableProperty] private IReadOnlyList<FanRow> _fans;

    /// <summary>The curve's output for 0..100 °C in 1 °C steps (percent) - the mini curve.</summary>
    [ObservableProperty] private IReadOnlyList<double> _curveSamples = Array.Empty<double>();

    public string Name => Curve.Name;
    public ObservableCollection<double> History { get; }

    /// <summary>Accessible name of the switch.</summary>
    public string SwitchName => Localizer.Instance.Format("Overview.LoopSwitchName", Name);

    /// <summary>The sensor, or "n sensors · Maximum/Average".</summary>
    public string Summary
    {
        get
        {
            IReadOnlyList<SensorOption> sources = Curve.Sources;
            return sources.Count == 1
                ? sources[0].Name
                : Localizer.Instance.Format("Overview.LoopSources", sources.Count,
                    AggregationOption.For(Curve.Aggregation).Display);
        }
    }

    /// <summary>Tooltip of the input: each source with its value.</summary>
    public string InputTooltip => string.Join("\n", Curve.Sources.Select(s => $"{s.Name}: {s.LiveValue}"));

    /// <summary>The switch. A user toggle goes to the controller (live, persisted by the daemon).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OutputText))]
    private bool _enabled;

    /// <summary>Offline the switch is locked: the command would not arrive, but the editor would already count it as done.</summary>
    [ObservableProperty] private bool _canToggle;

    /// <summary>The aggregated input (°C) or NaN.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TemperatureText), nameof(IsHot), nameof(DotTemperature))]
    private double _temperature = double.NaN;

    /// <summary>The curve's value at <see cref="Temperature"/> (percent) or NaN.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OutputText))]
    private double _outputPercent = double.NaN;

    public string TemperatureText => double.IsNaN(Temperature)
        ? "-"
        : Temperature.ToString("0", CultureInfo.InvariantCulture) + " °C";

    public bool IsHot => TemperatureLevels.IsHot(Temperature);

    /// <summary>"≈ n %" - approximate: the daemon smooths the input, applies the hysteresis and the fans' limits.</summary>
    public string OutputText => !Enabled
        ? Localizer.Instance["Overview.LoopOff"]
        : double.IsNaN(OutputPercent)
            ? "-"
            : Localizer.Instance.Format("Overview.LoopOutput", Math.Round(OutputPercent).ToString("0", CultureInfo.InvariantCulture));

    /// <summary>Where the mini curve's dot sits: the input clamped to the drawn 0..100 °C, NaN for no dot.</summary>
    public double DotTemperature => double.IsNaN(Temperature) ? double.NaN : Math.Clamp(Temperature, 0, 100);

    /// <summary>
    /// Points the row at a rebuilt live curve and its fans (same curve id), so the row - and a focused switch in it -
    /// survives the rebuild instead of being replaced.
    /// </summary>
    public void Rebind(CurveEditRow curve, IReadOnlyList<FanRow> fans)
    {
        Curve = curve;
        Fans = fans;
        _coreCurve = new Curve(curve.Name, curve.ToConfig().Points, curve.InterpolationMode);
        CurveSamples = Sample(_coreCurve);
        Refresh(CanToggle, curve.Enabled);
    }

    /// <summary>
    /// Reads the live input, evaluates the curve at it and aligns the switch with the daemon's state.
    /// </summary>
    /// <param name="canToggle">The daemon is reachable.</param>
    /// <param name="daemonEnabled">Whether the curve is on in the daemon's config.</param>
    public void Refresh(bool canToggle, bool daemonEnabled)
    {
        CanToggle = canToggle;
        Temperature = Curve.LiveTemperature;
        OutputPercent = double.IsNaN(Temperature) ? double.NaN : CurveEngine.Evaluate(_coreCurve, Temperature);
        SyncEnabled(daemonEnabled);
        // Built from the live values and the UI language - re-read every tick.
        OnPropertyChanged(nameof(InputTooltip));
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(SwitchName));
        OnPropertyChanged(nameof(OutputText));
    }

    private void SyncEnabled(bool daemonEnabled)
    {
        if (_pendingEnabled is { } expected)
        {
            if (daemonEnabled != expected && ++_pendingTicks <= MaxPendingTicks)
                return; // not confirmed yet - keep the user's toggle for now
            _pendingEnabled = null;
            _pendingTicks = 0;
        }
        _applying = true;
        Enabled = daemonEnabled;
        _applying = false;
    }

    partial void OnEnabledChanged(bool value)
    {
        if (_applying)
            return;
        _pendingEnabled = value;
        _pendingTicks = 0;
        _onToggle(value);
    }

    /// <summary>Opens the curve in the editor.</summary>
    [RelayCommand]
    private void Edit() => _onEdit();
}
