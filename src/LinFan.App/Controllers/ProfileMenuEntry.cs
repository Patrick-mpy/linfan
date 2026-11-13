// SPDX-License-Identifier: GPL-3.0-or-later

using System.Windows.Input;

namespace LinFan.App.Controllers;

/// <summary>
/// One entry of the header's profile menu: the heading, a profile to switch to, the separator or an action. One flat
/// list, because a MenuFlyout takes one items source; the view picks the look by <see cref="Kind"/>.
/// </summary>
public sealed class ProfileMenuEntry
{
    private ProfileMenuEntry(ProfileMenuEntryKind kind, string name)
    {
        Kind = kind;
        Name = name;
    }

    /// <summary>What the entry is; the view draws it accordingly (a separator as a line).</summary>
    public ProfileMenuEntryKind Kind { get; }

    public bool IsHeading => Kind == ProfileMenuEntryKind.Heading;

    /// <summary>A profile is a radio item: the running one carries the mark.</summary>
    public bool IsProfile => Kind == ProfileMenuEntryKind.Profile;

    public bool IsAction => Kind == ProfileMenuEntryKind.Action;

    public string Name { get; }

    /// <summary>"2 Kurven · 3 Lüfter".</summary>
    public string Detail { get; private init; } = "";

    public bool IsRunning { get; private init; }

    /// <summary>The profile holds unsaved changes.</summary>
    public bool IsDirty { get; private init; }

    /// <summary>The colour of the "running" pill: the same state as the profile button's dot.</summary>
    public ProfileDot Dot { get; private init; }

    /// <summary>The icon resource of an action.</summary>
    public string IconKey { get; private init; } = "";

    public ICommand? Command { get; private init; }

    /// <summary>Headings and the separator are disabled, which also keeps them out of keyboard navigation.</summary>
    public bool IsEnabled { get; private init; }

    /// <summary>Why the entry is disabled; <c>null</c> while it is not.</summary>
    public string? Tooltip { get; private init; }

    /// <summary>What a screen reader says.</summary>
    public string AutomationName { get; private init; } = "";

    public static ProfileMenuEntry Heading(string text) => new(ProfileMenuEntryKind.Heading, text) { AutomationName = text };

    public static ProfileMenuEntry Separator() => new(ProfileMenuEntryKind.Separator, "");

    public static ProfileMenuEntry Profile(string name, string detail, bool running, bool dirty, ProfileDot dot,
        ICommand command, bool enabled, string? disabledReason, string automationName) =>
        new(ProfileMenuEntryKind.Profile, name)
        {
            Detail = detail,
            IsRunning = running,
            IsDirty = dirty,
            Dot = dot,
            Command = command,
            IsEnabled = enabled,
            Tooltip = enabled ? null : disabledReason,
            AutomationName = automationName,
        };

    public static ProfileMenuEntry Action(string text, string iconKey, ICommand command, bool enabled) =>
        new(ProfileMenuEntryKind.Action, text)
        {
            IconKey = iconKey,
            Command = command,
            IsEnabled = enabled,
            AutomationName = text,
        };
}
