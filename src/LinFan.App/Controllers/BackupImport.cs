// SPDX-License-Identifier: GPL-3.0-or-later

using LinFan.App.Services;

namespace LinFan.App.Controllers;

/// <summary>
/// A backup that was read and checked (<see cref="BackupController.ReadImport"/>) and waits for the user's confirmation;
/// <see cref="ConfirmMessage"/> says what the import replaces and what the backup holds.
/// </summary>
public sealed record BackupImport(LinFanBackup Backup, string ConfirmMessage);
