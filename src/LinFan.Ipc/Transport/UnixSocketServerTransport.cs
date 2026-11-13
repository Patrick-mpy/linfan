// SPDX-License-Identifier: GPL-3.0-or-later

using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LinFan.Ipc.Transport;

/// <summary>
/// Server-Transport über einen Unix-Domain-Socket (Linux/macOS). Der Endpunkt ist ein Dateipfad;
/// das veraltete Socket-File wird vor dem Bind aufgeräumt - one a server still answers at is never taken over
/// (<see cref="IpcEndpointInUseException"/>).
/// <para>
/// <b>Zugriffskontrolle (Fail-Safe/Privilege-Separation):</b> der als Root laufende System-Daemon
/// beschränkt den Socket auf die Gruppe <c>linfan</c> (Modus <c>0660</c>, Gruppe <c>linfan</c>) - nur
/// Mitglieder dürfen <c>connect()</c>, nicht mehr jeder lokale Account. Der Kernel erzwingt das beim
/// Verbinden inkl. Supplementary-Groups. Ein User-Session-Daemon (nicht Root) setzt <c>0600</c> (nur der
/// Eigentümer; der Socket liegt ohnehin in einem 0700-Runtime-Dir). Zusätzlich nennt <see cref="DescribePeer"/>
/// die Peer-UID jeder Verbindung per <c>SO_PEERCRED</c> für die Audit-Spur. Fehlt die Gruppe <c>linfan</c>, bleibt der
/// Socket <c>root:root 0660</c> (fail-closed: nur Root erreichbar) samt Hinweis - statt world-rw zu öffnen.
/// </para>
/// </summary>
internal sealed class UnixSocketServerTransport : IIpcServerTransport
{
    private const string AllowedGroup = "linfan";

    private const UnixFileMode OwnerRw = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private const UnixFileMode OwnerGroupRw = OwnerRw | UnixFileMode.GroupRead | UnixFileMode.GroupWrite;

    /// <summary>
    /// How long a server that accepted the probe may stay silent before it counts as stuck. A live daemon sends every
    /// client a snapshot each tick, and its poll interval is at most 5 s.
    /// </summary>
    private static readonly TimeSpan DefaultSilentAfter = TimeSpan.FromSeconds(10);

    private readonly ILogger _log;
    private readonly TimeSpan _silentAfter;
    private string? _path;
    private Socket? _listener;
    private int _lockFd = -1;
    private string? _lockPath;

    public UnixSocketServerTransport(ILogger? log = null)
        : this(log, DefaultSilentAfter)
    {
    }

    /// <summary>Test seam: a shorter wait for a stuck server.</summary>
    internal UnixSocketServerTransport(ILogger? log, TimeSpan silentAfter)
    {
        _log = log ?? NullLogger.Instance;
        _silentAfter = silentAfter;
    }

    public void Listen(string endpoint)
    {
        string dir = Path.GetDirectoryName(endpoint)!;
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        // Taking a live daemon's endpoint over would leave two processes writing PWM, and the GUIs talking to only one
        // of them. The lock decides who the daemon is - two starting at the same moment cannot both pass - and the probe
        // whether the socket file belongs to a daemon that still serves or to one that died.
        bool claimed = TryClaim(endpoint + ".lock");
        if (!claimed || File.Exists(endpoint))
        {
            Liveness other = Probe(endpoint, _silentAfter);
            if (!claimed || other != Liveness.Nobody)
                throw new IpcEndpointInUseException(endpoint, serving: other == Liveness.Serving);
            File.Delete(endpoint);
        }

        _listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        PresetMode(_listener);
        _listener.Bind(new UnixDomainSocketEndPoint(endpoint));
        _path = endpoint; // only now ours: Dispose deletes it by name
        _listener.Listen(16);

        TrySecureSocket(endpoint);
    }

