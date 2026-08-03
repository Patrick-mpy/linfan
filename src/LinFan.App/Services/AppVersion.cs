// SPDX-License-Identifier: GPL-3.0-or-later

using System.Reflection;

namespace LinFan.App.Services;

/// <summary>
/// Liest die laufende App-Version aus <see cref="AssemblyInformationalVersionAttribute"/> („X.Y.Z[-pre]+&lt;sha&gt;",
/// zentral über <c>Directory.Build.props</c> gesetzt). <see cref="SemVer.TryParse"/> verwirft den <c>+sha</c>-Teil.
/// </summary>
public static class AppVersion
{
    /// <summary>Roh-String der informational version, oder <c>"0.0.0"</c>, falls nicht ermittelbar.</summary>
    public static string InformationalRaw =>
        Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? "0.0.0";

    /// <summary>Aktuelle Version als <see cref="SemVer"/>; <c>false</c>, wenn sie sich nicht parsen lässt.</summary>
    public static bool TryCurrent(out SemVer version) => SemVer.TryParse(InformationalRaw, out version);
}
