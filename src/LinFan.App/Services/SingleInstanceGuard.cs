// SPDX-License-Identifier: GPL-3.0-or-later

using System.IO.Pipes;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;

namespace LinFan.App.Services;

/// <summary>
/// Keeps the GUI to one instance per user. The first process owns a local activation endpoint; every
/// later launch connects to it - that connection brings the running window back to the front - and then
/// exits without ever building a UI.
/// <para>
/// The endpoint is a Unix domain socket (Linux/macOS) or a named pipe (Windows), the same split as in
/// <c>IpcTransportFactory</c>, but deliberately <b>not</b> its transports: those serve the daemon and are
/// built to <i>take</i> ownership (the socket server deletes a stale socket file before binding, the pipe
/// server allows many instances). Here the opposite is needed - creating the endpoint has to fail while
/// another instance holds it, because that failure is the detection.
/// </para>
/// <para>
/// A connection carries no payload: connecting <i>is</i> the activation request, and at the same time the
/// liveness probe that tells a running instance from a socket file left behind by a crash.
/// </para>
/// <para>
/// Fail-open by design: if the endpoint can neither be taken nor reached, the app starts normally with an
/// inert guard. Not being able to enforce a single window is a nuisance, never a reason to refuse startup -
/// the GUI is unprivileged and the daemon remains the only writer to the hardware.
/// </para>
/// </summary>
internal sealed class SingleInstanceGuard : IDisposable
{
    /// <summary>How long a second launch waits for the running instance before giving up and starting itself.</summary>
    private const int ConnectTimeoutMs = 2000;

    /// <summary>Failed accepts in a row after which the endpoint is given up as unusable.</summary>
    private const int MaxConsecutiveFailures = 5;

    /// <summary>Windows: the byte the owner sends back to confirm it has accepted the connection.</summary>
    private static readonly byte[] Ack = { 1 };

    private readonly CancellationTokenSource _cts = new();
    private readonly Socket? _listener;            // Unix
    private readonly NamedPipeServerStream? _pipe; // Windows
    private readonly string? _socketPath;          // Unix: removed on shutdown so no stale file is left

    private SingleInstanceGuard(Socket? listener = null, string? socketPath = null, NamedPipeServerStream? pipe = null)
    {
        _listener = listener;
        _socketPath = socketPath;
        _pipe = pipe;
    }

    /// <summary>Per-user endpoint: a socket path on Linux/macOS, a pipe name on Windows.</summary>
    public static string DefaultEndpoint() =>
        // Named pipes live in a machine-wide namespace, so the account has to be part of the name -
        // otherwise a second user's GUI would collide with the first one's endpoint instead of getting
        // its own instance. On Unix the path carries that separation on its own.
        OperatingSystem.IsWindows()
            ? $"linfan-gui-{CurrentUserSid()}"
            : UnixSocketPath(
                Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR"),
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                geteuid());

    /// <summary>
    /// Derives the Unix socket path from the environment: the per-user runtime dir when the session has
    /// one (tmpfs, cleared at logout), otherwise the per-user base dir the config lives in. Deliberately
    /// not <c>$TMPDIR</c> first, whose value differs between terminals and contexts on macOS (see
    /// <c>IpcEndpoint</c>) - both processes have to derive the same path.
    /// <para>
    /// Without a home directory <paramref name="appData"/> comes back empty (containers, service
    /// accounts). Combining that would produce a <b>relative</b> path, so every working directory would
    /// get a "single" instance of its own - hence the last resort in the temp dir, with the uid in the
    /// name because that directory is shared.
    /// </para>
    /// </summary>
    internal static string UnixSocketPath(string? runtimeDir, string? appData, uint uid) =>
        !string.IsNullOrEmpty(runtimeDir) ? Path.Combine(runtimeDir, "linfan-gui.sock")
        : !string.IsNullOrEmpty(appData) ? Path.Combine(appData, "linfan", "gui.sock")
        : Path.Combine(Path.GetTempPath(), $"linfan-gui-{uid}.sock");

