// SPDX-License-Identifier: GPL-3.0-or-later

namespace LinFan.Core.Abstractions;

/// <summary>
/// Optional for an <see cref="IFanController"/>: what its last <see cref="IFanController.RestoreDefaults"/> could not
/// do. The restore never throws - it has to reach every channel - so without this a channel left in manual mode would
/// go unnoticed. Evaluated opt-in by pattern match, like <see cref="IBackendDiagnostics"/>. In the daemon every
/// component sees the backend through <c>SynchronizedFanController</c>, which does not pass this interface on: only
/// that wrapper evaluates it, on the backend it wraps.
/// </summary>
public interface IRestoreDiagnostics
{
    /// <summary>
    /// One line per channel the last restore did not put on hardware auto (naming the fallback it took, if any);
    /// empty when every channel made it.
    /// </summary>
    IReadOnlyList<string> LastRestoreProblems { get; }
}
