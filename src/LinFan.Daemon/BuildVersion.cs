// SPDX-License-Identifier: GPL-3.0-or-later

using System.Reflection;

namespace LinFan.Daemon;

/// <summary>
/// The daemon's informational version, <c>X.Y.Z[-pre]+&lt;commit&gt;</c>: <c>Directory.Build.props</c>, or the release
/// tag the pipeline passes as <c>-p:Version</c>. It goes into every snapshot and the start log.
/// </summary>
internal static class BuildVersion
{
    public static string Current { get; } =
        typeof(BuildVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? "0.0.0";
}
