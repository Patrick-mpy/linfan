// SPDX-License-Identifier: GPL-3.0-or-later

using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace LinFan.Hardware.Windows;

/// <summary>
/// Per-process crash marker for <see cref="WindowsLhmBackend"/>. LHM's <c>SetDefault()</c> (and the
/// <c>SetControl(i, null)</c> in <c>Computer.Close()</c>) restore the register state LHM saved at this process's
/// first <c>SetSoftware</c> on a channel. If another process left the fan in manual at that moment - crashed, or
/// still running (e.g. an elevated CLI next to the service) - that manual state becomes the saved "default".
/// <para>
/// One file per writing process (<c>lhm-restore.&lt;pid&gt;.&lt;startTicks&gt;.marker</c>, content: the boot id).
/// A process creates its own marker before it touches the hardware and only then looks for others (create, then
/// scan): of two concurrent writers at least one sees the other. Any other marker of this boot - live, dead or
/// unreadable - makes SetDefault() untrusted. Markers of another boot are deleted (the firmware re-initialised the
/// Super-I/O since); the others stay until the next boot. Nobody deletes another process's marker.
/// </para>
/// </summary>
internal sealed class LhmRestoreMarker
{
    private const string Prefix = "lhm-restore.";
    private const string Suffix = ".marker";

    private readonly string _directory;
    private readonly int _processId;
    private readonly long _processStartTicks;
    private readonly Func<int, long, bool> _isProcessAlive;

    /// <summary>Read once at construction - it cannot change within a boot, and scans run concurrently.</summary>
    private readonly long? _currentBootId;

    /// <param name="bootId">Boot counter, constant within a boot; <c>null</c> if unreadable.</param>
    /// <param name="isProcessAlive">Whether a process with this id and start time (UTC ticks) still runs.</param>
    internal LhmRestoreMarker(
        string directory, Func<long?> bootId, int processId, long processStartTicks, Func<int, long, bool> isProcessAlive)
    {
        _directory = directory;
        try { _currentBootId = bootId(); }
        catch { _currentBootId = null; }
        _processId = processId;
        _processStartTicks = processStartTicks;
        _isProcessAlive = isProcessAlive;
        OwnPath = Path.Combine(directory, string.Create(
            CultureInfo.InvariantCulture, $"{Prefix}{processId}.{processStartTicks}{Suffix}"));
    }

    /// <summary>The marker for the current process, with the real boot id and process lookup.</summary>
    public static LhmRestoreMarker ForCurrentProcess(string directory)
    {
        using var self = Process.GetCurrentProcess();
        return new LhmRestoreMarker(
            directory, ReadBootId, self.Id, self.StartTime.ToUniversalTime().Ticks, IsProcessAlive);
    }

    /// <summary>This process's marker file.</summary>
    public string OwnPath { get; }

    /// <summary>This process created its marker (and has not removed it).</summary>
    public bool Owned { get; private set; }

    /// <summary>
    /// Does another process's marker of this boot exist (live, dead or unreadable)? This alone decides trust. Stops
    /// at the first hit; never throws, and an unlistable folder counts as "yes". <paramref name="deleteOtherBoots"/>
    /// removes markers of earlier boots met on the way - only a process that touches the hardware does that.
    /// </summary>
    public bool AnyOfThisBoot(bool deleteOtherBoots)
    {
        if (ForeignMarkers() is not { } markers)
            return true;
        foreach (var marker in markers)
        {
            if (IsThisBoot(marker.BootId))
                return true;
            if (deleteOtherBoots)
            {
                try { File.Delete(marker.Path); }
                catch { /* judged the same way next time */ }
            }
        }
        return false;
    }

    /// <summary>
    /// Is there a marker of this boot whose process is gone (or that cannot be read)? Only for the wording of the
    /// start warning ("a previous run crashed") - never for trust, which <see cref="AnyOfThisBoot"/> decides. Read-only.
    /// </summary>
    public bool AnyCrashedOfThisBoot()
    {
        if (ForeignMarkers() is not { } markers)
            return true;
        foreach (var marker in markers)
        {
            if (IsThisBoot(marker.BootId) && (marker.BootId is null || !SafeIsAlive(marker.ProcessId, marker.StartTicks)))
                return true;
        }
        return false;
    }

    /// <summary>Test seam: runs at the start of every scan.</summary>
    internal Action? OnScan { get; set; }

