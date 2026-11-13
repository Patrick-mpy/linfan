// SPDX-License-Identifier: GPL-3.0-or-later

using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LinFan.Ipc.Transport;

/// <summary>
/// Server-Transport über eine Windows-Named-Pipe (<c>\\.\pipe\&lt;name&gt;</c>); der Endpunkt ist der
/// reine Pipe-Name. Pro akzeptierter Verbindung wird eine <b>eigene</b> Pipe-Instanz erzeugt (mehrere
/// Clients gleichzeitig) - analog dazu, wie der Unix-Transport pro Accept einen neuen Stream liefert.
/// Der zurückgegebene Stream gehört dem Aufrufer (<see cref="IpcServer"/> entsorgt ihn).
/// <para>
/// <b>Zugriffskontrolle (Privilege-Separation):</b> die Pipe bekommt eine DACL: SYSTEM und Administratoren
/// Vollzugriff, die Gruppe <see cref="DefaultAllowedGroup"/> Lesen/Schreiben - damit sich <b>nur</b> berechtigte
/// GUI-Nutzer (nicht jeder authentifizierte Account) mit dem als SYSTEM/Admin laufenden Daemon verbinden
/// können. A missing group fails closed, like the Unix socket: the pipe then only admits SYSTEM, administrators
/// and the server's own account, and the error log says how to fix it - the control loop keeps running, only the
/// GUI is locked out. Zusätzlich bekommt das <b>eigene Konto</b> des Server-Prozesses Vollzugriff - ohne das Recht
/// <c>CreateNewInstance</c> ließe sich ab der zweiten Instanz keine weitere mehr anlegen (siehe
/// <see cref="BuildSecurity"/>). Die ACL wird nur unter Windows gesetzt; auf anderen Systemen (nur Tests,
/// .NET emuliert Named Pipes über Unix-Domain-Sockets) läuft die Pipe ohne explizite ACL.
/// </para>
/// <para>
/// <b>Squatting:</b> the first instance is created with <see cref="PipeOptions.FirstPipeInstance"/>, so the daemon
/// refuses to join a pipe of that name someone else created first (the accept loop retries until the name is
/// free). After that one instance of ours always exists - see <see cref="_next"/> - so the name never lapses.
/// </para>
/// </summary>
internal sealed class NamedPipeServerTransport : IIpcServerTransport
{
    /// <summary>Lokale Gruppe, deren Mitglieder mit dem Daemon reden dürfen (am Install anzulegen).</summary>
    internal const string DefaultAllowedGroup = "LinFan Users";

    /// <summary>How long a daemon that accepted the probe may stay silent before it counts as stuck (see the Unix transport).</summary>
    private static readonly TimeSpan DefaultSilentAfter = TimeSpan.FromSeconds(10);

    private const int ProbeConnectTimeoutMs = 2000;

    private const int HeldConnectTimeoutMs = 500;

    /// <summary>
    /// How long a taken name is retried before its holder is probed: a service restart can overlap the old process's
    /// shutdown, and that one is no reason to stand down.
    /// </summary>
    private static readonly TimeSpan DefaultRetryFor = TimeSpan.FromSeconds(3);

    private static readonly TimeSpan RetryEvery = TimeSpan.FromMilliseconds(250);

    private readonly object _gate = new();
    private readonly ILogger _log;
    private readonly string _allowedGroup;
    private readonly TimeSpan _silentAfter;
    private readonly TimeSpan _retryFor;
    private string? _name;
    private NamedPipeServerStream? _pending; // gerade auf Connect wartende Instanz (für sauberen Dispose)
    // The instance the next AcceptAsync waits on, created while the one just accepted still exists. Keeping one of
    // our instances alive at all times means the pipe name never lapses between two accepts, so only the very
    // first instance needs FirstPipeInstance - and nobody can slip in between.
    private NamedPipeServerStream? _next;
    // Die DACL ist für jede Instanz dieselbe → einmal aufbauen und wiederverwenden (der Accept-Loop ruft
    // AcceptAsync seriell). Nebeneffekt: die Gruppen-Auflösung samt Fehlermeldung, falls die Gruppe fehlt, läuft
    // einmal pro Daemon-Lauf statt bei jedem Client-Connect.
    private PipeSecurity? _security;
    private bool _disposed;

    public NamedPipeServerTransport(ILogger? log = null)
        : this(DefaultAllowedGroup, log)
    {
    }

    /// <param name="allowedGroup">Local group granted read/write; a parameter only so tests can name a missing one.</param>
    /// <param name="log">Logger.</param>
    /// <param name="silentAfter">Test seam: a shorter wait for a daemon that sends nothing.</param>
    /// <param name="retryFor">Test seam: a shorter wait for a taken name to free up.</param>
    internal NamedPipeServerTransport(
        string allowedGroup, ILogger? log = null, TimeSpan? silentAfter = null, TimeSpan? retryFor = null)
    {
        _allowedGroup = allowedGroup;
        _log = log ?? NullLogger.Instance;
        _silentAfter = silentAfter ?? DefaultSilentAfter;
        _retryFor = retryFor ?? DefaultRetryFor;
    }

