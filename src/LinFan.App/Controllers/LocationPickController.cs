// SPDX-License-Identifier: GPL-3.0-or-later

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LinFan.App.Localization;
using LinFan.Core.Models;

namespace LinFan.App.Controllers;

/// <summary>
/// The position dialog's state: the pick (a zone of the case schema, the grouped menu or the direction switch - all
/// on the same <see cref="Selected"/>), the case type the schema shows (a draft too: it applies with the pick, so
/// cancelling takes both back), the other fans for orientation and the nudge that spins the fan up to find it.
/// The dialog only returns the pick (<see cref="Result"/>); taking it over is the caller's (an editor step, an
/// onboarding row).
/// </summary>
public sealed partial class LocationPickController : ObservableObject
{
    private readonly CaseFan _self;
    private readonly IReadOnlyList<CaseFan> _others;

    /// <param name="others">The other visible fans - shown dimmed in their zones, and as occupancy in the menu.</param>
    public LocationPickController(string fanId, string fanName, FanLocation current, CaseType caseType, ManualControl manual,
                                  IReadOnlyList<CaseFan> others)
    {
        _self = new CaseFan(fanId, fanName, current, manual);
        _others = others;
        FanName = fanName;
        Original = current;
        OriginalCaseType = caseType;
        Manual = manual;
        _selected = current;
        _caseType = caseType;
        Manual.PropertyChanged += OnManualChanged;
        Rebuild();
    }

    /// <summary>
    /// Asks the view to close the dialog as a cancel: the editor rebuilt its fan rows (a resync), so <see cref="Manual"/>
    /// belongs to a row that is gone and would no longer follow the daemon.
    /// </summary>
    public event EventHandler? CloseRequested;

    /// <summary>The dialog closed: let go of the fan's manual control (it outlives the dialog) and end a nudge.</summary>
    public void Close()
    {
        Manual.PropertyChanged -= OnManualChanged;
        Manual.Revert();
    }

    internal void RequestClose() => CloseRequested?.Invoke(this, EventArgs.Empty);

