// SPDX-License-Identifier: GPL-3.0-or-later

using LinFan.Core.Services;

namespace LinFan.Core.Models;

/// <summary>
/// The physical device behind a channel as the backend sees it: a Linux hwmon chip, a LibreHardwareMonitor hardware
/// node, the SMC. Lets a device swapped in behind the same channel id be noticed.
/// <para>
/// Its text comes from drivers, libraries or a hand-edited config and reaches logs, the config and every GUI, so the
/// only constructor - the one the config's JSON goes through as well - cleans it: control and format characters are
/// stripped (<see cref="UntrustedText"/>), each value is trimmed and cut at <see cref="MaxLength"/>. Every instance is in
/// that form, so two that describe the same device compare equal, and none can push a config past
/// <see cref="ConfigLimits"/>. Never throws.
/// </para>
/// </summary>
public sealed record HardwareDevice
{
    /// <summary>Longest name or fingerprint a device keeps; the rest is cut off.</summary>
    public const int MaxLength = 128;

    /// <param name="name">The device's name as the backend reports it; <c>null</c> or empty after cleaning gives "".</param>
    /// <param name="fingerprint">See <see cref="Fingerprint"/>; empty after cleaning gives <c>null</c>.</param>
    public HardwareDevice(string? name, string? fingerprint)
    {
        Name = Clean(name) ?? "";
        Fingerprint = Clean(fingerprint);
    }

    /// <summary>The device's name as the backend reports it (hwmon chip name, LHM hardware name, <c>SMC</c>).</summary>
    public string Name { get; }

    /// <summary>
    /// Identifies the physical device behind the entry - its model, not the single unit - best effort: another
    /// fingerprint behind the same id means another device, an equal one proves nothing. <c>null</c> = the backend cannot
    /// tell. Never contains a serial number.
    /// </summary>
    public string? Fingerprint { get; }

    private static string? Clean(string? text)
    {
        if (text is null)
            return null;
        string clean = UntrustedText.Strip(text).Trim();
        if (clean.Length > MaxLength)
        {
            // Never cut a surrogate pair in half: the lone high surrogate would be unsafe text again.
            int length = char.IsHighSurrogate(clean[MaxLength - 1]) ? MaxLength - 1 : MaxLength;
            clean = clean[..length].TrimEnd();
        }
        return clean.Length == 0 ? null : clean;
    }
}
