// SPDX-License-Identifier: GPL-3.0-or-later

namespace LinFan.Core.Services;

/// <summary>
/// Why the daemon runs without the configuration it found at start (until the next saved change). Sent across IPC by
/// member name, like <see cref="FailSafeState"/>.
/// </summary>
public enum ConfigLoadProblem
{
    /// <summary>The file could not be read or used and is still in place - the next saved change overwrites it.</summary>
    NotLoaded,

    /// <summary>The file was unusable and was moved aside (<c>config.json.invalid…</c>); the daemon started fresh.</summary>
    SetAside,
}