    /// <summary>
    /// Tries to become the one GUI instance. Returns the guard when this process owns the endpoint (the
    /// caller keeps it alive for the whole run), or <c>null</c> when a running instance was found and
    /// activated - then this process must exit silently.
    /// </summary>
    public static SingleInstanceGuard? AcquireOrActivate(string endpoint)
    {
        try
        {
            return OperatingSystem.IsWindows() ? AcquireWindows(endpoint) : AcquireUnix(endpoint);
        }
        catch
        {
            return Inert(); // fail-open: start normally rather than not at all
        }
    }

    /// <summary>
    /// Starts watching the endpoint; <paramref name="onActivate"/> runs on a background thread for every
    /// further launch (marshal to the UI thread yourself). A no-op on an inert guard.
    /// </summary>
    public void ListenForActivation(Action onActivate)
    {
        if (_listener is null && _pipe is null)
            return;

        _ = Task.Run(() => ListenAsync(onActivate));
    }

    public void Dispose()
    {
        _cts.Cancel();
        ReleaseEndpoint();
        _cts.Dispose();
    }

    /// <summary>Closes the endpoint and removes the socket file behind it. Safe to call more than once.</summary>
    private void ReleaseEndpoint()
    {
        try { _pipe?.Dispose(); } catch { /* egal */ }
        try { _listener?.Dispose(); } catch { /* egal */ }
        // Disposing the socket already unlinks its file; this only covers the case where that did not
        // happen, so the next start does not have to go through the stale-file path.
        try { if (_socketPath is not null) File.Delete(_socketPath); } catch { /* egal */ }
    }

    private async Task ListenAsync(Action onActivate)
    {
        int failures = 0;
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                if (_pipe is not null)
                    await AcceptPipeAsync();
                else
                    using (await _listener!.AcceptAsync(_cts.Token)) { }
            }
            catch (IOException)
            {
                // A launch that vanished before its connection was accepted (killed mid-handshake) leaves
                // the pipe instance in a broken state; disconnecting puts it back into listening. Keep
                // watching - otherwise every later launch would open a window of its own. If that does not
                // recover, stop: a background thread spinning on a broken endpoint is worse than a second
                // window.
                try { _pipe?.Disconnect(); } catch { /* egal */ }
                if (++failures >= MaxConsecutiveFailures)
                    break;
                continue;
            }
            catch
            {
                break; // disposed, cancelled or endpoint broken - stop watching, never crash the GUI
            }

