// SPDX-License-Identifier: GPL-3.0-or-later

using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.Principal;

namespace LinFan.Ipc.Transport;

/// <summary>
/// Who may own the daemon pipe: SYSTEM, the administrators, or the current account (a dry-run daemon started from
/// it). A non-admin squatter cannot make another account the owner of a pipe it created, so the owner tells the
/// daemon's pipe from an impostor's - used by the client before talking to it and by the server before joining it.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class PipeOwner
{
    public static SecurityIdentifier? Of(PipeStream pipe) =>
        pipe.GetAccessControl().GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;

    public static bool IsTrusted(SecurityIdentifier? owner)
    {
        if (owner is null)
            return false;
        if (owner.IsWellKnown(WellKnownSidType.LocalSystemSid)
            || owner.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid))
            return true;

        using var self = WindowsIdentity.GetCurrent();
        return owner == self.User;
    }
}
