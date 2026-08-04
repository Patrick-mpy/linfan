# Contributing to LinFan

Thanks for wanting to help — issues and pull requests are welcome!

## How this repository works

This GitHub repository is a **snapshot mirror** of the primary development repository (a private GitLab instance); CI syncs it on every push to `main` and on release tags. Issues and pull requests
still live **here on GitHub** and are actively watched:

- **Issues** are the public tracker — please use the issue templates.
- **Pull requests** are reviewed here. An accepted change is applied to the upstream repository (crediting you) and shows up on GitHub with the next sync commit; the PR is then closed with a reference to that commit.

## Before you start

For anything beyond a small fix, please **open an issue first** so we can agree on the direction —
that keeps you from investing work into something that conflicts with the architecture or roadmap.

## Development setup

You need the **.NET 8 SDK**. Then:

```bash
dotnet build                                     # build everything
dotnet test                                      # test suite

dotnet run --project src/LinFan.Daemon -- run    # daemon: control loop + IPC (dry run without root)
dotnet run --project src/LinFan.App              # GUI, connects to the daemon
```

The [README](../README.md#quick-start) covers the platform specifics (root requirements, macOS);
[docs/INSTALL.md](../docs/INSTALL.md) covers real installations.

## Quality gates

CI must be green; you can check everything locally:

```bash
dotnet build
dotnet test
dotnet format --verify-no-changes --severity warn   # style/naming rules from .editorconfig
```

**Changes to core logic need unit tests.** Hardware access sits behind
`ISensorBackend` / `IFanController` and is mocked in tests.

## Conventions

- **The architecture is binding** — MVC with process separation;
  read [docs/ARCHITECTURE.md](../docs/ARCHITECTURE.md) before moving or adding code.
  In short: the GUI never touches hardware, control commands only go through the privileged daemon, and platform-specific code lives exclusively in `LinFan.Hardware.*` behind the backend interfaces (no `#if <OS>` in Core/App).
- **Fail-safe first** — this project drives real fans;
  a bug can overheat hardware. Any change to a PWM/control path (control loop, calibration, watchdog, `RestoreDefaults`) must keep the fail-safe guarantees: over-temperature, a crash, or shutdown always ends in hardware auto / 100 %.
- **Commits** follow [Conventional Commits](https://www.conventionalcommits.org/) (`fix(app): …`), in English; keep them small and focused.
- **Comments** are in English and only explain the *why* where it is not obvious from the code.
  No commented-out code, no debug leftovers.
- **UI strings are localized:** new keys go into both `Strings.resx` (English, neutral) **and** `Strings.de.resx` — a parity test fails otherwise. Numbers use `InvariantCulture` for parsing/serialization.
- **Prefer platform, framework, and existing building blocks over new dependencies** — the stack is deliberately lean.

## Code of conduct & security

Participation is covered by the [Code of Conduct](CODE_OF_CONDUCT.md). Security issues go through the [security policy](SECURITY.md), not the public tracker.
