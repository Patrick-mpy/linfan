// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.InteropServices;

namespace LinFan.Ipc;

/// <summary>
/// Ermittelt den IPC-Endpunkt, über den GUI und Daemon kommunizieren - OS-abhängig ein
/// Unix-Domain-Socket-<b>Pfad</b> (Linux/macOS) bzw. ein Named-<b>Pipe</b>-Name (Windows).
/// <para>
/// <see cref="SocketPath"/> ist der Endpunkt, auf dem der <b>Server</b> bindet (und der Default):
/// <c>LINFAN_SOCKET</c> (Override, z. B. in der systemd-Unit) → <c>$XDG_RUNTIME_DIR/linfan.sock</c>
/// (Linux-User-Session) → auf macOS je nach Rechten <c>/Library/Application Support/linfan/linfan.sock</c>
/// (Root-Daemon, maschinenweit + traversierbar) bzw. <c>~/Library/Application Support/linfan/linfan.sock</c>
/// (User-Daemon) → <c>/run/linfan/linfan.sock</c> (Linux-System-Dienst als Root).
/// </para>
/// <para>
/// Der <b>Client</b> kennt die Privilegien des Daemons nicht und probiert deshalb über
/// <see cref="ClientCandidates"/> mehrere Endpunkte der Reihe nach durch - sonst landet z. B. eine als
/// User laufende GUI auf dem User-Pfad, während ein per <c>sudo</c> gestarteter Daemon auf dem
/// maschinenweiten Pfad bindet.
/// </para>
/// </summary>
public static class IpcEndpoint
{
    public const string SystemPath = "/run/linfan/linfan.sock";

    /// <summary>Maschinenweiter macOS-Pfad für den Root-Daemon (traversierbar für den GUI-User).</summary>
    public const string MacSystemPath = "/Library/Application Support/linfan/linfan.sock";

    /// <summary>Default-Named-Pipe-Name auf Windows (<c>\\.\pipe\linfan</c>).</summary>
    public const string WindowsPipeName = "linfan";

    public static string SocketPath
    {
        get
        {
            string? overridePath = Environment.GetEnvironmentVariable("LINFAN_SOCKET");
            if (!string.IsNullOrEmpty(overridePath))
                return overridePath;

            // Windows: Named Pipe statt Socket-Pfad. Kein XDG/Dateipfad-Konzept.
            if (OperatingSystem.IsWindows())
                return WindowsPipeName;

            string? xdg = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
            if (!string.IsNullOrEmpty(xdg))
                return Path.Combine(xdg, "linfan.sock");

            // macOS kennt weder /run (read-only) noch XDG_RUNTIME_DIR. Bewusst NICHT $TMPDIR
            // (Path.GetTempPath() hängt an der TMPDIR-Env, die zwischen Terminals/Kontexten abweicht).
            // Als Root (Steuerung, per sudo) den maschinenweiten Pfad, sonst den festen per-User-Pfad
            // (derselbe Basisordner wie die Config).
            if (OperatingSystem.IsMacOS())
                return IsRootUnix() ? MacSystemPath : MacUserSocketPath();

            return SystemPath;
        }
    }

    /// <summary>
    /// Fester, TMPDIR-unabhängiger per-User-macOS-Pfad unter <c>~/Library/Application Support/linfan/</c>
    /// (derselbe Basisordner wie <c>config.json</c>). Deterministisch über beide Prozesse.
    /// </summary>
    private static string MacUserSocketPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "linfan", "linfan.sock");

    /// <summary>Endpunkte, die ein Client der Reihe nach probiert (Override → User-Session → System).</summary>
    public static IReadOnlyList<string> ClientCandidates()
    {
        var endpoints = new List<string>();

        string? overridePath = Environment.GetEnvironmentVariable("LINFAN_SOCKET");
        if (!string.IsNullOrEmpty(overridePath))
            endpoints.Add(overridePath);

        if (OperatingSystem.IsWindows())
        {
            // Auf Windows läuft der Daemon als Dienst; es gibt nur den einen Pipe-Namen.
            endpoints.Add(WindowsPipeName);
            return endpoints.Distinct().ToList();
        }

        string? xdg = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        if (!string.IsNullOrEmpty(xdg))
            endpoints.Add(Path.Combine(xdg, "linfan.sock"));

        // macOS: User-Pfad (User-Daemon) und maschinenweiter Pfad (Root-Daemon/Steuerung) durchprobieren.
        if (OperatingSystem.IsMacOS())
        {
            endpoints.Add(MacUserSocketPath());
            endpoints.Add(MacSystemPath);
        }

        endpoints.Add(SystemPath);

        return endpoints.Distinct().ToList();
    }

    /// <summary>Ob der Prozess unter Unix als Root (euid 0) läuft; auf Windows immer <c>false</c>.</summary>
    private static bool IsRootUnix() => !OperatingSystem.IsWindows() && geteuid() == 0;

    [DllImport("libc", SetLastError = true)]
    private static extern uint geteuid();
}
