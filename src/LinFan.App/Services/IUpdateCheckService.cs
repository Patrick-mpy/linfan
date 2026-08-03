// SPDX-License-Identifier: GPL-3.0-or-later

namespace LinFan.App.Services;

/// <summary>
/// Fragt die neueste veröffentlichte Version ab und meldet sie, wenn sie neuer als <paramref name="current"/> ist.
/// Best-effort: jeder Fehler (offline, Rate-Limit, noch kein Release) ergibt <c>null</c> — der Aufrufer zeigt
/// dann einfach kein Banner. Hinter einem Interface, damit der Controller im Test gemockt werden kann.
/// </summary>
public interface IUpdateCheckService
{
    Task<UpdateInfo?> CheckAsync(SemVer current, CancellationToken ct = default);
}
