// SPDX-License-Identifier: GPL-3.0-or-later

using System.Diagnostics;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LinFan.App.Services;

namespace LinFan.App.Controllers;

/// <summary>
/// Steuert den rein additiven Update-Hinweis: prüft beim Start einmal die neueste GitHub-Release und zeigt
/// bei einer neueren Version ein dismissierbares Banner (Link zur Release-Seite). Respektiert den Opt-out
/// und die zuletzt weggeklickte Version - eine spätere Release zeigt wieder an. Kein Auto-Download.
/// </summary>
public partial class UpdateController : ObservableObject
{
    private readonly IUpdateCheckService _service;
    private readonly UiSettingsStore _store;
    private readonly SemVer _current;
    private readonly Func<string, Task<bool>> _openUrl;
    private readonly Action<Action> _marshal;
    private UpdateInfo? _info;

    /// <param name="service">Update-Abfrage; Standard fragt GitHub. Injizierbar für Tests.</param>
    /// <param name="store">Quelle für Opt-out + weggeklickte Version; Standard <see cref="UiSettingsStore"/>.</param>
    /// <param name="current">Laufende Version; Standard aus <see cref="AppVersion"/>.</param>
    /// <param name="openUrl">Öffnet die Release-Seite im Browser; Standard OS-Shell. Injizierbar für Tests.</param>
    /// <param name="marshal">Führt eine Aktion auf dem UI-Thread aus; Standard <see cref="Dispatcher.UIThread"/> (Tests: synchron).</param>
    public UpdateController(
        IUpdateCheckService? service = null, UiSettingsStore? store = null,
        SemVer? current = null, Func<string, Task<bool>>? openUrl = null, Action<Action>? marshal = null)
    {
        _service = service ?? new UpdateCheckService();
        _store = store ?? new UiSettingsStore();
        _current = current ?? (AppVersion.TryCurrent(out SemVer v) ? v : new SemVer(0, 0, 0, null));
        _openUrl = openUrl ?? OpenInBrowser;
        _marshal = marshal ?? (a => Dispatcher.UIThread.Post(a));
    }

    /// <summary>True, wenn eine neuere, nicht weggeklickte Version vorliegt - steuert die Banner-Sichtbarkeit.</summary>
    [ObservableProperty] private bool _updateAvailable;

    /// <summary>Anzeigename der neueren Version (z. B. „0.2.0") für die Banner-Beschriftung.</summary>
    [ObservableProperty] private string _latestVersion = "";

    /// <summary>
    /// Führt den Check aus: Opt-out ⇒ nichts; sonst die neueste Release abfragen und - falls neuer und nicht
    /// die zuletzt weggeklickte Version - das Banner zeigen. Best-effort (der Service wirft nie).
    /// </summary>
    public async Task CheckAsync(CancellationToken ct = default)
    {
        UiSettings settings = _store.Load();
        if (!settings.UpdateChecksEnabled)
            return;

        UpdateInfo? info = await _service.CheckAsync(_current, ct).ConfigureAwait(false);
        if (info is null || string.Equals(info.LatestVersion, settings.DismissedUpdateVersion, StringComparison.Ordinal))
            return;

        _info = info;
        // Property-Sets explizit auf den UI-Thread marshalen (projektüblich, wie MainController für Snapshots) -
        // unabhängig davon, auf welchem Thread der Await zurückkommt.
        _marshal(() =>
        {
            LatestVersion = info.LatestVersion;
            UpdateAvailable = true;
        });
    }

    /// <summary>Öffnet die Release-Seite im Browser (Best-effort).</summary>
    [RelayCommand]
    private async Task OpenRelease()
    {
        if (_info is { } info)
            await _openUrl(info.ReleaseUrl);
    }

    /// <summary>Blendet das Banner aus und merkt sich die weggeklickte Version (eine neuere zeigt wieder an).</summary>
    [RelayCommand]
    private void Dismiss()
    {
        if (_info is { } info)
            _store.Save(_store.Load() with { DismissedUpdateVersion = info.LatestVersion });
        UpdateAvailable = false;
    }

    private static Task<bool> OpenInBrowser(string url)
    {
        try
        {
            using Process? _ = Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            return Task.FromResult(true);
        }
        catch
        {
            return Task.FromResult(false); // kein Browser/Handler verfügbar → still scheitern
        }
    }
}