    /// <summary>
    /// Other processes' markers with their stored boot id (<c>null</c> = unreadable), read lazily; <c>null</c> if the
    /// folder cannot be listed.
    /// </summary>
    private IEnumerable<ForeignMarker>? ForeignMarkers()
    {
        OnScan?.Invoke();
        string[] files;
        try
        {
            files = Directory.Exists(_directory)
                ? Directory.GetFiles(_directory, Prefix + "*" + Suffix)
                : Array.Empty<string>();
        }
        catch
        {
            return null;
        }
        return Enumerate(files);

        IEnumerable<ForeignMarker> Enumerate(string[] paths)
        {
            foreach (string path in paths)
            {
                if (!TryParseName(Path.GetFileName(path), out int pid, out long startTicks))
                    continue; // not a marker of ours
                if (pid == _processId && startTicks == _processStartTicks)
                    continue; // our own
                yield return new ForeignMarker(path, pid, startTicks, ReadBoot(path));
            }
        }
    }

    /// <summary>An unreadable boot id on either side counts as this boot (the safe direction).</summary>
    private bool IsThisBoot(long? markerBoot) =>
        markerBoot is not { } marker || _currentBootId is not { } current || marker == current;

    /// <summary>
    /// Creates this process's marker before its first software write. Throws <see cref="IOException"/> if that is
    /// not possible - the caller then refuses the write, because an unprotected manual fan would, after a crash,
    /// become the next process's "default".
    /// </summary>
    public void EnsureOwn()
    {
        if (Owned)
            return;

        try
        {
            Directory.CreateDirectory(_directory);
            using (var stream = new FileStream(OwnPath, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream))
            {
                Owned = true; // from here on the file exists and is ours, even if writing the content fails
                writer.Write(_currentBootId?.ToString(CultureInfo.InvariantCulture) ?? "");
            }
        }
        catch (Exception ex)
        {
            if (Owned)
            {
                // An empty or partial file reads as "unreadable" = untrusted after a crash; still remove it, since
                // the write it would protect is refused anyway.
                try { File.Delete(OwnPath); Owned = false; }
                catch { /* kept and owned: a later clean dispose removes it */ }
            }
            throw new IOException($"Absturz-Marker {OwnPath} nicht schreibbar - der Lüfter bleibt auf Firmware-Auto.", ex);
        }
    }

    /// <summary>Removes this process's own marker (best-effort); never touches another process's.</summary>
    public void DeleteOwn()
    {
        if (!Owned)
            return;
        try
        {
            File.Delete(OwnPath);
            Owned = false;
        }
        catch
        {
            // best-effort: a leftover marker only makes the next start in this boot use 100 % (safe).
        }
    }

    internal static bool TryParseName(string fileName, out int pid, out long startTicks)
    {
        pid = 0;
        startTicks = 0;
        if (fileName.Length <= Prefix.Length + Suffix.Length
            || !fileName.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)
            || !fileName.EndsWith(Suffix, StringComparison.OrdinalIgnoreCase))
            return false;

        string[] parts = fileName[Prefix.Length..^Suffix.Length].Split('.');
        return parts.Length == 2
            && int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out pid)
            && long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out startTicks);
    }

    private static long? ReadBoot(string file)
    {
        try
        {
            string content = File.ReadAllText(file).Trim();
            return long.TryParse(content, NumberStyles.None, CultureInfo.InvariantCulture, out long boot) ? boot : null;
        }
        catch
        {
            return null;
        }
    }

    private bool SafeIsAlive(int pid, long startTicks)
    {
        try { return _isProcessAlive(pid, startTicks); }
        catch { return false; }
    }

    /// <summary>
    /// Windows' boot counter (<c>PrefetchParameters\BootId</c>, a DWORD that grows by one per boot). Fast Startup
    /// ("shutdown" = hibernated kernel) may not advance it; that only errs towards untrusted (100 %).
    /// </summary>
    private static long? ReadBootId()
    {
        if (!OperatingSystem.IsWindows())
            return null;
        return ReadBootIdWindows();
    }

    [SupportedOSPlatform("windows")]
    private static long? ReadBootIdWindows()
    {
        using RegistryKey? key = Registry.LocalMachine.OpenSubKey(
            @"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management\PrefetchParameters");
        return key?.GetValue("BootId") is int id ? (uint)id : null;
    }

    private static bool IsProcessAlive(int pid, long startTicks)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return process.StartTime.ToUniversalTime().Ticks == startTicks;
        }
        catch (ArgumentException)
        {
            return false; // no such process
        }
    }
}

/// <summary>Another process's marker file, with its stored boot id (<c>null</c> = unreadable).</summary>
internal readonly record struct ForeignMarker(string Path, int ProcessId, long StartTicks, long? BootId);