    private void OnManualChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ManualControl.IsActive) or nameof(ManualControl.EngageBlocked))
        {
            OnPropertyChanged(nameof(NudgeLabel));
            OnPropertyChanged(nameof(NudgeTooltip));
            NudgeCommand.NotifyCanExecuteChanged();
        }
    }

    public string FanName { get; }

    /// <summary>The position the fan had when the dialog opened.</summary>
    public FanLocation Original { get; }

    // The case type when the dialog opened.
    private CaseType OriginalCaseType { get; }

    public ManualControl Manual { get; }

    /// <summary>„Position von „CPU"".</summary>
    public string Heading => Localizer.Instance.Format("FanLocationDialog.HeadingFor", FanName);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedLabel), nameof(CanFlip), nameof(DirectionIndex), nameof(Moved), nameof(Changed),
        nameof(FromText), nameof(ToText))]
    private FanLocation _selected;

    /// <summary>The case type the schema and the menu show - picked with the „Gehäuse ▾" chip.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CaseTypeLabel), nameof(CaseTypeChipName), nameof(CaseCaption), nameof(Changed),
        nameof(FromText), nameof(ToText), nameof(CanFlip), nameof(DirectionIndex), nameof(SelectedLabel))]
    private CaseType _caseType;

    [ObservableProperty] private IReadOnlyList<MenuChoice> _caseTypeMenu = [];

    public string CaseTypeLabel => CaseTypeText.Name(CaseType);

    public string CaseTypeChipName => CaseTypeText.ChipName(CaseType);

    public string CaseCaption => CaseTypeText.Caption(CaseType);

    [ObservableProperty] private IReadOnlyList<CaseZone> _zones = [];
    [ObservableProperty] private IReadOnlyList<CaseZone> _tray = [];
    [ObservableProperty] private IReadOnlyList<MenuChoice> _positionMenu = [];

    /// <summary>The pick's full name in the case type („Gehäuse oben · Auslass", „Tastatur · Einlass", „Ohne Position").</summary>
    public string SelectedLabel => LabelOf(Selected);

    /// <summary>The pick is a case edge of the type, so its direction can be switched.</summary>
    public bool CanFlip => CaseSchema.CanFlip(Selected) && CaseSchema.IsInSchema(Selected, CaseType);

    /// <summary>Segment switch „Einlass | Auslass": 0 = intake, 1 = exhaust. Writing flips the pick's direction.</summary>
    public int DirectionIndex
    {
        get => FanLocationOption.DirectionOf(Selected) == AirflowDirection.Exhaust ? 1 : 0;
        set
        {
            if (CanFlip && value is 0 or 1 && value != DirectionIndex)
                Selected = CaseSchema.Flip(Selected);
        }
    }

    /// <summary>The fan's position changed.</summary>
    public bool Moved => Selected != Original;

    /// <summary>The position or the case type changed - only then can the dialog apply.</summary>
    public bool Changed => Moved || CaseType != OriginalCaseType;

    /// <summary>What applying hands back; <c>null</c> while nothing changed.</summary>
    public LocationPick? Result => Changed ? new LocationPick(Selected, CaseType) : null;

    /// <summary>The footer: „bisher" → „neu", only the new case type, or „Keine Änderung".</summary>
    public string FromText =>
        Moved ? LabelOf(Original)
        : Changed ? Localizer.Instance.Format("FanLocationDialog.CaseOnly", CaseTypeLabel)
        : Localizer.Instance["FanLocationDialog.NoChange"];

    public string ToText => Moved ? "→ " + LabelOf(Selected) : "";

    /// <summary>The nudge exists only for a fan that can be driven.</summary>
    public bool ShowNudge => Manual.CanControl;

    public string NudgeLabel => Localizer.Instance[Manual.IsActive ? "FanLocationDialog.NudgeStop" : "FanLocationDialog.Nudge"];

    public string NudgeTooltip => !Manual.IsActive && Manual.EngageBlocked
        ? Localizer.Instance["FanLocationDialog.NudgeBlocked"]
        : Localizer.Instance["FanLocationDialog.NudgeTooltip"];

    partial void OnSelectedChanged(FanLocation value) => Rebuild();

    partial void OnCaseTypeChanged(CaseType value) => Rebuild();

    /// <summary>A click on a zone: its usual direction - a click on the pick itself keeps a switched direction.</summary>
    private void Pick(FanLocation usual)
    {
        if (CaseSchema.ZoneOf(Selected) != CaseSchema.ZoneOf(usual))
            Selected = usual;
    }

    /// <summary>Spins the fan up to find it in the case (full speed), or lets it go back to its curve.</summary>
    [RelayCommand(CanExecute = nameof(CanNudge))]
    private void Nudge()
    {
        if (Manual.IsActive)
        {
            Manual.IsActive = false;
            return;
        }
        Manual.IsActive = true;
        if (Manual.IsActive)
            Manual.Percent = 100;
    }

    private bool CanNudge() => Manual.IsActive || !Manual.EngageBlocked;

    private void Rebuild()
    {
        IReadOnlyList<CaseZone> zones = CaseSchema.Build(_others, CaseType, Selected, _self, Pick,
            Localizer.Instance["FanLocationDialog.TagNew"], Localizer.Instance["FanLocationDialog.TagUnchanged"]);
        Zones = zones.Where(z => !z.IsTray).ToList();
        Tray = zones.Where(z => z.IsTray).ToList();
        PositionMenu = Controllers.PositionMenu.Build(Selected, loc => Selected = loc, CaseType, Occupancy);
        CaseTypeMenu = CaseTypeText.Menu(CaseType, t => CaseType = t);
    }

    // Who else sits at exactly this position (for the menu).
    private string Occupancy(FanLocation loc) => string.Join(", ", _others.Where(f => f.Location == loc).Select(f => f.Name));

    private string LabelOf(FanLocation loc) => CaseSchema.FullName(loc, CaseType);
}
