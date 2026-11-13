// SPDX-License-Identifier: GPL-3.0-or-later

using Avalonia.Media;

namespace LinFan.App.Services;

/// <summary>
/// The accent colours of one theme, all derived together (<see cref="AccentPalette.Derive"/>) - they are the values of
/// the accent tokens in Theme/SemanticColors.axaml.
/// </summary>
/// <param name="Accent">Lines, text, selection marks (<c>Accent</c>, <c>AccentColor</c>).</param>
/// <param name="Fill">Filled buttons (<c>AccentFill</c>); <paramref name="OnAccent"/> reads on it.</param>
/// <param name="Hover">Filled button under the pointer (<c>AccentHover</c>).</param>
/// <param name="Pressed">Filled button while pressed (<c>AccentPressed</c>).</param>
/// <param name="Shade">One step darker than <paramref name="Accent"/> - the brand mark's blade shading (<c>AccentShadeColor</c>).</param>
/// <param name="Clamp">Clamp lines of the curve chart (<c>ClampColor</c>).</param>
/// <param name="Tint">Faint accent wash, with alpha (<c>AccentTint</c>).</param>
/// <param name="OnAccent">Text and icons on <paramref name="Fill"/> (<c>OnAccent</c>).</param>
public sealed record AccentTokens(
    Color Accent,
    Color Fill,
    Color Hover,
    Color Pressed,
    Color Shade,
    Color Clamp,
    Color Tint,
    Color OnAccent);
