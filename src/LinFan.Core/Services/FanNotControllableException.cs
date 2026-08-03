// SPDX-License-Identifier: GPL-3.0-or-later

namespace LinFan.Core.Services;

/// <summary>
/// Eine Kalibrierung ist nicht möglich, weil der Lüfter nicht steuerbar ist (z. B. ohne erhöhte
/// Rechte). Typisiert (statt generischer <see cref="NotSupportedException"/> mit deutschem Text), damit
/// der Daemon die Ursache codifiziert über IPC überträgt und die GUI sie lokalisiert.
/// </summary>
public sealed class FanNotControllableException : Exception
{
    public FanNotControllableException(string message) : base(message) { }
}
