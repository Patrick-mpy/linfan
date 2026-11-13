// SPDX-License-Identifier: GPL-3.0-or-later

namespace LinFan.App.Controllers;

/// <summary>
/// A profile switch that has to be confirmed because the editor holds unsaved changes: everything the dialog says, and
/// why "discard &amp; switch" is not offered. The view answers with <see cref="CurveEditorController.SaveAndSwitchAsync"/>,
/// <see cref="CurveEditorController.DiscardAndSwitch"/> or nothing (cancel).
/// </summary>
/// <param name="DiscardBlockedReason">
/// Set when the target exists only in the draft - discarding would remove it, so there would be nothing to switch to.
/// </param>
public sealed record ProfileSwitchRequest(
    string ProfileId, string Title, string Message, string DiscardLabel, string SaveLabel, string? DiscardBlockedReason);