    private enum Liveness
    {
        /// <summary>Nobody listens: the file is stale (or gone).</summary>
        Nobody,

        /// <summary>A server accepted and sent a snapshot - or closed the connection itself (its client cap).</summary>
        Serving,

        /// <summary>Something holds the endpoint but sends nothing: stopped, stuck, or still starting.</summary>
        Silent,
    }

    /// <summary>
    /// What answers at the socket file. A connect alone proves little - the kernel completes it into the listen backlog
    /// even for a daemon that is stopped (SIGSTOP) or stuck - so a server must also send its first snapshot.
    /// </summary>
    private static Liveness Probe(string endpoint, TimeSpan silentAfter)
    {
        using var probe = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        if (Connect(probe, endpoint) is { } unanswered)
            return unanswered;

        probe.ReceiveTimeout = (int)silentAfter.TotalMilliseconds;
        try
        {
            _ = probe.Receive(new byte[1]);
            return Liveness.Serving; // a snapshot's first byte, or the server closed the connection on its own
        }
        catch (SocketException)
        {
            return Liveness.Silent;
        }
    }

    /// <summary>
    /// Whether a server holds the socket file, serving or not - the connect alone: a holder that sends nothing is no
    /// reason for a fan write next to it either (see <see cref="IpcTransportFactory.IsEndpointHeld"/>).
    /// </summary>
    internal static bool IsHeld(string endpoint)
    {
        if (!File.Exists(endpoint))
            return false;
        using var probe = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            return Connect(probe, endpoint) is not Liveness.Nobody;
        }
        catch (ArgumentException)
        {
            return false; // a path too long for a socket: no server can have bound there
        }
    }

    /// <summary>Connects the probe: <c>null</c> once connected, else what the failed connect says about the endpoint.</summary>
    private static Liveness? Connect(Socket probe, string endpoint)
    {
        probe.SendTimeout = 2000; // bounds a blocking connect on a full backlog (Linux: SO_SNDTIMEO)
        try
        {
            probe.Connect(new UnixDomainSocketEndPoint(endpoint));
            return null;
        }
        catch (SocketException ex) when (ex.SocketErrorCode is SocketError.ConnectionRefused
                                                              or SocketError.AddressNotAvailable)
        {
            return Liveness.Nobody; // refused, or the file vanished meanwhile
        }
        catch (SocketException)
        {
            return Liveness.Silent; // a connect that hangs on a full backlog, or one that is denied
        }
    }

    /// <summary>
    /// Takes an exclusive lock next to the socket for the process lifetime; <c>false</c> if another process holds it.
    /// Linux only (the open flags are Linux values) - elsewhere the probe alone decides.
    /// </summary>
    private bool TryClaim(string lockPath)
    {
        if (!OperatingSystem.IsLinux())
            return true;

        int fd = open(lockPath, O_RDWR | O_CREAT | O_NOFOLLOW | O_CLOEXEC, (uint)OwnerRw);
        if (fd < 0)
            throw new IOException($"Lock-Datei {lockPath} nicht anlegbar (errno {Marshal.GetLastWin32Error()}).");
        if (flock(fd, LOCK_EX | LOCK_NB) == 0)
        {
            _lockFd = fd;
            _lockPath = lockPath;
            return true;
        }

        int errno = Marshal.GetLastWin32Error();
        _ = close(fd);
        if (errno == EWOULDBLOCK)
            return false;
        throw new IOException($"Lock-Datei {lockPath} nicht sperrbar (errno {errno}).");
    }

    /// <summary>
    /// Gives the socket its final mode before bind: Linux creates the file with the socket's own mode (minus the
    /// umask), so with a permissive umask no other account can connect before <see cref="TrySecureSocket"/> runs.
    /// Other kernels ignore it, hence best effort.
    /// </summary>
    private static void PresetMode(Socket socket)
    {
        if (!OperatingSystem.IsLinux())
            return;
        _ = fchmod((int)socket.Handle, (uint)(geteuid() == 0 ? OwnerGroupRw : OwnerRw));
    }

    public async Task<Stream> AcceptAsync(CancellationToken ct)
    {
        Socket client = await _listener!.AcceptAsync(ct);
        return new NetworkStream(client, ownsSocket: true);
    }

    public void Dispose()
    {
        try { _listener?.Dispose(); } catch { /* egal */ }
        try { if (_path is not null && File.Exists(_path)) File.Delete(_path); } catch { /* egal */ }
        if (_lockFd >= 0)
        {
            try { File.Delete(_lockPath!); } catch { /* egal */ } // while still held: nobody can claim the old file
            _ = close(_lockFd);
            _lockFd = -1;
        }
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
            if (OperatingSystem.IsLinux() && geteuid() == 0)
            {
                File.SetUnixFileMode(path, OwnerGroupRw); // 0660
                int gid = GroupGid(AllowedGroup);
                if (gid >= 0)
                {
                    if (lchown(path, owner: uint.MaxValue, group: (uint)gid) != 0)
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
                File.SetUnixFileMode(path, OwnerRw); // 0600
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
        File.SetUnixFileMode(path, OwnerRw); // 0600

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

        if (lchown(path, owner: uid, group: gid) != 0)
            _log.LogWarning("IPC: chown des Socket auf UID {Uid} fehlgeschlagen (errno {Errno}) - GUI kann sich "
                + "evtl. nicht verbinden.", uid, Marshal.GetLastWin32Error());
    }

    /// <summary>Uid, gid and pid of the peer via <c>SO_PEERCRED</c>; Linux only.</summary>
    public string? DescribePeer(Stream connection)
    {
        if (!OperatingSystem.IsLinux() || connection is not NetworkStream stream)
            return null; // SO_PEERCRED-Konstanten sind Linux-spezifisch; macOS nutzte LOCAL_PEERCRED (später)

        var cred = new Ucred();
        int len = Marshal.SizeOf<Ucred>();
        return getsockopt((int)stream.Socket.Handle, SOL_SOCKET, SO_PEERCRED, ref cred, ref len) == 0
            ? $"uid={cred.Uid}, gid={cred.Gid}, pid={cred.Pid}"
            : null;
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

    /// <summary>
    /// Like <c>chown</c>, but changes a symlink at the path rather than its target. What keeps one from being planted
    /// there is the root-owned directory of the system socket; the chmod before it follows links.
    /// </summary>
    /// <param name="owner">Neuer Eigentümer (uid); <c>(uid_t)-1</c> = <see cref="uint.MaxValue"/> lässt ihn unverändert.</param>
    /// <param name="group">Neue Gruppe (gid); <c>(gid_t)-1</c> = <see cref="uint.MaxValue"/> lässt sie unverändert.</param>
    [DllImport("libc", SetLastError = true)]
    private static extern int lchown([MarshalAs(UnmanagedType.LPUTF8Str)] string path, uint owner, uint group);

    [DllImport("libc", SetLastError = true)]
    private static extern int fchmod(int fd, uint mode);

    // Linux values (x86-64 and arm64 share them).
    private const int O_RDWR = 0x2;
    private const int O_CREAT = 0x40;
    private const int O_NOFOLLOW = 0x20000;
    private const int O_CLOEXEC = 0x80000;
    private const int LOCK_EX = 2;
    private const int LOCK_NB = 4;
    private const int EWOULDBLOCK = 11;

    [DllImport("libc", SetLastError = true)]
    private static extern int open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags, uint mode);

    [DllImport("libc", SetLastError = true)]
    private static extern int flock(int fd, int operation);

    [DllImport("libc", SetLastError = true)]
    private static extern int close(int fd);

    [DllImport("libc", SetLastError = true)]
    private static extern int getsockopt(int sockfd, int level, int optname, ref Ucred optval, ref int optlen);
}
