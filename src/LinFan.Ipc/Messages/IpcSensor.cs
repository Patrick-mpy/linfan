// SPDX-License-Identifier: GPL-3.0-or-later

namespace LinFan.Ipc.Messages;

/// <summary>Ein Sensorwert über die IPC-Grenze (nur primitive, serialisierbare Felder).</summary>
/// <param name="Name">The name to show: the user's own, or else the hardware label.</param>
/// <param name="Label">The hardware label, also while an own name is set - what resetting the name brings back.
/// <c>null</c> = older daemon.</param>
public sealed record IpcSensor(string Id, string Name, string Kind, string Unit, double Value, string? Label = null);
