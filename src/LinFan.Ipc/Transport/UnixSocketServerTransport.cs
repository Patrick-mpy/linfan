// SPDX-License-Identifier: GPL-3.0-or-later

using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LinFan.Ipc.Transport;

/// <summary>
/// Server-Transport über einen Unix-Domain-Socket (Linux/macOS). Der Endpunkt ist ein Dateipfad;
/// das veraltete Socket-File wird vor dem Bind aufgeräumt.
/// <para>
/// <b>Zugriffskontrolle (Fail-Safe/Privilege-Separation):</b> der als Root laufende System-Daemon
/// beschränkt den Socket auf die Gruppe <c>linfan</c> (Modus <c>0660</c>, Gruppe <c>linfan</c>) - nur
/// Mitglieder dürfen <c>connect()</c>, nicht mehr jeder lokale Account. Der Kernel erzwingt das beim
/// Verbinden inkl. Supplementary-Groups. Ein User-Session-Daemon (nicht Root) setzt <c>0600</c> (nur der
/// Eigentümer; der Socket liegt ohnehin in einem 0700-Runtime-Dir). Zusätzlich wird die Peer-UID jeder
/// Verbindung per <c>SO_PEERCRED</c> als Audit-Spur geloggt. Fehlt die Gruppe <c>linfan</c>, bleibt der
/// Socket <c>root:root 0660</c> (fail-closed: nur Root erreichbar) samt Hinweis - statt world-rw zu öffnen.
/// </para>
/// </summary>
internal sealed class UnixSocketServerTransport : IIpcServerTransport
{
    private const string AllowedGroup = "linfan";

    private readonly ILogger _log;
    private string? _path;
    private Socket? _listener;

    public UnixSocketServerTransport(ILogger? log = null) => _log = log ?? NullLogger.Instance;

    public void Listen(string endpoint)
    {
        _path = endpoint;

        string dir = Path.GetDirectoryName(endpoint)!;
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        if (File.Exists(endpoint))
            File.Delete(endpoint); // veraltetes Socket-File aufräumen

        _listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        _listener.Bind(new UnixDomainSocketEndPoint(endpoint));
        _listener.Listen(16);

        TrySecureSocket(endpoint);
    }

    public async Task<Stream> AcceptAsync(CancellationToken ct)
    {
        Socket client = await _listener!.AcceptAsync(ct);
        LogPeer(client);
        return new NetworkStream(client, ownsSocket: true);
    }

    public void Dispose()
    {
        try { _listener?.Dispose(); } catch { /* egal */ }
        try { if (_path is not null && File.Exists(_path)) File.Delete(_path); } catch { /* egal */ }
    }

