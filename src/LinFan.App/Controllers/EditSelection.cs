// SPDX-License-Identifier: GPL-3.0-or-later

namespace LinFan.App.Controllers;

/// <summary>
/// Where an edit happened: the profile and curve open in the curve editor and which of its two panes was shown.
/// Stored with every history step, so undo brings the user back to the place of the change.
/// </summary>
public sealed record EditSelection(string? ProfileId, string? CurveId, CurveTabPane Pane);
