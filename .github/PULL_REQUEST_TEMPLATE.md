## Summary

<!-- What does this PR change, and why? For non-trivial changes, please link the issue where the
     direction was discussed (see CONTRIBUTING.md). -->

Closes #

## Checklist

- [ ] `dotnet build`, `dotnet test`, and `dotnet format --verify-no-changes --severity warn` pass locally
- [ ] New or changed core logic is covered by unit tests
- [ ] Commits follow Conventional Commits; commit messages and code comments are in English
- [ ] The change respects the MVC layering and dependency rules ([docs/ARCHITECTURE.md](https://github.com/Patrick-mpy/linfan/blob/main/docs/ARCHITECTURE.md)) — no platform code outside `LinFan.Hardware.*`
- [ ] Hardware/control-path changes keep the fail-safe guarantees (watchdog, `RestoreDefaults`)
- [ ] New UI strings exist in both `Strings.resx` and `Strings.de.resx`
