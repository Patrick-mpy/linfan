// SPDX-License-Identifier: GPL-3.0-or-later

namespace LinFan.App.Services;

/// <summary>Ergebnis eines erfolgreichen Update-Checks: eine neuere Version steht bereit.</summary>
/// <param name="LatestVersion">Anzeigename der neuesten Version (z. B. „0.2.0").</param>
/// <param name="ReleaseUrl">Zielseite der Release (GitHub <c>html_url</c>) zum Öffnen im Browser.</param>
public sealed record UpdateInfo(string LatestVersion, string ReleaseUrl);
