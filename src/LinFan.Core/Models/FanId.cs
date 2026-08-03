// SPDX-License-Identifier: GPL-3.0-or-later

namespace LinFan.Core.Models;

/// <summary>Stabiler Bezeichner eines steuerbaren Lüfters (backend-spezifisch kodiert, z. B. <c>hwmon7/pwm1</c>).</summary>
public readonly record struct FanId(string Value)
{
    public override string ToString() => Value;
}
