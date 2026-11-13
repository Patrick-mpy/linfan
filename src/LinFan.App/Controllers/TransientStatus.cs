// SPDX-License-Identifier: GPL-3.0-or-later

using System.Windows.Input;
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
    /// <summary>How long a message with an action stays - long enough to reach its button.</summary>
    public static readonly TimeSpan ActionAutoHide = TimeSpan.FromSeconds(7);

    private readonly TimeSpan _autoHide;
    private readonly TimeProvider _clock;
    private CancellationTokenSource? _cts;

    /// <param name="autoHide">How long an auto-hiding message stays visible (default 4 s; injectable for tests).</param>
    /// <param name="clock">Time source of the auto-hide delays (injectable, so tests advance time instead of waiting).</param>
    public TransientStatus(TimeSpan? autoHide = null, TimeProvider? clock = null)
    {
        _autoHide = autoHide ?? TimeSpan.FromSeconds(4);
        _clock = clock ?? TimeProvider.System;
    }

    [ObservableProperty] private string _text = "";

    /// <summary>True while the current message is an error (toast turns red, no auto-hide).</summary>
    [ObservableProperty] private bool _isError;

    /// <summary>Label of the toast's action button (e.g. “Undo”); empty without an action.</summary>
    [ObservableProperty] private string _actionLabel = "";

    /// <summary>What the action button does; <c>null</c> = the toast has no action button.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAction))]
    private ICommand? _actionCommand;

    public bool HasAction => ActionCommand is not null;

    /// <summary>The toast's symbol besides its severity - only a message with an action brings its own (never an error).</summary>
    [ObservableProperty] private StatusIcon _icon;

    /// <summary>Dismisses the toast (its X button).</summary>
    [RelayCommand]
    private void Dismiss() => Set("");

    public void Set(string text, bool autoHide = false, bool isError = false) =>
        Show(text, isError, autoHide ? _autoHide : null, "", null, StatusIcon.Default);

    /// <summary>A message with an action button (delete → “Undo”). Always fades out, after <see cref="ActionAutoHide"/>.</summary>
    public void SetWithAction(string text, string actionLabel, ICommand action, StatusIcon icon = StatusIcon.Default) =>
        Show(text, isError: false, ActionAutoHide, actionLabel, action, icon);

    private void Show(string text, bool isError, TimeSpan? hideAfter, string actionLabel, ICommand? action, StatusIcon icon)
    {
        Text = text;
        IsError = isError;
        ActionLabel = actionLabel;
        ActionCommand = action;
        Icon = icon;
        _cts?.Cancel(); // cancel a previous auto-hide
        _cts = null;
        if (hideAfter is not { } delay)
            return;

        _cts = new CancellationTokenSource();
        _ = ClearAfterAsync(delay, _cts);
    }

    private async Task ClearAfterAsync(TimeSpan delay, CancellationTokenSource cts)
    {
        try
        {
            await Task.Delay(delay, _clock, cts.Token);
            Text = "";
            ActionLabel = "";
            ActionCommand = null;
            Icon = StatusIcon.Default;
        }
        catch (OperationCanceledException)
        {
            // superseded by a newer message
        }
        finally
        {
            // Release the field only if it still points to exactly this (now disposed) CTS - otherwise the
            // next Set would call Cancel() on a disposed CTS (ObjectDisposedException). Single-threaded UI
            // dispatch: no race, ReferenceEquals suffices.
            if (ReferenceEquals(_cts, cts))
                _cts = null;
            cts.Dispose();
        }
    }
}
