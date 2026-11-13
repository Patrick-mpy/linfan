// SPDX-License-Identifier: GPL-3.0-or-later

using LinFan.Daemon;

// Entry point. 'run' starts the generic host with the control loop and the IPC server for the GUI;
// every other command is a one-shot CLI action (list, monitor, restore, … - see CliApp).
string command = args.Length > 0 ? args[0].ToLowerInvariant() : "list";
return command == "run"
    ? await DaemonHost.RunAsync(args)
    : await CliApp.RunAsync(args);
