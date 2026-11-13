// SPDX-License-Identifier: GPL-3.0-or-later

using LinFan.Core.Models;

namespace LinFan.Core.Services;

/// <summary>
/// One speed sensor, one fan: the sensor a fan's speed is read from. Its explicit coupling
/// (<see cref="FanConfig.RpmSource"/>) wins; without one the backend's pairing counts - unless another fan is coupled to
/// that sensor explicitly, which says the pairing is wrong: the fan would show, and calibrate with, the other one's speed.
/// Indexes the couplings once, so a snapshot resolves every fan without searching the config per fan.
/// </summary>
public sealed class FanTachometers
{
    private readonly Dictionary<string, SensorId> _coupled = new(StringComparer.Ordinal);
    private readonly HashSet<string> _coupledSensors = new(StringComparer.Ordinal);

    public FanTachometers(IEnumerable<FanConfig> fans)
    {
        ArgumentNullException.ThrowIfNull(fans);
        foreach (FanConfig fan in fans)
        {
            if (string.IsNullOrWhiteSpace(fan.RpmSource))
                continue;
            _coupled.TryAdd(fan.FanId, new SensorId(fan.RpmSource));
            _coupledSensors.Add(fan.RpmSource);
        }
    }

    /// <summary>Whether the fan has a coupling of its own (else its sensor, if any, is the backend's pairing).</summary>
    public bool HasOwnCoupling(FanDescriptor fan) => _coupled.ContainsKey(fan.Id.Value);

    /// <summary>
    /// The fan's speed sensor, or <c>null</c> without one. An explicit coupling counts even while its sensor is not
    /// detected: the backend's pairing is no stand-in for a sensor the user chose instead of it.
    /// </summary>
    public SensorId? Of(FanDescriptor fan)
    {
        ArgumentNullException.ThrowIfNull(fan);
        if (_coupled.TryGetValue(fan.Id.Value, out SensorId own))
            return own;
        // This fan has no coupling of its own, so a coupled sensor here is another fan's.
        return fan.Tachometer is { } paired && !_coupledSensors.Contains(paired.Value) ? paired : null;
    }
}
