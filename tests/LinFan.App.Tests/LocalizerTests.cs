// SPDX-License-Identifier: GPL-3.0-or-later

using LinFan.App.Localization;
using LinFan.App.Services;

namespace LinFan.App.Tests;

/// <summary>
/// Verhalten des <see cref="Localizer"/>: bekannte Keys lösen auf, unbekannte fallen sichtbar auf den
/// Key selbst zurück, und <see cref="Localizer.SetLanguage"/> schaltet die Auflösung live zwischen
/// Deutsch und Englisch um. Mutiert das prozessweite Singleton — stellt deshalb am Ende die für die
/// übrigen Tests gepinnte deutsche Kultur wieder her (siehe TestCulture; Parallelität ist deaktiviert).
/// </summary>
public sealed class LocalizerTests : IDisposable
{
    public void Dispose() => Localizer.Instance.SetLanguage(LanguageChoice.German);

    [Fact]
    public void KnownKey_ResolvesToGerman()
    {
        Localizer.Instance.SetLanguage(LanguageChoice.German);
        Assert.Equal("Übernehmen", Localizer.Instance["MainWindow.Apply"]);
    }

    [Fact]
    public void UnknownKey_FallsBackToKeyItself()
    {
        const string key = "Does.Not.Exist";
        Assert.Equal(key, Localizer.Instance[key]);
    }

    [Fact]
    public void SetLanguage_SwitchesRepresentativeLookupBetweenGermanAndEnglish()
    {
        Localizer.Instance.SetLanguage(LanguageChoice.German);
        Assert.Equal("Übernehmen", Localizer.Instance["MainWindow.Apply"]);

        Localizer.Instance.SetLanguage(LanguageChoice.English);
        Assert.Equal("Apply", Localizer.Instance["MainWindow.Apply"]);
    }

    [Fact]
    public void Format_AppliesArgumentsInActiveCulture()
    {
        Localizer.Instance.SetLanguage(LanguageChoice.German);
        Assert.Equal("max 80", Localizer.Instance.Format("MainCtrl.MaxTemp", 80));
    }
}
