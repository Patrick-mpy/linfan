// SPDX-License-Identifier: GPL-3.0-or-later

namespace LinFan.Ipc.Messages;

/// <summary>
/// A device whose configured entries no longer match the hardware, as the daemon reports it in
/// <see cref="IpcSnapshot.HardwareChanges"/>. The GUI resolves it with <see cref="IpcCommand.AcceptHardware"/> or
/// <see cref="IpcCommand.ForgetHardware"/>, passing <paramref name="Key"/> as the target.
/// <para>
/// <paramref name="Kind"/> is the name of a <c>LinFan.Core.Services.HardwareChangeKind</c> member (<c>Replaced</c>:
/// another device answers to the ids; <c>Missing</c>: the ids are not detected), by name for the reason given on
/// <see cref="IpcFan"/>. The old device is the one the entries recorded (both <c>null</c> for entries that never
/// recorded one), the new one the device behind the ids now (both <c>null</c> when missing). A fingerprint is
/// <c>null</c> where the backend could not tell.
/// </para>
/// </summary>
public sealed record IpcHardwareChange(
    string Key,
    string Kind,
    string? OldName,
    string? OldFingerprint,
    string? NewName,
    string? NewFingerprint,
    IReadOnlyList<string> FanIds,
    IReadOnlyList<string> SensorIds);
