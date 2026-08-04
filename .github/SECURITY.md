# Security Policy

LinFan deliberately splits into an unprivileged GUI and a **privileged daemon** (root on Linux, SYSTEM on Windows) connected over a local IPC socket / named pipe that is gated by a user group.
Anything that lets an unprivileged user cross that boundary — controlling fans without being in the group, escalating privileges via the daemon or the installers, or tampering with the configuration the daemon executes — is a security issue.

## Supported versions

Only the **latest release** receives security fixes.

## Reporting a vulnerability

Please **do not** open a public issue for security problems. Instead:

- preferred: [report privately via GitHub](https://github.com/Patrick-mpy/linfan/security/advisories/new) (Security → “Report a vulnerability”)

Include your platform, the LinFan version, and steps to reproduce. This is a spare-time project — you can expect an initial response within about a week and a coordinated fix and disclosure as fast as is realistic.
