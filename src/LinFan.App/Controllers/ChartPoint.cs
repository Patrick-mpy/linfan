// SPDX-License-Identifier: GPL-3.0-or-later

namespace LinFan.App.Controllers;

/// <summary>
/// A curve position picked on the <see cref="Controls.CurveChart"/> (°C → %), both rounded to whole numbers:
/// the parameter of <c>CurveChart.AddPointCommand</c>. The chart does not check it against the existing points -
/// whether it may be added (point limit, temperature already taken) is the command's <c>CanExecute</c>.
/// </summary>
public readonly record struct ChartPoint(decimal Temperature, decimal Percent);
