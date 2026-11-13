// SPDX-License-Identifier: GPL-3.0-or-later

using LinFan.App.Localization;
using LinFan.Core.Models;

namespace LinFan.App.Controllers;

/// <summary>
/// An entry of the "add curve" menu: a template with its name, description and preview points - or, with
/// <see cref="Template"/> <c>null</c>, the explanatory line on top (a menu has a single item list, so that line is an
/// entry as well; it is not selectable).
/// </summary>
public sealed record CurveTemplateOption(CurveTemplate? Template, string NameKey, string? DescriptionKey = null)
{
    public bool IsTemplate => Template is not null;

    /// <summary>Looked up on every read, so a language switch shows the next time the menu opens.</summary>
    public string Name => Localizer.Instance[NameKey];

    public string Description => DescriptionKey is null ? "" : Localizer.Instance[DescriptionKey];

    /// <summary>The template's points (Core presets) - for the menu's mini preview; none for the header line.</summary>
    public IReadOnlyList<CurvePoint> Points => Template is { } template ? CurveTemplates.Points(template) : Array.Empty<CurvePoint>();

    public override string ToString() => Name;
}
