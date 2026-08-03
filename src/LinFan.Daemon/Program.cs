// SPDX-License-Identifier: GPL-3.0-or-later

using LinFan.Daemon;

// Einstiegspunkt. 'run' startet den Generic-Host mit dem Regel-Loop (Dauerbetrieb);
// alle anderen Befehle laufen als einmalige CLI-Aktion (list/monitor/init/calibrate/set/auto).
// Der IPC-Server für die GUI folgt in Phase 1, Teil 3.
string command = args.Length > 0 ? args[0].ToLowerInvariant() : "list";
return command == "run"
    ? await DaemonHost.RunAsync(args)
    : await CliApp.RunAsync(args);
