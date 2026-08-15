// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using System.Runtime.CompilerServices;
using LinFan.App.Localization;
using LinFan.App.Services;

namespace LinFan.App.UiTests;

// Die Headless-UI-Tests prüfen sichtbaren deutschen Text im echten XAML. Da die GUI ihre Texte über
// den Localizer auflöst, wird die Kultur für die Test-Assembly auf Deutsch gepinnt - unabhängig von
// der OS-Kultur des Build-Hosts (englischer CI ⇒ sonst rote Tests).
internal static class TestCulture
{
    // ModuleInitializer ist hier bewusst: die Kultur muss stehen, bevor irgendein Test der Assembly läuft.
#pragma warning disable CA2255
    [ModuleInitializer]
    public static void PinGerman()
    {
        CultureInfo.DefaultThreadCurrentUICulture = new CultureInfo("de");
        Localizer.Instance.SetLanguage(LanguageChoice.German);
    }
#pragma warning restore CA2255
}
