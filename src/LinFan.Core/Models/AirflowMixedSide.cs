// SPDX-License-Identifier: GPL-3.0-or-later

namespace LinFan.Core.Models;

/// <summary>
/// One side of the case with intake and exhaust fans at once - allowed, but part of the fresh air goes straight out
/// again. The pressure balance counts each fan by its own direction all the same.
/// </summary>
public sealed record AirflowMixedSide(CaseSide Side, IReadOnlyList<string> IntakeFanIds, IReadOnlyList<string> ExhaustFanIds);
