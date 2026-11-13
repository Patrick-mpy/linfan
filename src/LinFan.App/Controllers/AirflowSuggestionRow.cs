// SPDX-License-Identifier: GPL-3.0-or-later

namespace LinFan.App.Controllers;

/// <summary>
/// One line of the setup assistant's airflow summary (read-only): a fan, its position and the curve its role gets.
/// </summary>
public sealed record AirflowSuggestionRow(string FanId, string FanName, string LocationDisplay, string CurveName, string Reason);