    /// <summary>
    /// Beschränkt den Socket auf die Gruppe <see cref="AllowedGroup"/> (System-Daemon) bzw. den Eigentümer
    /// (User-Session). Best effort: schlägt etwas fehl, bleibt der Socket im letzten (restriktiven) Zustand.
    /// </summary>
    private void TrySecureSocket(string path)
    {
        if (OperatingSystem.IsWindows())
            return; // Windows kennt keine Unix-Rechte (analyzer-Guard; läuft hier ohnehin nur auf Unix)

        try
        {
            // Ownerschreibbar + gruppenschreibbar, aber NICHT world (0660) - Basis für beide Zweige.
            const UnixFileMode ownerGroupRw =
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.GroupWrite;

            if (OperatingSystem.IsLinux() && geteuid() == 0)
            {
                File.SetUnixFileMode(path, ownerGroupRw); // 0660
                int gid = GroupGid(AllowedGroup);
                if (gid >= 0)
                {
                    if (chown(path, owner: uint.MaxValue, group: (uint)gid) != 0)
                        _log.LogWarning("IPC: Konnte Socket nicht der Gruppe '{Group}' zuordnen (errno {Errno}).",
                            AllowedGroup, Marshal.GetLastWin32Error());
                }
                else
                {
                    // Fail-closed: ohne Gruppe bleibt der Socket root:root 0660 → nur Root/GUI-als-Root
                    // erreichbar. Besser als world-rw; der Hinweis nennt den fehlenden Installationsschritt.
                    _log.LogError(
                        "IPC: Gruppe '{Group}' nicht gefunden - Socket bleibt root-only (0660, Gruppe root). Die "
                        + "unprivilegierte GUI kann sich erst verbinden, wenn die Gruppe existiert und der GUI-"
                        + "Nutzer Mitglied ist (siehe README/Unit).", AllowedGroup);
                }
            }
            else if (OperatingSystem.IsMacOS() && geteuid() == 0)
            {
                SecureMacRootSocket(path);
            }
            else
            {
                // User-Session-Daemon (nicht Root): nur der Eigentümer.
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite); // 0600
            }
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "IPC: Absichern des Socket-Zugriffs best effort fehlgeschlagen.");
        }
    }

    /// <summary>
    /// macOS-Root-Daemon: den Socket für den aufrufenden User erreichbar machen, ohne ihn world-rw zu öffnen
    /// (fail-closed wie der Linux-Gruppen-Zweig). <c>sudo</c> liefert die Original-UID/GID in
    /// <c>SUDO_UID</c>/<c>SUDO_GID</c>; der Socket wird darauf ge-<c>chown</c>t (0600), das Elternverzeichnis
    /// bleibt traversierbar (0755). Ohne <c>SUDO_UID</c> (echter launchd-Daemon) bleibt er root-only.
    /// </summary>
    [SupportedOSPlatform("macos")] // nur aus dem macOS-Root-Zweig von TrySecureSocket aufgerufen
    private void SecureMacRootSocket(string path)
    {
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite); // 0600

        if (!uint.TryParse(Environment.GetEnvironmentVariable("SUDO_UID"), out uint uid))
        {
            _log.LogWarning("IPC: Kein SUDO_UID - Socket bleibt root-only (0600). GUI als derselbe Nutzer via "
                + "sudo starten oder LINFAN_SOCKET auf einen gemeinsamen Pfad setzen.");
            return;
        }

        uint gid = uint.TryParse(Environment.GetEnvironmentVariable("SUDO_GID"), out uint g) ? g : uint.MaxValue;

        // Elternverzeichnis traversierbar halten (0755), sonst erreicht der User den Socket nicht.
        const UnixFileMode dir0755 =
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
            | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
            | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;
        try { File.SetUnixFileMode(Path.GetDirectoryName(path)!, dir0755); } catch { /* best effort */ }

        if (chown(path, owner: uid, group: gid) != 0)
            _log.LogWarning("IPC: chown des Socket auf UID {Uid} fehlgeschlagen (errno {Errno}) - GUI kann sich "
                + "evtl. nicht verbinden.", uid, Marshal.GetLastWin32Error());
    }

    /// <summary>Loggt die Peer-UID einer Verbindung (Audit-Spur) via <c>SO_PEERCRED</c>; nur auf Linux.</summary>
    private void LogPeer(Socket client)
    {
        if (!OperatingSystem.IsLinux())
            return; // SO_PEERCRED-Konstanten sind Linux-spezifisch; macOS nutzte LOCAL_PEERCRED (später)

        try
        {
            var cred = new Ucred();
            int len = Marshal.SizeOf<Ucred>();
            if (getsockopt((int)client.Handle, SOL_SOCKET, SO_PEERCRED, ref cred, ref len) == 0)
                _log.LogDebug("IPC: Client verbunden (uid={Uid}, gid={Gid}, pid={Pid}).", cred.Uid, cred.Gid, cred.Pid);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "IPC: Peer-Credentials konnten nicht gelesen werden.");
        }
    }

    /// <summary>Liefert die GID einer Gruppe per <c>getgrnam</c> oder -1, wenn sie nicht existiert.</summary>
    private static int GroupGid(string name)
    {
        IntPtr grp = getgrnam(name);
        if (grp == IntPtr.Zero)
            return -1;
        // struct group { char* gr_name; char* gr_passwd; gid_t gr_gid; char** gr_mem; } - gr_gid folgt auf
        // zwei Zeiger (LP64: Offset 2×PtrSize). Nur die GID wird gelesen, gr_mem nicht gebraucht.
        return Marshal.ReadInt32(grp, IntPtr.Size * 2);
    }

    // --- libc (Linux) ---------------------------------------------------------

    private const int SOL_SOCKET = 1;   // Linux/glibc
    private const int SO_PEERCRED = 17; // Linux/glibc

    [StructLayout(LayoutKind.Sequential)]
    private struct Ucred
    {
        public int Pid;
        public uint Uid;
        public uint Gid;
    }

    [DllImport("libc", SetLastError = true)]
    private static extern uint geteuid();

    [DllImport("libc", SetLastError = true)]
    private static extern IntPtr getgrnam([MarshalAs(UnmanagedType.LPUTF8Str)] string name);

    /// <param name="owner">Neuer Eigentümer (uid); <c>(uid_t)-1</c> = <see cref="uint.MaxValue"/> lässt ihn unverändert.</param>
    /// <param name="group">Neue Gruppe (gid); <c>(gid_t)-1</c> = <see cref="uint.MaxValue"/> lässt sie unverändert.</param>
    [DllImport("libc", SetLastError = true)]
    private static extern int chown([MarshalAs(UnmanagedType.LPUTF8Str)] string path, uint owner, uint group);

    [DllImport("libc", SetLastError = true)]
    private static extern int getsockopt(int sockfd, int level, int optname, ref Ucred optval, ref int optlen);
}
