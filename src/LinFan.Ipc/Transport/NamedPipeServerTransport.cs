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
/// Clients gleichzeitig) — analog dazu, wie der Unix-Transport pro Accept einen neuen Stream liefert.
/// Der zurückgegebene Stream gehört dem Aufrufer (<see cref="IpcServer"/> entsorgt ihn).
/// <para>
/// <b>Zugriffskontrolle (Privilege-Separation):</b> die Pipe bekommt eine DACL: SYSTEM und Administratoren
/// Vollzugriff, die Gruppe <see cref="AllowedGroup"/> Lesen/Schreiben — damit sich <b>nur</b> berechtigte
/// GUI-Nutzer (nicht jeder authentifizierte Account) mit dem als SYSTEM/Admin laufenden Daemon verbinden
/// können. Existiert die Gruppe nicht (Installationsschritt fehlt), wird das geloggt und ersatzweise
/// „Authentifizierte Benutzer" gewährt, damit die GUI nicht bricht (Härtung greift, sobald die Gruppe da
/// ist). Die ACL wird nur unter Windows gesetzt; auf anderen Systemen (nur Tests, .NET emuliert Named
/// Pipes über Unix-Domain-Sockets) läuft die Pipe ohne explizite ACL.
/// </para>
/// </summary>
internal sealed class NamedPipeServerTransport : IIpcServerTransport
{
    /// <summary>Lokale Gruppe, deren Mitglieder mit dem Daemon reden dürfen (am Install anzulegen).</summary>
    private const string AllowedGroup = "LinFan Users";

    private readonly object _gate = new();
    private readonly ILogger _log;
    private string? _name;
    private NamedPipeServerStream? _pending; // gerade auf Connect wartende Instanz (für sauberen Dispose)
    private bool _disposed;

    public NamedPipeServerTransport(ILogger? log = null) => _log = log ?? NullLogger.Instance;

    public void Listen(string endpoint) => _name = endpoint;

    public async Task<Stream> AcceptAsync(CancellationToken ct)
    {
        if (_name is null)
            throw new InvalidOperationException("Listen() wurde nicht aufgerufen.");

        NamedPipeServerStream server = CreateInstance(_name);
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
            // Abbruch/Dispose/Fehler: die noch nicht übergebene Instanz selbst aufräumen.
            await server.DisposeAsync();
            throw;
        }

        lock (_gate)
        {
            if (ReferenceEquals(_pending, server))
                _pending = null;
        }
        return server;
    }

    public void Dispose()
    {
        NamedPipeServerStream? pending;
        lock (_gate)
        {
            _disposed = true;
            pending = _pending;
            _pending = null;
        }
        // Eine wartende Instanz disposen bricht ihr WaitForConnectionAsync ab (ObjectDisposedException) —
        // so endet die Accept-Schleife des Servers auch ohne ausgelöstes Cancellation-Token.
        try { pending?.Dispose(); } catch { /* egal */ }
    }

    private NamedPipeServerStream CreateInstance(string name)
    {
        if (OperatingSystem.IsWindows())
            return CreateSecuredInstance(name);

        // Nicht-Windows (nur Tests): plain Pipe ohne ACL — Unix-Domain-Socket-Emulation.
        return new NamedPipeServerStream(
            name, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
    }

    [SupportedOSPlatform("windows")]
    private NamedPipeServerStream CreateSecuredInstance(string name)
    {
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(
            AllowedPrincipal(),
            PipeAccessRights.ReadWrite, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            PipeAccessRights.FullControl, AccessControlType.Allow));

        return NamedPipeServerStreamAcl.Create(
            name, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous,
            inBufferSize: 0, outBufferSize: 0, security);
    }

    /// <summary>
    /// SID der Gruppe <see cref="AllowedGroup"/>, deren Mitglieder Lese-/Schreibzugriff bekommen. Fehlt die
    /// Gruppe, wird auf „Authentifizierte Benutzer" zurückgefallen (GUI bleibt funktionsfähig) und gewarnt —
    /// die Härtung greift, sobald die Gruppe am Install angelegt und der GUI-Nutzer aufgenommen ist.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private SecurityIdentifier AllowedPrincipal()
    {
        try
        {
            return (SecurityIdentifier)new NTAccount(AllowedGroup).Translate(typeof(SecurityIdentifier));
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex,
                "IPC: Gruppe '{Group}' nicht gefunden — Pipe fällt auf 'Authentifizierte Benutzer' zurück. "
                + "Für die Zugriffsbeschränkung die Gruppe anlegen und den GUI-Nutzer aufnehmen (siehe Packaging).",
                AllowedGroup);
            return new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null);
        }
    }
}
