// SPDX-License-Identifier: GPL-3.0-or-later

using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LinFan.App.Localization;
using LinFan.App.Services;

namespace LinFan.App.Controllers;

/// <summary>
/// The version lines under the settings menu: this app's build, and the service's whenever it runs a different one -
/// after an update one of the two keeps the old build until it restarts. <see cref="MainController"/> feeds it every
/// snapshot. Next to the version, a link to the project on GitHub.
/// </summary>
public sealed partial class VersionController : ObservableObject, IDisposable
{
    /// <summary>The public repository - the one the update check asks for releases.</summary>
    public const string RepositoryUrl = "https://github.com/" + UpdateCheckService.DefaultRepoSlug;

    // Short enough to read, long enough to find the commit.
    private const int CommitLength = 7;

    private readonly string _app;
    private readonly Func<string, Task<bool>> _openUrl;
    private bool _connected;
    private string? _daemon;

    /// <param name="appVersion">This app's informational version; default <see cref="AppVersion.InformationalRaw"/>.</param>
    /// <param name="openUrl">Opens a web page; default the OS shell (tests record the call).</param>
    public VersionController(string? appVersion = null, Func<string, Task<bool>>? openUrl = null)
    {
        _app = appVersion ?? AppVersion.InformationalRaw;
        _openUrl = openUrl ?? UrlLauncher.OpenAsync;
        Localizer.Instance.PropertyChanged += OnLanguageChanged;
    }

    /// <summary>Opens <see cref="RepositoryUrl"/> in the browser (best effort).</summary>
    [RelayCommand]
    private async Task OpenRepository() => await _openUrl(RepositoryUrl);

    /// <summary>„LinFan 0.4.0 · a1b2c3d": selectable, so a bug report can name the exact build.</summary>
    public string AppLabel => "LinFan " + Short(_app);

    /// <summary>
    /// Connected to a service that runs another build - a different version, or none reported at all: a daemon that
    /// does not send its version is older than this app. Offline nothing is known, so nothing shows.
    /// </summary>
    public bool ShowServiceMismatch => _connected && !string.Equals(_daemon, _app, StringComparison.Ordinal);

    public string ServiceMismatchText => !ShowServiceMismatch ? ""
        : _daemon is null ? Localizer.Instance["Settings.VersionServiceOlder"]
        : Localizer.Instance.Format("Settings.VersionService", Short(_daemon));

    public void Apply(MonitorSnapshot snapshot)
    {
        string? daemon = snapshot.Connected ? snapshot.DaemonVersion : null;
        if (_connected == snapshot.Connected && _daemon == daemon)
            return;
        _connected = snapshot.Connected;
        _daemon = daemon;
        OnPropertyChanged(nameof(ShowServiceMismatch));
        OnPropertyChanged(nameof(ServiceMismatchText));
    }

    /// <summary>„0.4.0+a1b2c3d4…" → „0.4.0 · a1b2c3d": the build metadata (the commit) cut to its short form.</summary>
    internal static string Short(string informational)
    {
        int plus = informational.IndexOf('+');
        if (plus < 0)
            return informational;
        string build = informational[(plus + 1)..];
        string version = informational[..plus];
        return build.Length == 0 ? version : $"{version} · {build[..Math.Min(CommitLength, build.Length)]}";
    }

    private void OnLanguageChanged(object? sender, PropertyChangedEventArgs e) =>
        OnPropertyChanged(nameof(ServiceMismatchText));

    public void Dispose() => Localizer.Instance.PropertyChanged -= OnLanguageChanged;
}
