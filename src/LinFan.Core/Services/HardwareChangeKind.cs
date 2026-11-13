// SPDX-License-Identifier: GPL-3.0-or-later

namespace LinFan.Core.Services;

/// <summary>What changed about the hardware behind configured ids (<see cref="HardwareChange"/>).</summary>
public enum HardwareChangeKind
{
    /// <summary>Another device answers to the ids: the recorded fingerprint and the current one differ.</summary>
    Replaced,

    /// <summary>The ids are not detected at all.</summary>
    Missing,
}