            failures = 0;
            onActivate();
        }

        // Left the loop for anything other than shutdown: give the endpoint back instead of holding it
        // unattended. Otherwise the next launch would connect to a claim nobody answers and exit without
        // showing anything at all - while releasing it only brings the second window back, which is the
        // nuisance this class prefers over an unreachable GUI.
        if (!_cts.IsCancellationRequested)
            ReleaseEndpoint();
    }

    /// <summary>
    /// Takes one connection on the pipe and acknowledges it. The acknowledgement is what makes the
    /// handshake safe: a second process that closed its end before this accept happened would leave the
    /// wait failing and its activation lost, so it stays connected until this byte arrives.
    /// </summary>
    private async Task AcceptPipeAsync()
    {
        await _pipe!.WaitForConnectionAsync(_cts.Token);
        try
        {
            await _pipe.WriteAsync(Ack, _cts.Token);
            await _pipe.FlushAsync(_cts.Token);
        }
        catch (IOException)
        {
            // The other process is already gone - it got what it came for either way.
        }
        finally
        {
            _pipe.Disconnect(); // free the single pipe instance for the next launch
        }
    }

    private static SingleInstanceGuard Inert() => new();

    // --- Unix (Linux/macOS): the socket file is the lock - bind() fails while it exists --------------

    /// <summary>
    /// The Unix half of <see cref="AcquireOrActivate"/>. Internal so the tests can drive it on Windows as
    /// well, where AF_UNIX exists since Windows 10 - the mirror image of <c>NamedPipeTransportTests</c>,
    /// which checks the pipe transport on Linux. Otherwise the branch of the platform that has priority
    /// would only ever run in CI.
    /// </summary>
    internal static SingleInstanceGuard? AcquireUnix(string endpoint)
    {
        string? dir = Path.GetDirectoryName(endpoint);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        for (int attempt = 0; ; attempt++)
        {
            var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            try
            {
                listener.Bind(new UnixDomainSocketEndPoint(endpoint));
                listener.Listen(backlog: 4);
                RestrictToOwner(endpoint);
                return new SingleInstanceGuard(listener, endpoint);
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressAlreadyInUse)
            {
                listener.Dispose();

                if (TrySignalUnix(endpoint))
                    return null; // a running instance took the request over

                // Nobody is listening → the socket file outlived its process (crash, kill -9). Remove it
                // and bind once more; a second failure means another launch won the race in between, so
                // leave the endpoint to it rather than fighting over the file.
                if (attempt > 0)
                    return Inert();
                try { File.Delete(endpoint); } catch { return Inert(); }
            }
            catch
            {
                listener.Dispose();
                throw;
            }
        }
    }

    private static bool TrySignalUnix(string endpoint)
    {
        try
        {
            using var client = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            client.Connect(new UnixDomainSocketEndPoint(endpoint));
            return true;
        }
        catch
        {
            return false; // refused (stale file) or unreachable
        }
    }

    /// <summary>
    /// Restricts the socket to its owner (0600) so no other local account can pop up this user's window.
    /// bind() creates the file under the umask first, so this closes a very short window - the runtime dir
    /// it lives in is user-only anyway. Best effort: on failure the socket keeps the umask permissions.
    /// </summary>
    private static void RestrictToOwner(string path)
    {
        if (OperatingSystem.IsWindows())
            return; // analyzer guard; this path only ever runs on Unix

        try { File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
        catch { /* egal */ }
    }

    // --- Windows: a pipe limited to one server instance is the lock, freed when the owner dies ------

    [SupportedOSPlatform("windows")]
    private static SingleInstanceGuard? AcquireWindows(string endpoint)
    {
        try
        {
            var pipe = new NamedPipeServerStream(
                endpoint, PipeDirection.Out, maxNumberOfServerInstances: 1,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            return new SingleInstanceGuard(pipe: pipe);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // "All pipe instances are busy" - the name is taken, so a GUI should be running.
            return TrySignalWindows(endpoint) ? null : Inert();
        }
    }

    [SupportedOSPlatform("windows")]
    private static bool TrySignalWindows(string endpoint)
    {
        try
        {
            // The running process may only pull its window up front if a process that currently holds the
            // foreground right hands it over - a freshly launched one does. Without this Windows just
            // flashes the taskbar button. Granted before connecting, because the connect is what triggers
            // the activation over there.
            try { AllowSetForegroundWindow(AsfwAny); } catch (EntryPointNotFoundException) { /* egal */ }

            using var client = new NamedPipeClientStream(
                ".", endpoint, PipeDirection.In, PipeOptions.Asynchronous);
            client.Connect(ConnectTimeoutMs);
            WaitForAck(client);
            return true;
        }
        catch
        {
            return false; // nobody listening, or the owner is hung - start normally instead
        }
    }

    /// <summary>
    /// Blocks until the owner confirms the connection - see <see cref="AcceptPipeAsync"/>. Closing the
    /// pipe any earlier can drop the activation, and this process exits right afterwards. Bounded, so a
    /// hung owner costs a moment instead of a start that never happens.
    /// </summary>
    private static void WaitForAck(NamedPipeClientStream client)
    {
        using var timeout = new CancellationTokenSource(ConnectTimeoutMs);
        try { client.ReadAsync(new byte[1], timeout.Token).AsTask().GetAwaiter().GetResult(); }
        catch { /* egal - the connection alone was the request */ }
    }

    [SupportedOSPlatform("windows")]
    private static string CurrentUserSid()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        // Pipe names must not contain a backslash, which the DOMAIN\user fallback carries.
        return identity.User?.Value ?? identity.Name.Replace('\\', '-');
    }

    /// <summary>ASFW_ANY - every process may take the foreground; this one is about to exit anyway.</summary>
    private const uint AsfwAny = unchecked((uint)-1);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(uint processId);

    /// <summary>Effective uid; only called from the Unix fallback in <see cref="DefaultEndpoint"/>.</summary>
    [DllImport("libc", SetLastError = true)]
    private static extern uint geteuid();
}
