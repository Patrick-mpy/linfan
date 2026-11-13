// SPDX-License-Identifier: GPL-3.0-or-later

using Microsoft.Extensions.Logging;

namespace LinFan.Ipc.Transport;

/// <summary>
/// Wählt die Transport-Implementierung passend zum Betriebssystem - analog zu <c>BackendFactory</c>
/// im Daemon. Linux/macOS nutzen Unix-Domain-Sockets, Windows eine Named Pipe. Die OS-Auswahl ist die
/// einzige Verzweigung; <see cref="IpcClient"/>/<see cref="IpcServer"/> und die GUI bleiben transport-neutral.
/// </summary>
public static class IpcTransportFactory
{
    public static IIpcClientTransport CreateClient()
    {
        if (IsUnix)
            return new UnixSocketClientTransport();
        if (OperatingSystem.IsWindows())
            return new NamedPipeClientTransport();

        throw Unsupported();
    }

    /// <param name="log">Logger für Zugriffskontroll-/Audit-Meldungen des Server-Transports (optional).</param>
    public static IIpcServerTransport CreateServer(ILogger? log = null)
    {
        if (IsUnix)
            return new UnixSocketServerTransport(log);
        if (OperatingSystem.IsWindows())
            return new NamedPipeServerTransport(log);

        throw Unsupported();
    }

    /// <summary>
    /// Whether anything holds the endpoint - a daemon that serves and one that is stopped or stuck alike; on Windows
    /// whoever owns the pipe, a squatter included. The CLI asks before it writes a fan itself: next to a daemon it
    /// would fight that daemon's control loop, while a false alarm only refuses the command. Without a transport
    /// nothing can hold one.
    /// </summary>
    public static bool IsEndpointHeld(string endpoint)
    {
        if (IsUnix)
            return UnixSocketServerTransport.IsHeld(endpoint);
        if (OperatingSystem.IsWindows())
            return NamedPipeServerTransport.IsHeld(endpoint);
        return false;
    }

    private static bool IsUnix => OperatingSystem.IsLinux() || OperatingSystem.IsMacOS();

    private static PlatformNotSupportedException Unsupported() => new(
        "Kein Transport für dieses Betriebssystem: Unix-Domain-Socket (Linux/macOS) bzw. Named Pipe (Windows).");
}
