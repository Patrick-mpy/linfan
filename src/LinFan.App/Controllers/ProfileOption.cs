// SPDX-License-Identifier: GPL-3.0-or-later

namespace LinFan.App.Controllers;

/// <summary>Anzeigeoption für ein Onboarding-Profil (Id + Anzeigename + kurze Beschreibung).</summary>
public sealed record ProfileOption(string Id, string DisplayName, string Description)
{
    public override string ToString() => DisplayName;
}
