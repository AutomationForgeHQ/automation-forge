# Automation Forge

The prototyping suite for Unreal Engine 5.8 — motion, voice, faces and staging generated in the editor and run as one pipeline, by you and the agents working beside you, so your team plays the design before the assets exist.

This repository is the product's front door: the release **manifest**, the `forge` command-line installer, the hub, and the public documentation. Each open plugin has its own repository under this organisation; builds for every free plugin are published on [`releases`](https://github.com/AutomationForgeHQ/releases).

Built by Blackcode SA. Documentation ships with the first public release.

## The manifest

[`manifest.json`](manifest.json) is the one document the hub, the `forge` CLI and the website read to know what exists and where to get it. It is generated, never edited by hand:

```
python tools/build_manifest.py --forge <path to the forge monorepo>
```

The generator joins three sources of truth — the GitHub Releases on `releases` (what is downloadable), the plugin register in the monorepo (what each plugin is: set, role, distribution), and each plugin's descriptor (its dependencies) — and takes every checksum from GitHub's own per-asset digest, so nothing is downloaded to build it.

Shape, abridged:

```jsonc
{
  "schemaVersion": 1,
  "generatedAt": "2026-08-28T12:00:00Z",
  "channels": ["stable", "nightly"],
  "sets":    [ { "id": "montageforge", "name": "MontageForge", "members": ["MontageForge", "MontageForgeToolset"] } ],
  "plugins": [ {
    "id": "MontageForge",
    "set": "montageforge",
    "role": "core",                       // core · provider · addon · adapter · toolset
    "distribution": "open",               // open · fab · paid
    "dependencies": [],                   // other plugins in this manifest, by id
    "source": "https://github.com/AutomationForgeHQ/MontageForge",   // null until the mirror exists
    "versions": [ {
      "version": "0.1.0", "engine": "5.8", "platform": "Win64", "channel": "stable",
      "url": "https://github.com/AutomationForgeHQ/releases/releases/download/montageforge-v0.1.0/MontageForge-0.1.0-UE5.8-Win64.zip",
      "size": 9099943,
      "sha256": "b7825e37…",
      "symbols": "…-symbols.zip",
      "releasedAt": "2026-08-28T10:00:04Z",
      "notes": "https://github.com/AutomationForgeHQ/releases/releases/tag/montageforge-v0.1.0"
    } ]
  } ]
}
```

Versions are newest first per engine. A `fab` or `paid` plugin's package carries binaries and public headers only; an `open` plugin's carries its source. Until the product domain exists the manifest is served from this repository's raw URL.

## The hub and the CLI

**Download:** [`AutomationForge-Setup.exe`](https://github.com/AutomationForgeHQ/automation-forge/releases/latest/download/AutomationForge-Setup.exe)
— Windows 10/11 x64, per-user, no administrator prompt. It installs the hub and
`forge`, adds `forge` to PATH, and can start the hub with Windows. Portable:
[`AutomationForge-Hub-win-x64.zip`](https://github.com/AutomationForgeHQ/automation-forge/releases/latest/download/AutomationForge-Hub-win-x64.zip),
[`forge-win-x64.zip`](https://github.com/AutomationForgeHQ/automation-forge/releases/latest/download/forge-win-x64.zip),
[`SHA256SUMS.txt`](https://github.com/AutomationForgeHQ/automation-forge/releases/latest/download/SHA256SUMS.txt).
Asset names never carry a version, so those links always point at the newest
release.

Both share `src/Forge.Core` — manifest, release discovery, engines, installs,
receipts, project descriptors, settings.

```
forge engines                       the Unreal installations on this machine
forge list [--engine 5.8]           every set and plugin, with what is installed
forge install montageforge          a set, or a plugin, dependencies first
forge install MotionForge --project C:\Games\MyGame   into a project instead
forge update                        everything on a target to its latest
forge uninstall MontageForge
forge enable MontageForge --project C:\Games\MyGame   the .uproject edit
forge channel [stable|nightly]      the channel both surfaces follow
forge login · logout · whoami       one account for the site, the hub and the CLI
```

Free plugins need no account. Signing in opens the account site in the
browser; when you say yes there, the session is handed to this machine over
the loopback and kept under DPAPI — no password passes through the desktop. A target under Program Files makes the CLI
relaunch itself elevated once. The hub (`src/Forge.Hub`, Avalonia) is the same
core with a window: sets, one action per plugin, engine picker, update counts;
it relaunches itself headless and elevated for engine installs. It is resident:
an icon by the clock, a check every four hours over every engine and project it
installed into, a Windows notification when something new appears, and it
replaces itself through the installer when a newer hub is released. Settings
(`%LOCALAPPDATA%\AutomationForge\settings.json`): channel, start with Windows,
keep running in the tray, notifications.

### Versions, channels, releases

- `VERSION` at the root is the version being worked on; `Directory.Build.props`
  stamps it into every assembly. **Bump it right after a release.**
- A stable release is the tag `v<VERSION>` — `release.yml` refuses a tag that
  does not match the file, builds with `build.yml`, and publishes.
- `nightly.yml` builds `main` every night it moved, as
  `<VERSION>-nightly.<yyyymmdd>`, a GitHub pre-release; nightlies older than
  fourteen days are pruned. The hub's **nightly** channel sees pre-releases
  (of the hub and of the plugins — a plugin pre-release on `releases` is a
  nightly there too); **stable** never does. On nightly, whichever is newest
  wins, so a stable release published after a nightly takes over.
- `tools/publish.ps1 [-Version x.y.z]` is the whole build locally — the same
  script CI runs. It needs Inno Setup 6 (`winget install JRSoftware.InnoSetup`)
  or `-SkipInstaller`.
- The installer is unsigned until the code-signing certificate arrives;
  Windows will ask to confirm the first run.
