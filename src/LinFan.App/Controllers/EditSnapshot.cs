// SPDX-License-Identifier: GPL-3.0-or-later

namespace LinFan.App.Controllers;

/// <summary>
/// One undo/redo step: the editor's whole configuration as JSON (the output of its config build, so it compares
/// by plain string equality) plus the selection to put back.
/// </summary>
public sealed record EditSnapshot(string Config, EditSelection Selection);