    /// <summary>
    /// Takes the first instance of the name right away. If the name is taken, a daemon the pipe's owner vouches for
    /// (SYSTEM, administrators, this account) that still answers - or holds the pipe without sending - makes this
    /// instance stand down (<see cref="IpcEndpointInUseException"/>): the fans are that daemon's. A holder with any
    /// other owner is a squatter, not a daemon: it must not suppress the start restore, so the accept loop just
    /// retries until the name is free.
    /// </summary>
    public void Listen(string endpoint) => Listen(endpoint, CancellationToken.None);

    public void Listen(string endpoint, CancellationToken ct)
    {
        _name = endpoint;
        if (!OperatingSystem.IsWindows())
            return;

        DateTime giveUp = DateTime.UtcNow + _retryFor;
        while (true)
        {
            try
            {
                _next = CreateInstance(endpoint, first: true);
                return;
            }
            catch (UnauthorizedAccessException) when (DateTime.UtcNow < giveUp)
            {
                // Taken - maybe by a predecessor that is still shutting down.
                ct.WaitHandle.WaitOne(RetryEvery);
                ct.ThrowIfCancellationRequested();
            }
            catch (UnauthorizedAccessException)
            {
                if (Probe(endpoint, _silentAfter, ct) is { } serving)
                    throw new IpcEndpointInUseException(endpoint, serving);
                return;
            }
        }
    }

