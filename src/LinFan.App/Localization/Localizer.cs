// SPDX-License-Identifier: GPL-3.0-or-later

using System.ComponentModel;
using System.Globalization;
using System.Resources;
using LinFan.App.Services;

namespace LinFan.App.Localization;

/// <summary>
/// Beobachtbarer Zugriff auf die übersetzten UI-Strings (<c>Resources/Strings*.resx</c> →
/// Satellite-Assemblies). Singleton, weil alle <c>{l:Tr}</c>-Bindings dieselbe Quelle teilen und
/// ein Sprachwechsel global gilt. Spiegelt für die Sprache, was <c>ThemeVariantMap</c>/
/// <c>App.ApplyTheme</c> fürs Theme tun: die Auswahl lebt in <see cref="UiSettings"/>/
/// <c>SettingsController</c>, das eigentliche Anwenden hier.
/// </summary>
public sealed class Localizer : INotifyPropertyChanged
{
    // Avalonias ReflectionIndexerNode.ShouldUpdate sucht die Indexer-Property per Reflexion über den
    // gemeldeten PropertyChanged-Namen; der Default-Reflexionsname eines C#-Indexers ist "Item". Nur
    // dieser Name lässt alle [key]-Bindings neu lesen (null/leer greift bei Avalonia hier NICHT).
    private const string IndexerName = "Item";

    private static readonly ResourceManager Resources =
        new("LinFan.App.Resources.Strings", typeof(Localizer).Assembly);

    public static Localizer Instance { get; } = new();

    private CultureInfo _culture = CultureInfo.CurrentUICulture;

    private Localizer() { }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Übersetzt einen Key; fehlt er, bleibt der Key selbst sichtbar (statt leer) als Hinweis.</summary>
    public string this[string key] => Resources.GetString(key, _culture) ?? key;

    /// <summary>Wie der Indexer, aber mit <see cref="string.Format(IFormatProvider, string, object?[])"/> für <c>{0}</c>-Platzhalter.</summary>
    public string Format(string key, params object?[] args) => string.Format(_culture, this[key], args);

    /// <summary>
    /// Setzt die aktive Sprache und benachrichtigt alle Bindings live. <see cref="LanguageChoice.System"/>
    /// wird aus <see cref="CultureInfo.InstalledUICulture"/> aufgelöst (<c>de*</c> → Deutsch, sonst Englisch).
    /// </summary>
    public void SetLanguage(LanguageChoice choice)
    {
        _culture = Resolve(choice);
        CultureInfo.DefaultThreadCurrentUICulture = _culture;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(IndexerName));
    }

    private static CultureInfo Resolve(LanguageChoice choice) => choice switch
    {
        LanguageChoice.German => new CultureInfo("de"),
        LanguageChoice.English => new CultureInfo("en"),
        _ => CultureInfo.InstalledUICulture.TwoLetterISOLanguageName == "de"
            ? new CultureInfo("de")
            : new CultureInfo("en"),
    };
}
