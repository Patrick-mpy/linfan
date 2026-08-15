// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using System.Runtime.CompilerServices;
using LinFan.App.Localization;
using LinFan.App.Services;

// Die bestehenden App-Tests assertieren auf deutsche UI-Strings. Da die GUI seit der i18n-Umstellung
// ihre Texte über den Localizer auflöst, wird die Kultur für die gesamte Test-Assembly auf Deutsch
// gepinnt - sonst hinge das Ergebnis von der OS-Kultur des Build-Hosts ab (englischer CI ⇒ rote Tests).
//
// Parallelität ist bewusst deaktiviert: der Localizer ist ein prozessweites Singleton, und
// LocalizerTests schaltet die Sprache kurzzeitig auf Englisch. Liefe das parallel zu Tests, die
// deutschen Text erwarten, bräche es sporadisch.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace LinFan.App.Tests;

internal static class TestCulture
{
    [ModuleInitializer]
    public static void PinGerman()
    {
        CultureInfo.DefaultThreadCurrentUICulture = new CultureInfo("de");
        Localizer.Instance.SetLanguage(LanguageChoice.German);
    }
}
