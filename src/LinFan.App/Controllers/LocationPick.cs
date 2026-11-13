// SPDX-License-Identifier: GPL-3.0-or-later

using LinFan.Core.Models;

namespace LinFan.App.Controllers;

/// <summary>What „Position wählen" returns: the fan's position and the case type it was picked in (both apply together).</summary>
public sealed record LocationPick(FanLocation Location, CaseType CaseType);
