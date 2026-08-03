// SPDX-License-Identifier: GPL-3.0-or-later

using System.Reflection;
using LinFan.App.Localization;

namespace LinFan.App.Tests;

/// <summary>
/// Test-Helfer: liest die Anzahl der aktuell am <see cref="Localizer"/>-Singleton hängenden
/// <c>PropertyChanged</c>-Handler über das vom Compiler erzeugte Backing-Field des field-like Events.
/// Grundlage der Regressionstests gegen die Localizer-Event-Leaks (Onboarding-/Settings-Controller).
/// Parallelität ist assembly-weit deaktiviert (TestCulture), daher ist die Zählung stabil.
/// </summary>
internal static class LocalizerProbe
{
    public static int SubscriberCount()
    {
        FieldInfo field = typeof(Localizer).GetField(
                              "PropertyChanged", BindingFlags.NonPublic | BindingFlags.Instance)
                          ?? throw new InvalidOperationException("Localizer.PropertyChanged Backing-Field nicht gefunden");
        var handler = (Delegate?)field.GetValue(Localizer.Instance);
        return handler?.GetInvocationList().Length ?? 0;
    }
}
