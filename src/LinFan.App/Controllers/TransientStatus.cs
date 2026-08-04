// SPDX-License-Identifier: GPL-3.0-or-later

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace LinFan.App.Controllers;

/// <summary>
/// Transient status message backing one status toast: successes fade out after the auto-hide
/// interval, errors stay until dismissed (the toast's X). Shared by the editor and backup
/// controllers so the fragile auto-hide CTS lifecycle exists exactly once. Task.Delay only
/// (no dispatcher), so plain [Fact] controller tests stay valid; the continuation rides the
/// caller's synchronization context.
/// </summary>
public sealed partial class TransientStatus : ObservableObject
{
    private readonly TimeSpan _autoHide;
    private CancellationTokenSource? _cts;

    /// <param name="autoHide">How long an auto-hiding message stays visible (default 4 s; injectable for tests).</param>
    public TransientStatus(TimeSpan? autoHide = null) => _autoHide = autoHide ?? TimeSpan.FromSeconds(4);

    [ObservableProperty] private string _text = "";

    /// <summary>True while the current message is an error (toast turns red, no auto-hide).</summary>
    [ObservableProperty] private bool _isError;

    /// <summary>Dismisses the toast (its X button).</summary>
    [RelayCommand]
    private void Dismiss() => Set("");

    public void Set(string text, bool autoHide = false, bool isError = false)
    {
        Text = text;
        IsError = isError;
        _cts?.Cancel(); // cancel a previous auto-hide
        _cts = null;
        if (!autoHide)
            return;

        _cts = new CancellationTokenSource();
        _ = ClearAfterAsync(_cts);
    }

    private async Task ClearAfterAsync(CancellationTokenSource cts)
    {
        try
        {
            await Task.Delay(_autoHide, cts.Token);
            Text = "";
        }
        catch (OperationCanceledException)
        {
            // superseded by a newer message
        }
        finally
        {
            // Release the field only if it still points to exactly this (now disposed) CTS — otherwise the
            // next Set would call Cancel() on a disposed CTS (ObjectDisposedException). Single-threaded UI
            // dispatch: no race, ReferenceEquals suffices.
            if (ReferenceEquals(_cts, cts))
                _cts = null;
            cts.Dispose();
        }
    }
}
