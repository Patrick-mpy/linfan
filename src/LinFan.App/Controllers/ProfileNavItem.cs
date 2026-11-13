// SPDX-License-Identifier: GPL-3.0-or-later

using CommunityToolkit.Mvvm.ComponentModel;
using LinFan.App.Localization;

namespace LinFan.App.Controllers;

/// <summary>A profile in the side menu; selecting it opens the profile editor.</summary>
public sealed partial class ProfileNavItem : CurveTabNavItem
{
    public ProfileRow Profile { get; }

    /// <summary>The selected profile shows its curves below it (the chevron points down); the others are collapsed.</summary>
    [ObservableProperty] private bool _isExpanded;

    public ProfileNavItem(ProfileRow profile) => Profile = profile;

    /// <summary>The row's accessible name: the profile, and that it runs (the badge alone is only visual).</summary>
    public override string ToString() => Profile.IsActive
        ? Localizer.Instance.Format("ProfileNavItem.RunningName", Profile.Name)
        : Profile.Name;
}
