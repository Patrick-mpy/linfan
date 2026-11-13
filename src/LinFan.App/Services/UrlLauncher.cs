// SPDX-License-Identifier: GPL-3.0-or-later

using System.Diagnostics;

namespace LinFan.App.Services;

/// <summary>Opens a web page in the user's browser through the OS shell (xdg-open, the Windows shell, open).</summary>
public static class UrlLauncher
{
    /// <summary>Best effort: <c>false</c> if no browser or handler could be started, never throws.</summary>
    public static Task<bool> OpenAsync(string url)
    {
        try
        {
            using Process? _ = Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            return Task.FromResult(true);
        }
        catch
        {
            return Task.FromResult(false);
        }
    }
}
