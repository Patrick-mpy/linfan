// SPDX-License-Identifier: GPL-3.0-or-later

using LinFan.Core.Models;

namespace LinFan.Core.Services;

/// <summary>
/// Guesses a sensor's <see cref="SensorCategory"/> from its id - the GUI only ever sees the id, never the backend's
/// source path. The backends shape ids differently, and the part that names the hardware sits in a fixed place:
/// <list type="bullet">
/// <item>Linux <c>chip[@bus]/channel</c> - the chip is the hwmon driver name (<c>k10temp</c>, <c>nct6798</c>).</item>
/// <item>Windows the LibreHardwareMonitor identifier - its first segment names the hardware class
///   (<c>/amdcpu/0/…</c>, <c>/gpu-nvidia/0/…</c>, <c>/lpc/nct6797d/0/…</c>, <c>/nvme/0/…</c>).</item>
/// <item>macOS <c>smc/&lt;key&gt;</c> - the SMC key's prefix (<c>TC0P</c> = CPU proximity).</item>
/// </list>
/// The category itself only suggests a sensor group; whatever is not recognised is <see cref="SensorCategory.Other"/>.
/// The CPU/GPU driver lists matter beyond that: <see cref="AirflowTuneService"/> builds its keywords on them to pick
/// the source sensors of the curves it proposes.
/// </summary>
public static class SensorClassifier
{
    private const string SmcPrefix = "smc/";

    // Matched as substrings, so "cpu" also covers cpu_thermal, amdcpu and intelcpu, and "gpu" amdgpu and the
    // gpu-* identifiers. Changing them changes AirflowTuneService's sensor detection too.
    internal static readonly string[] CpuDrivers = ["cpu", "coretemp", "k10temp", "zenpower"];
    internal static readonly string[] GpuDrivers = ["gpu", "radeon", "nvidia", "nouveau", "i915"];

    private static readonly string[] StoragePrefixes = ["nvme", "drivetemp", "hdd"];

    // Board vendors' WMI/EC drivers, the ACPI thermal zone and the ThinkPad EC; on Windows "lpc" is the Super I/O
    // and EC bus. The Super I/O chips themselves (nct6798, it8688) are matched by family below.
    private static readonly string[] MainboardPrefixes = ["acpitz", "asus", "gigabyte_wmi", "thinkpad", "lpc"];
    private static readonly string[] SuperIoFamilies = ["nct", "it"];

    // SMC keys are case-sensitive; the first matching prefix wins. Tp0P is a CPU core on M1 but the power supply on
    // Intel Macs, and Tf (M3) mixes CPU and GPU cores - both stay Other rather than being filed wrongly.
    private static readonly (string Prefix, SensorCategory Category)[] SmcKeys =
    [
        ("TCGC", SensorCategory.Gpu), // integrated graphics
        ("Tp0P", SensorCategory.Other),
        ("TC", SensorCategory.Cpu),
        ("Tp", SensorCategory.Cpu),
        ("Te", SensorCategory.Cpu),
        ("TG", SensorCategory.Gpu),
        ("Tg", SensorCategory.Gpu),
        ("TH", SensorCategory.Storage),
        ("Tm", SensorCategory.Mainboard),
        ("TN", SensorCategory.Mainboard),
        ("TP", SensorCategory.Mainboard),
    ];

    public static SensorCategory Classify(string sensorId)
    {
        ArgumentNullException.ThrowIfNull(sensorId);

        if (sensorId.StartsWith(SmcPrefix, StringComparison.Ordinal))
        {
            string key = sensorId[SmcPrefix.Length..];
            foreach ((string prefix, SensorCategory category) in SmcKeys)
                if (key.StartsWith(prefix, StringComparison.Ordinal))
                    return category;
            return SensorCategory.Other;
        }

        // Windows ids start with the separator, Linux ids with the chip.
        string driver = sensorId.TrimStart('/');
        int end = driver.AsSpan().IndexOfAny('/', '@');
        if (end >= 0)
            driver = driver[..end];

        // GPU before CPU, as in AirflowTuneService.
        if (ContainsAny(driver, GpuDrivers))
            return SensorCategory.Gpu;
        if (ContainsAny(driver, CpuDrivers))
            return SensorCategory.Cpu;
        if (StartsWithAny(driver, StoragePrefixes))
            return SensorCategory.Storage;
        if (StartsWithAny(driver, MainboardPrefixes) || SuperIoFamilies.Any(f => IsChipOfFamily(driver, f)))
            return SensorCategory.Mainboard;
        return SensorCategory.Other;
    }

    private static bool ContainsAny(string s, string[] parts) =>
        parts.Any(p => s.Contains(p, StringComparison.OrdinalIgnoreCase));

    private static bool StartsWithAny(string s, string[] prefixes) =>
        prefixes.Any(p => s.StartsWith(p, StringComparison.OrdinalIgnoreCase));

    /// <summary>The family name followed by a model number: <c>nct6798</c>, <c>it8688</c> - but not <c>iwlwifi_1</c>.</summary>
    private static bool IsChipOfFamily(string driver, string family) =>
        driver.Length > family.Length
        && driver.StartsWith(family, StringComparison.OrdinalIgnoreCase)
        && char.IsAsciiDigit(driver[family.Length]);
}
