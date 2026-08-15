// SPDX-License-Identifier: GPL-3.0-or-later

using LinFan.App.Localization;
using LinFan.App.Services;

namespace LinFan.App.Tests;

/// <summary>
/// Verhalten des <see cref="Localizer"/>: bekannte Keys lösen auf, unbekannte fallen sichtbar auf den
/// Key selbst zurück, und <see cref="Localizer.SetLanguage"/> schaltet die Auflösung live zwischen
/// Deutsch und Englisch um. Mutiert das prozessweite Singleton - stellt deshalb am Ende die für die
/// übrigen Tests gepinnte deutsche Kultur wieder her (siehe TestCulture; Parallelität ist deaktiviert).
/// </summary>
public sealed class LocalizerTests : IDisposable
{
    public void Dispose() => Localizer.Instance.SetLanguage(LanguageChoice.German);

    [Fact]
    public void KnownKey_ResolvesToGerman()
    {
        Localizer.Instance.SetLanguage(LanguageChoice.German);
        Assert.Equal("Verwerfen", Localizer.Instance["MainWindow.Revert"]);
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
        Assert.Equal("Verwerfen", Localizer.Instance["MainWindow.Revert"]);

        Localizer.Instance.SetLanguage(LanguageChoice.English);
        Assert.Equal("Discard", Localizer.Instance["MainWindow.Revert"]);
    }

    [Fact]
    public void Format_AppliesArgumentsInActiveCulture()
    {
        Localizer.Instance.SetLanguage(LanguageChoice.German);
        Assert.Equal("max 80", Localizer.Instance.Format("MainCtrl.MaxTemp", 80));
    }

    [Fact]
    public void UngroupedLabel_FollowsLanguage()
    {
        // Was a hardcoded German const before - the fallback group label must switch with the language.
        Localizer.Instance.SetLanguage(LanguageChoice.German);
        Assert.Equal("Ungruppiert", Controllers.FanGroup.Ungrouped);
        Assert.Equal("Ungruppiert", Controllers.SensorGroup.Ungrouped);

        Localizer.Instance.SetLanguage(LanguageChoice.English);
        Assert.Equal("Ungrouped", Controllers.FanGroup.Ungrouped);
        Assert.Equal("Ungrouped", Controllers.SensorGroup.Ungrouped);
    }

    [Fact]
    public void ThemeOptions_FollowLanguage()
    {
        // Were hardcoded German ("Hell"/"Dunkel") before - they showed up in the English UI.
        Localizer.Instance.SetLanguage(LanguageChoice.German);
        Assert.Equal("Hell", Controllers.ThemeOption.For(ThemeChoice.Light).Display);
        Assert.Equal("Dunkel", Controllers.ThemeOption.For(ThemeChoice.Dark).Display);

        Localizer.Instance.SetLanguage(LanguageChoice.English);
        Assert.Equal("Light", Controllers.ThemeOption.For(ThemeChoice.Light).Display);
        Assert.Equal("Dark", Controllers.ThemeOption.For(ThemeChoice.Dark).Display);
    }

    [Fact]
    public void ThemeOption_For_MatchesAFreshlyBuiltList_ByValueEquality()
    {
        // The ComboBox finds its selection in ItemsSource by record equality - that must hold for a
        // list built after a language switch, otherwise the selection would silently clear.
        Localizer.Instance.SetLanguage(LanguageChoice.English);
        Assert.Contains(Controllers.ThemeOption.For(ThemeChoice.Dark), Controllers.ThemeOption.Build());
    }
}
