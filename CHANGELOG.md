# Automation Forge hub and CLI

Every released version of the hub and the `forge` CLI, newest first. A stable release publishes **one**
section of this file, the one whose heading starts with its version, as its release notes. Write for someone
who installs the update, not for the commit log.

Headings are `## <x.y.z> — <date>`. Use `Added` / `Changed` / `Fixed` / `Compatibility` / `Known issues`, only
the ones that apply. A version that has not shipped says `unreleased` in place of the date; set the date when it
is tagged. Versions up to 0.4.2 have their notes on the
[releases page](https://github.com/AutomationForgeHQ/automation-forge/releases).

## 0.4.3 — unreleased

### Fixed
- **C++ builds work again on an engine you installed plugins into.** The hub used to put engine installs in
  `Engine/Plugins/AutomationForge`. On a launcher engine, Unreal only compiles the build rules of plugins in
  `Engine/Plugins/Marketplace`, so building any C++ project or packaging a plugin on that engine stopped at
  once with *"Expecting to find a type to be declared in a module rules named 'ForgeKeys'"*. Engine installs now
  go to `Engine/Plugins/Marketplace/AutomationForge`, beside what Fab installs.

### Changed
- **Plugins an older hub installed move by themselves.** Installing, updating or removing a plugin on an engine
  first moves that engine's older installs to the new folder. If nothing else is about to change, the hub's
  footer offers *move plugins to the engine's Marketplace folder*, and the CLI has `forge migrate`. Moving needs
  administrator rights and a closed Unreal Editor. A plugin the editor has loaded stays where it is and is named,
  so you can close the editor and run the move again.

### Compatibility
- Nothing to reinstall: the move keeps every file and every installed version as it was.
- Plugins installed into a project (`<Project>/Plugins/AutomationForge`) are not affected.
