// SPDX-License-Identifier: GPL-3.0-or-later

using Avalonia;
using Avalonia.Controls;

namespace LinFan.App.Controls;

/// <summary>
/// Wiederverwendbare Kalibrier-Statusanzeige: Kopfzeile, Fortschrittsbalken und Detailzeile als ein
/// einheitliches Bild. Reine View-Mechanik - der Aufrufer speist die Werte über die StyledProperties und
/// stellt den umgebenden Container (z. B. InfoBg-Border, Abbrechen-Button) bereit.
/// </summary>
public partial class CalibrationCard : UserControl
{
    /// <summary>Kopfzeile, z. B. „Kalibriere Lüfter 2 von 5: CPU Fan". Leer → ausgeblendet.</summary>
    public static readonly StyledProperty<string?> HeadlineProperty =
        AvaloniaProperty.Register<CalibrationCard, string?>(nameof(Headline));

    /// <summary>Detailzeile (z. B. Phase · PWM · RPM bzw. Abschlussmeldung). Leer → ausgeblendet.</summary>
    public static readonly StyledProperty<string?> DetailProperty =
        AvaloniaProperty.Register<CalibrationCard, string?>(nameof(Detail));

    /// <summary>Fortschritt in Prozent (0..100) für den Balken.</summary>
    public static readonly StyledProperty<double> ProgressProperty =
        AvaloniaProperty.Register<CalibrationCard, double>(nameof(Progress));

    /// <summary>Ob der Fortschrittsbalken angezeigt wird (nur während eines laufenden Vorgangs).</summary>
    public static readonly StyledProperty<bool> ShowProgressProperty =
        AvaloniaProperty.Register<CalibrationCard, bool>(nameof(ShowProgress));

    public string? Headline
    {
        get => GetValue(HeadlineProperty);
        set => SetValue(HeadlineProperty, value);
    }

    public string? Detail
    {
        get => GetValue(DetailProperty);
        set => SetValue(DetailProperty, value);
    }

    public double Progress
    {
        get => GetValue(ProgressProperty);
        set => SetValue(ProgressProperty, value);
    }

    public bool ShowProgress
    {
        get => GetValue(ShowProgressProperty);
        set => SetValue(ShowProgressProperty, value);
    }

    public CalibrationCard() => InitializeComponent();
}
