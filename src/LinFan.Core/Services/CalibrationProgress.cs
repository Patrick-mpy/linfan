// SPDX-License-Identifier: GPL-3.0-or-later

namespace LinFan.Core.Services;

/// <summary>
/// Fortschrittsmeldung während der Kalibrier-Rampe (für die Live-Anzeige in der GUI). Trägt nur die
/// Messwerte; die Phase ist während der Rampe stets „Messen", den Prozentwert leitet die GUI aus
/// <see cref="Pwm"/> ab (pwm·100/255) - ein vorformatierter Anzeigetext gehört nicht in die Domain.
/// </summary>
public sealed record CalibrationProgress(int Pwm, int Rpm);
