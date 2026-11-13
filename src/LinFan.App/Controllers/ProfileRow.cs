// SPDX-License-Identifier: GPL-3.0-or-later

using CommunityToolkit.Mvvm.ComponentModel;
using LinFan.App.Localization;
using LinFan.Core.Models;

namespace LinFan.App.Controllers;

/// <summary>Editierbares Profil im Editor: Name (beobachtbar) + gespeicherte Zuordnungen (Lüfter→Kurve).</summary>
public partial class ProfileRow : ObservableObject
{
    private readonly string _originalName; // Fallback gegen einen leer gespeicherten Profilnamen

    public string Id { get; }

    [ObservableProperty] private string _name;

    /// <summary>
    /// True for the one profile the daemon regulates with. Selecting a profile in the side menu only loads
    /// it into the editor - this flag follows the explicit activation, and the controller keeps exactly one
    /// row carrying it.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActivityHint))]
    private bool _isActive;

    /// <summary>
    /// Unsaved changes in this profile: new, renamed, a changed curve list or a changed curve. Set by the
    /// controller's diff; display only.
    /// </summary>
    [ObservableProperty] private bool _isDirty;

    /// <summary>Tooltip zum Aktiv-Punkt in der Profil-Liste.</summary>
    public string ActivityHint => IsActive
        ? Localizer.Instance["ProfileRow.ActiveHint"]
        : Localizer.Instance["ProfileRow.InactiveHint"];

    internal void NotifyLanguageChanged() => OnPropertyChanged(nameof(ActivityHint));

    /// <summary>
    /// The profile's curves while it is not shown in the editor - updated when it is switched away from or saved, and
    /// directly by a curve switched on/off and by taking an airflow proposal over into it.
    /// </summary>
    public IReadOnlyList<CurveConfig> Curves { get; set; }

    /// <summary>The profile's fan → curve assignments, kept like <see cref="Curves"/>.</summary>
    public IReadOnlyList<ProfileAssignment> Assignments { get; set; }

    public ProfileRow(string id, string name, IReadOnlyList<CurveConfig> curves,
                      IReadOnlyList<ProfileAssignment> assignments)
    {
        Id = id;
        _name = name;
        _originalName = name;
        Curves = curves;
        Assignments = assignments;
    }

    /// <summary>Getrimmter Name; ist er leer, der ursprünglich geladene Name (kein leer gespeicherter Profilname).</summary>
    private string PersistName => string.IsNullOrWhiteSpace(Name) ? _originalName : Name.Trim();

    public Profile ToProfile() => new() { Id = Id, Name = PersistName, Curves = Curves, Assignments = Assignments };

    /// <summary>Variante mit explizit übergebenen Kurven/Zuordnungen (für das aktive Profil = aktueller Editor-Stand), ohne den gespeicherten Snapshot zu verändern.</summary>
    public Profile ToProfile(IReadOnlyList<CurveConfig> curves, IReadOnlyList<ProfileAssignment> assignments) =>
        new() { Id = Id, Name = PersistName, Curves = curves, Assignments = assignments };

    public override string ToString() => Name;
}
