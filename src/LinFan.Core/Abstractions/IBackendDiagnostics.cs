// SPDX-License-Identifier: GPL-3.0-or-later

namespace LinFan.Core.Abstractions;

/// <summary>
/// Optionale Start-Diagnose eines Hardware-Backends: ein nicht-fataler Hinweis, der bei der
/// Discovery auffiel (z. B. „nur GPU-Sensoren gefunden" → Verdacht auf einen Treiber-Konflikt mit
/// einem anderen Monitoring-/Lüftertool). Rein informativ - ein unauffälliges Backend liefert
/// <c>null</c>. Plattformspezifisch befüllt (nur dort, wo es solche Fälle gibt); Daemon und CLI
/// werten das <b>opt-in</b> per Pattern-Match aus, damit keine Plattform-Logik in die neutralen
/// Schichten leckt.
/// </summary>
public interface IBackendDiagnostics
{
    /// <summary>
    /// Beim Öffnen des Backends erkannter Hinweis, oder <c>null</c>, wenn unauffällig. Eine
    /// <b>Momentaufnahme vom Start</b> - kein Live-Monitor: ein erst später auftretender Konflikt
    /// wird nicht erfasst, und der Hinweis wird nicht zurückgenommen, wenn er sich auflöst.
    /// </summary>
    string? StartupWarning { get; }
}
