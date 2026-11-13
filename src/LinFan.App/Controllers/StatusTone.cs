// SPDX-License-Identifier: GPL-3.0-or-later

namespace LinFan.App.Controllers;

/// <summary>The dot colour of a status chip (fan › Advanced, the airflow page); the view maps it to the statusChip classes.</summary>
public enum StatusTone
{
    Muted,
    Ok,
    Busy,
    Warn,
}
