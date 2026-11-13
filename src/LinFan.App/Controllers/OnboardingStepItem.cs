// SPDX-License-Identifier: GPL-3.0-or-later

namespace LinFan.App.Controllers;

/// <summary>
/// One segment of the wizard's step indicator ("2 · Devices"). Immutable: the controller rebuilds the whole
/// indicator whenever the step or the language changes.
/// </summary>
public sealed record OnboardingStepItem(string Label, bool IsDone, bool IsCurrent);
