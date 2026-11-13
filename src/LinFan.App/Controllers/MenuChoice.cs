// SPDX-License-Identifier: GPL-3.0-or-later

using System.Windows.Input;

namespace LinFan.App.Controllers;

/// <summary>
/// One entry of a choice menu (MenuFlyout) the controller builds: a value to pick, an action, a heading that structures
/// the list, or a separator. Flat on purpose - the menus avoid submenus, which would open in the plain Fluent look.
/// </summary>
public sealed class MenuChoice
{
    /// <summary>The header Avalonia renders as a separator line.</summary>
    private const string SeparatorHeader = "-";

    private MenuChoice(string header, ICommand? command, bool isValue, bool isCurrent, string automationName, string detail = "")
    {
        Header = header;
        Detail = detail;
        Command = command;
        IsValue = isValue;
        IsCurrent = isCurrent;
        AutomationName = automationName;
    }

    public string Header { get; }

    /// <summary>A short note on the right of the entry (who sits at a position, „läuft"); empty for none.</summary>
    public string Detail { get; }

    /// <summary>What picking the entry does; <c>null</c> for headings and separators.</summary>
    public ICommand? Command { get; }

    /// <summary>
    /// One of the values the menu chooses between - shown as a radio item, so it can carry the mark of the current value.
    /// Actions („+ Neue Gruppe …") and the entries of a bulk menu (no current value) are plain items.
    /// </summary>
    public bool IsValue { get; }

    /// <summary>The value the menu's owner has right now (the menu marks it).</summary>
    public bool IsCurrent { get; }

    /// <summary>Only items can be picked - headings and separators are disabled, which also keeps them out of keyboard navigation.</summary>
    public bool IsEnabled => Command is not null;

    /// <summary>What a screen reader says - the full name where the header is a short one („Gehäuse vorn · Einlass" for „Vorn").</summary>
    public string AutomationName { get; }

    public static MenuChoice Value(string header, ICommand command, bool isCurrent, string? automationName = null, string detail = "") =>
        new(header, command, isValue: true, isCurrent, automationName ?? header, detail);

    public static MenuChoice Action(string header, ICommand command, string? automationName = null) =>
        new(header, command, isValue: false, isCurrent: false, automationName ?? header);

    public static MenuChoice Heading(string header) => new(header, null, isValue: false, isCurrent: false, header);

    public static MenuChoice Separator() => new(SeparatorHeader, null, isValue: false, isCurrent: false, "");
}
