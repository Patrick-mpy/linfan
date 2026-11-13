// SPDX-License-Identifier: GPL-3.0-or-later

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LinFan.App.Controllers;
using LinFan.Core.Models;

namespace LinFan.App.Controls;

/// <summary>
/// The case schema (<see cref="CaseSchema"/>) as a view: the zones on the grid of the <see cref="CaseType"/>, a caption
/// with the colour legend, and the tray below. On the airflow page the fans inside are the buttons; in the position
/// dialog (<see cref="IsPicker"/>) the zones are a radio group and the arrow keys walk the pickable ones in
/// <see cref="CaseSchema.PickOrder"/> - moving the focus along is pure UI, the pick itself goes through each zone's command.
/// </summary>
public partial class CaseSchemaView : UserControl
{
    public static readonly StyledProperty<IEnumerable<CaseZone>?> ZonesProperty =
        AvaloniaProperty.Register<CaseSchemaView, IEnumerable<CaseZone>?>(nameof(Zones));

    public static readonly StyledProperty<IEnumerable<CaseZone>?> TrayProperty =
        AvaloniaProperty.Register<CaseSchemaView, IEnumerable<CaseZone>?>(nameof(Tray));

    public static readonly StyledProperty<double> CaseHeightProperty =
        AvaloniaProperty.Register<CaseSchemaView, double>(nameof(CaseHeight), 400);

    public static readonly StyledProperty<bool> IsPickerProperty =
        AvaloniaProperty.Register<CaseSchemaView, bool>(nameof(IsPicker));

    public static readonly StyledProperty<string> WasHereTextProperty =
        AvaloniaProperty.Register<CaseSchemaView, string>(nameof(WasHereText), "");

    public static readonly StyledProperty<CaseType> CaseTypeProperty =
        AvaloniaProperty.Register<CaseSchemaView, CaseType>(nameof(CaseType));

    public static readonly StyledProperty<string> CaptionProperty =
        AvaloniaProperty.Register<CaseSchemaView, string>(nameof(Caption), "");

    public static readonly StyledProperty<IDataTemplate?> FanTemplateProperty =
        AvaloniaProperty.Register<CaseSchemaView, IDataTemplate?>(nameof(FanTemplate));

    public IEnumerable<CaseZone>? Zones
    {
        get => GetValue(ZonesProperty);
        set => SetValue(ZonesProperty, value);
    }

    public IEnumerable<CaseZone>? Tray
    {
        get => GetValue(TrayProperty);
        set => SetValue(TrayProperty, value);
    }

    /// <summary>Which grid the zones sit on - it has to match the type <see cref="CaseSchema.Build"/> laid them out for.</summary>
    public CaseType CaseType
    {
        get => GetValue(CaseTypeProperty);
        set => SetValue(CaseTypeProperty, value);
    }

    /// <summary>What the view shows („Seitenansicht · vorn links, hinten rechts"), next to the colour legend.</summary>
    public string Caption
    {
        get => GetValue(CaptionProperty);
        set => SetValue(CaptionProperty, value);
    }

    public double CaseHeight
    {
        get => GetValue(CaseHeightProperty);
        set => SetValue(CaseHeightProperty, value);
    }

    /// <summary>The zones are the choice (position dialog) instead of the fans being the buttons (airflow page).</summary>
    public bool IsPicker
    {
        get => GetValue(IsPickerProperty);
        set => SetValue(IsPickerProperty, value);
    }

    /// <summary>„bisher hier" under the zone a fan is moved away from.</summary>
    public string WasHereText
    {
        get => GetValue(WasHereTextProperty);
        set => SetValue(WasHereTextProperty, value);
    }

    /// <summary>How a fan inside a zone looks - set from <see cref="IsPicker"/>.</summary>
    public IDataTemplate? FanTemplate
    {
        get => GetValue(FanTemplateProperty);
        private set => SetValue(FanTemplateProperty, value);
    }

    /// <summary>One radio group per schema, so two schemas never share a selection.</summary>
    public string GroupName { get; } = "caseSchema-" + Guid.NewGuid().ToString("N");

    public CaseSchemaView()
    {
        InitializeComponent();
        ApplyMode();
        ApplyCaseType();
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Bubble);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsPickerProperty)
            ApplyMode();
        else if (change.Property == CaseTypeProperty)
            ApplyCaseType();
    }

    private void ApplyCaseType() => GridItems.ItemsPanel = (ITemplate<Panel?>)Resources[CaseType switch
    {
        CaseType.Server => "ServerPanel",
        CaseType.Laptop => "LaptopPanel",
        CaseType.Open => "OpenPanel",
        _ => "TowerPanel",
    }]!;

    private void ApplyMode()
    {
        var zone = (IDataTemplate)Resources[IsPicker ? "ZonePick" : "ZoneStatic"]!;
        GridItems.ItemTemplate = zone;
        TrayItems.ItemTemplate = zone;
        FanTemplate = (IDataTemplate)Resources[IsPicker ? "FanText" : "FanButton"]!;
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        int step = e.Key switch
        {
            Key.Right or Key.Down => 1,
            Key.Left or Key.Up => -1,
            _ => 0,
        };
        if (!IsPicker || step == 0 || e.KeyModifiers != KeyModifiers.None)
            return;

        // CaseSchema.Build hands them over in PickOrder; „Nicht im Schema" is no position to walk to.
        var zones = (Zones ?? []).Concat(Tray ?? []).Where(z => z.Pick is not null).ToList();
        int current = zones.FindIndex(z => z.IsSelected);
        if (zones.Count == 0)
            return;
        // No pick among them (it lies outside the schema): the first key picks the first zone, or the last going back.
        CaseZone next = current < 0
            ? zones[step > 0 ? 0 : zones.Count - 1]
            : zones[(current + step + zones.Count) % zones.Count];
        next.Pick?.Execute(null);
        e.Handled = true;
        // The pick rebuilds the zones: move the focus to the new pick once its button exists.
        Dispatcher.UIThread.Post(FocusPick, DispatcherPriority.Loaded);
    }

    private void FocusPick() =>
        this.GetVisualDescendants().OfType<RadioButton>()
            .FirstOrDefault(r => r.DataContext is CaseZone { IsSelected: true })
            ?.Focus(NavigationMethod.Directional);
}
