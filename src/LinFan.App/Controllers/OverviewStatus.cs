// SPDX-License-Identifier: GPL-3.0-or-later

using LinFan.App.Localization;

namespace LinFan.App.Controllers;

/// <summary>
/// The status tile: "OK" while nothing needs a look (info hints included - the second line then names the first one),
/// "1 Hinweis"/"N Hinweise" when the heaviest hint is a notice, "Kritisch" when one is critical. N is
/// <see cref="AttentionCount"/>, the same count the header's badge on the overview tab shows. The second line names the
/// most important hint and how many more there are (infos included); with no hints at all it says that temperatures and
/// fans are within range.
/// </summary>
/// <param name="AttentionCount">The hints that need a look: notices and critical ones, not plain infos.</param>
public sealed record OverviewStatus(OverviewLevel Level, string Headline, string Detail, string? Tooltip, int AttentionCount = 0)
{
    /// <summary>The tile's accessible name: caption, headline and detail.</summary>
    public string Name => Localizer.Instance.Format("Overview.TileName", Localizer.Instance["Overview.Status"], Headline, Detail);

    /// <param name="hints">Sorted heaviest first (as <see cref="OverviewHints.Build"/> returns them).</param>
    public static OverviewStatus From(IReadOnlyList<OverviewHint> hints)
    {
        Localizer l = Localizer.Instance;
        if (hints.Count == 0)
            return new OverviewStatus(OverviewLevel.Ok, l["Overview.StatusOk"], l["Overview.StatusAllWithinRange"], null);

        int attention = hints.Count(h => h.Severity >= HintSeverity.Notice);
        string detail = hints.Count == 1
            ? hints[0].Text
            : l.Format("Overview.StatusMore", hints[0].Text, hints.Count - 1);
        string tooltip = l["Overview.StatusTooltip"];
        return hints[0].Severity switch
        {
            HintSeverity.Critical => new OverviewStatus(OverviewLevel.Critical, l["Overview.StatusCritical"], detail, tooltip, attention),
            HintSeverity.Notice => new OverviewStatus(OverviewLevel.Notice,
                attention == 1 ? l["Overview.StatusNoticeOne"] : l.Format("Overview.StatusNotices", attention), detail, tooltip,
                attention),
            _ => new OverviewStatus(OverviewLevel.Ok, l["Overview.StatusOk"], detail, tooltip),
        };
    }
}
