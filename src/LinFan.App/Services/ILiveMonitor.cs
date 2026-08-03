// SPDX-License-Identifier: GPL-3.0-or-later

namespace LinFan.App.Services;

/// <summary>
/// Liefert Live-Momentaufnahmen der Hardware für die GUI. In Phase 1 als In-Process-Implementierung
/// (Prototyp); in Teil 3 ersetzt eine IPC-Variante diese, ohne dass der Controller sich ändert.
/// </summary>
public interface ILiveMonitor
{
    MonitorSnapshot Read();
}
