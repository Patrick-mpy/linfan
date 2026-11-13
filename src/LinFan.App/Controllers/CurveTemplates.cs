// SPDX-License-Identifier: GPL-3.0-or-later

using LinFan.Core.Models;
using LinFan.Core.Services;

namespace LinFan.App.Controllers;

/// <summary>
/// What a <see cref="CurveTemplate"/> stands for: the point table from Core (<see cref="CurvePresets"/>, all meant for
/// linear interpolation and at 100 % by 86 °C) and the menu entry with name and description - the names shared with
/// the onboarding profiles. The single source of the "add curve" menu and of the default curve of a new profile.
/// </summary>
public static class CurveTemplates
{
    /// <summary>The "add curve" menu: the explanatory line, then the templates in the order offered.</summary>
    public static IReadOnlyList<CurveTemplateOption> Menu { get; } = new[]
    {
        new CurveTemplateOption(null, "MainWindow.TemplateMenuHeader"),
        new CurveTemplateOption(CurveTemplate.Silent, "OnboardingCtrl.ProfileSilentName", "OnboardingCtrl.ProfileSilentDesc"),
        new CurveTemplateOption(CurveTemplate.Balanced, "OnboardingCtrl.ProfileBalancedName", "OnboardingCtrl.ProfileBalancedDesc"),
        new CurveTemplateOption(CurveTemplate.Performance, "OnboardingCtrl.ProfilePerformanceName", "OnboardingCtrl.ProfilePerformanceDesc"),
        new CurveTemplateOption(CurveTemplate.Constant40, "MainWindow.TemplateConstant40Name", "MainWindow.TemplateConstant40Desc"),
    };

    public static IReadOnlyList<CurvePoint> Points(CurveTemplate template) => template switch
    {
        CurveTemplate.Silent => CurvePresets.Silent,
        CurveTemplate.Balanced => CurvePresets.Balanced,
        CurveTemplate.Performance => CurvePresets.Performance,
        CurveTemplate.Constant40 => CurvePresets.Constant40,
        _ => throw new ArgumentOutOfRangeException(nameof(template), template, null),
    };

    public static string Name(CurveTemplate template) => Menu.First(o => o.Template == template).Name;
}
