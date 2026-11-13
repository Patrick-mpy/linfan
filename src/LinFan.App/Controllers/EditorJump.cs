// SPDX-License-Identifier: GPL-3.0-or-later

namespace LinFan.App.Controllers;

/// <summary>Where a link from the settings leads in the curves tab: a profile, and optionally one of its curves.</summary>
public sealed record EditorJump(string ProfileId, string? CurveId = null);
