// SPDX-License-Identifier: GPL-3.0-or-later

using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LinFan.App.Localization;

namespace LinFan.App.Controllers;

/// <summary>
/// One entry of the overview's hint list (the status tile summarises them): severity, text and an optional action.
/// <see cref="OverviewHints"/> builds the data; the <see cref="MainController"/> attaches <see cref="Command"/>.
/// <see cref="Key"/> (rule + subject) is the identity across ticks: a hint whose value changes (a temperature, a percent)
/// keeps its instance and only its text changes - the row, its focused button and the screen reader stay undisturbed.
/// Severity and action may change under the same key too (an action is not offered while it cannot start).
/// </summary>
public sealed partial class OverviewHint : ObservableObject
{
    public OverviewHint(string key, HintSeverity severity, string text, HintAction action = HintAction.None,
                        string? target = null, string actionLabel = "")
    {
        Key = key;
        _severity = severity;
        _text = text;
        _action = action;
        _target = target;
        _actionLabel = actionLabel;
    }

    /// <summary>The rule and its subject, e.g. "hot:k10temp/temp1".</summary>
    public string Key { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCritical), nameof(IsNotice), nameof(AccessibleText))]
    private HintSeverity _severity;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AccessibleText))]
    private string _text;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAction), nameof(NeedsDaemon))]
    private HintAction _action;

    /// <summary>What <see cref="Action"/> acts on (group key, fan id or curve id).</summary>
    [ObservableProperty] private string? _target;

    [ObservableProperty] private string _actionLabel;

    public bool HasAction => Action != HintAction.None;

    /// <summary>The action sends a command to the daemon, so it is locked while offline.</summary>
    public bool NeedsDaemon => Action is HintAction.BackToCurve or HintAction.EnableCurve or HintAction.Calibrate;

    public bool IsCritical => Severity == HintSeverity.Critical;
    public bool IsNotice => Severity == HintSeverity.Notice;

    /// <summary>The text with its severity in words - the icon's colour and shape are not read out.</summary>
    public string AccessibleText => Localizer.Instance.Format("Overview.HintAccessible", Localizer.Instance[Severity switch
    {
        HintSeverity.Critical => "Overview.SeverityCritical",
        HintSeverity.Notice => "Overview.SeverityNotice",
        _ => "Overview.SeverityInfo",
    }], Text);

    /// <summary>The button's command; <c>null</c> until the controller binds one.</summary>
    public ICommand? Command { get; set; }

    /// <summary>Takes the changeable parts of <paramref name="fresh"/> (same <see cref="Key"/>); returns this instance.</summary>
    public OverviewHint UpdateFrom(OverviewHint fresh)
    {
        Severity = fresh.Severity;
        Action = fresh.Action;
        Text = fresh.Text;
        Target = fresh.Target;
        ActionLabel = fresh.ActionLabel;
        OnPropertyChanged(nameof(AccessibleText)); // follows a language switch even with the same text
        return this;
    }

    // The command's lock depends on the action (see NeedsDaemon).
    partial void OnActionChanged(HintAction value) => (Command as IRelayCommand)?.NotifyCanExecuteChanged();
}
