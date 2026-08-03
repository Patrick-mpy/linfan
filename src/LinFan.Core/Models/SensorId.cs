// SPDX-License-Identifier: GPL-3.0-or-later

namespace LinFan.Core.Models;

/// <summary>Stabiler Bezeichner eines Sensorkanals (backend-spezifisch kodiert, z. B. <c>hwmon7/temp1</c>).</summary>
public readonly record struct SensorId(string Value)
{
    public override string ToString() => Value;
}