    /// <summary>
    /// Whether the daemon that owns the pipe serves (<c>true</c>: it sent a snapshot or dropped the probe itself, its
    /// client cap) or sends nothing (<c>false</c>); <c>null</c> when nobody answers or the owner is not trusted.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static bool? Probe(string name, TimeSpan silentAfter, CancellationToken ct)
    {
        using var probe = new NamedPipeClientStream(
            ".", name, PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
        try
        {
            probe.ConnectAsync(ProbeConnectTimeoutMs, ct).GetAwaiter().GetResult();
            if (!PipeOwner.IsTrusted(PipeOwner.Of(probe)))
                return null;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
        {
            // A daemon always keeps a listening instance (even a stuck one - see _next), so a pipe that only times out
            // is no daemon of ours: treating it as one would let any account keep the service down.
            return null;
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(silentAfter);
        try
        {
            _ = probe.ReadAsync(new byte[1], cts.Token).AsTask().GetAwaiter().GetResult();
            return true;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return false;
        }
        catch (IOException)
        {
            return true; // accepted the probe, then dropped it
        }
    }

    /// <summary>
    /// Whether anything holds the name - unlike <see cref="Probe"/>, an owner that is not trusted counts too: a squatter
    /// must not make a running service invisible to a CLI about to write a fan, and a false alarm there only refuses
    /// that command. A daemon always has an instance listening, so a short connect suffices.
    /// </summary>
    [SupportedOSPlatform("windows")]
    internal static bool IsHeld(string name)
    {
        using var probe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.None,
            TokenImpersonationLevel.Identification);
        try
        {
            probe.Connect(HeldConnectTimeoutMs);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true; // the name exists, but this account may not connect or the connect failed
        }
    }

    public async Task<Stream> AcceptAsync(CancellationToken ct)
    {
        if (_name is null)
            throw new InvalidOperationException("Listen() wurde nicht aufgerufen.");

        NamedPipeServerStream? server;
        lock (_gate)
        {
            server = _next;
            _next = null;
        }
        server ??= CreateWithoutFollowUp(_name);
        lock (_gate)
        {
            if (_disposed)
            {
                server.Dispose();
                throw new ObjectDisposedException(nameof(NamedPipeServerTransport));
            }
            _pending = server;
        }

        try
        {
            await server.WaitForConnectionAsync(ct);
        }
        catch
        {
            // Abbruch/Dispose/Fehler: die noch nicht übergebene Instanz selbst aufräumen. A client that connects and
            // drops at once fails the wait too - replace the instance BEFORE letting it go, so the name stays ours.
            lock (_gate)
            {
                if (ReferenceEquals(_pending, server))
                    _pending = null;
            }
            if (!ct.IsCancellationRequested)
                CreateFollowUp(_name);
            await server.DisposeAsync();
            throw;
        }

        lock (_gate)
        {
            if (ReferenceEquals(_pending, server))
                _pending = null;
        }
        CreateFollowUp(_name);
        return server;
    }

    /// <summary>
    /// Instance for an accept with no follow-up at hand: the first one of the transport, or a replacement after the
    /// follow-up could not be created. It is first tried as the first instance of the name. That also fails while
    /// connections this daemon handed out are still open, so a pipe that exists is joined only if it is ours - the
    /// owner shows who created it.
    /// </summary>
    private NamedPipeServerStream CreateWithoutFollowUp(string name)
    {
        try
        {
            return CreateInstance(name, first: true);
        }
        catch (UnauthorizedAccessException) when (OperatingSystem.IsWindows())
        {
            NamedPipeServerStream joined = CreateInstance(name, first: false);
            if (PipeOwner.IsTrusted(PipeOwner.Of(joined)))
                return joined;
            joined.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Creates the instance the next accept waits on while one of ours still holds the name. Should it fail, the
    /// next accept falls back to <see cref="CreateWithoutFollowUp"/>.
    /// </summary>
    private void CreateFollowUp(string name)
    {
        try
        {
            NamedPipeServerStream next = CreateInstance(name, first: false);
            lock (_gate)
            {
                if (_disposed || _next is not null)
                    next.Dispose();
                else
                    _next = next;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.LogWarning(ex, "IPC: follow-up pipe instance could not be created.");
        }
    }

    public void Dispose()
    {
        NamedPipeServerStream? pending, next;
        lock (_gate)
        {
            _disposed = true;
            pending = _pending;
            _pending = null;
            next = _next;
            _next = null;
        }
        // Eine wartende Instanz disposen bricht ihr WaitForConnectionAsync ab (ObjectDisposedException) -
        // so endet die Accept-Schleife des Servers auch ohne ausgelöstes Cancellation-Token.
        try { pending?.Dispose(); } catch { /* egal */ }
        try { next?.Dispose(); } catch { /* egal */ }
    }

    private NamedPipeServerStream CreateInstance(string name, bool first)
    {
        if (OperatingSystem.IsWindows())
            return CreateSecuredInstance(name, first);

        // Nicht-Windows (nur Tests): plain Pipe ohne ACL - Unix-Domain-Socket-Emulation.
        return new NamedPipeServerStream(
            name, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
    }

    [SupportedOSPlatform("windows")]
    private NamedPipeServerStream CreateSecuredInstance(string name, bool first) =>
        NamedPipeServerStreamAcl.Create(
            name, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte,
            first ? PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance : PipeOptions.Asynchronous,
            inBufferSize: 0, outBufferSize: 0, _security ??= BuildSecurity());

    [SupportedOSPlatform("windows")]
    internal PipeSecurity BuildSecurity()
    {
        var security = new PipeSecurity();
        if (AllowedPrincipal() is { } group)
        {
            security.AddAccessRule(new PipeAccessRule(
                group, PipeAccessRights.ReadWrite, AccessControlType.Allow));
        }
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            PipeAccessRights.FullControl, AccessControlType.Allow));

        // Eigenes Konto: Windows prüft für JEDE weitere Instanz einer bereits existierenden Pipe das Recht
        // FILE_CREATE_PIPE_INSTANCE in deren DACL - nur die allererste Instanz ist frei. Läuft der Server
        // nicht als SYSTEM/Administrator (z. B. Daemon im Dry-Run ohne Adminrechte, Tests), träfe er sonst
        // nur die ReadWrite-ACE, könnte nach dem ersten Client keine Instanz mehr anlegen und nähme keine
        // weitere Verbindung an. Bewusst NUR fürs eigene Konto: bekäme die erlaubte Gruppe dieses Recht,
        // könnte ein Mitglied eigene Instanzen derselben Pipe anlegen und der GUI gegenüber den Daemon
        // spielen (Pipe-Squatting) - genau das, was die Zugriffskontrolle hier verhindern soll.
        using var self = WindowsIdentity.GetCurrent();
        if (self.User is { } own)
        {
            security.AddAccessRule(new PipeAccessRule(
                own, PipeAccessRights.FullControl, AccessControlType.Allow));
        }

        return security;
    }

    /// <summary>
    /// SID of the group granted read/write, or <c>null</c> when it does not exist. Nothing is granted in its place:
    /// falling back to "Authenticated Users" would open the privileged daemon to every local account whenever the
    /// installer's group setup failed or someone deleted the group.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private SecurityIdentifier? AllowedPrincipal()
    {
        try
        {
            return (SecurityIdentifier)new NTAccount(_allowedGroup).Translate(typeof(SecurityIdentifier));
        }
        catch (IdentityNotMappedException ex)
        {
            _log.LogError(ex,
                "IPC: group '{Group}' not found - the pipe only admits SYSTEM and administrators, the GUI cannot "
                + "connect. Create the group, add the GUI user, then restart the service (see packaging/windows).",
                _allowedGroup);
            return null;
        }
    }
}
