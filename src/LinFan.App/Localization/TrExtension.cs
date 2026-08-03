// SPDX-License-Identifier: GPL-3.0-or-later

using Avalonia.Data;
using Avalonia.Markup.Xaml;

namespace LinFan.App.Localization;

/// <summary>
/// XAML-Markup-Extension <c>{l:Tr Key}</c>: liefert ein Reflection-<see cref="Binding"/> auf
/// <see cref="Localizer"/><c>[Key]</c> (OneWay) mit <b>explizit gesetzter</b> Source. Dadurch ist
/// die Bindung unabhängig vom DataContext und von <c>AvaloniaUseCompiledBindingsByDefault</c>.
/// Bei <see cref="Localizer.SetLanguage"/> aktualisieren sich alle so gebundenen Texte live.
/// </summary>
public sealed class TrExtension : MarkupExtension
{
    public TrExtension()
    {
    }

    public TrExtension(string key) => Key = key;

    /// <summary>Bereichs-präfigierter Punkt-Key, z. B. <c>Header.Theme</c>.</summary>
    public string Key { get; set; } = string.Empty;

    public override object ProvideValue(IServiceProvider serviceProvider) => new Binding
    {
        Source = Localizer.Instance,
        Path = $"[{Key}]",
        Mode = BindingMode.OneWay,
    };
}
