// SPDX-License-Identifier: GPL-3.0-or-later

namespace LinFan.Ipc.Messages;

/// <summary>Ein Sensorwert über die IPC-Grenze (nur primitive, serialisierbare Felder).</summary>
public sealed record IpcSensor(string Id, string Name, string Kind, string Unit, double Value);
