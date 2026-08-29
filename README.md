# Automation Forge

The prototyping suite for Unreal Engine 5.8 — motion, voice, faces and staging generated in the editor and run as one pipeline, by you and the agents working beside you, so your team plays the design before the assets exist.

This repository is the product's front door: the release **manifest**, the `forge` command-line installer, the hub, and the public documentation. Each open plugin has its own repository under this organisation; builds for every free plugin are published on [`releases`](https://github.com/AutomationForgeHQ/releases).

Built by Blackcode SA. Documentation ships with the first public release.

## The manifest

[`manifest.json`](manifest.json) is the one document the hub, the `forge` CLI and the website read to know what exists and where to get it. It is generated, never edited by hand — every plugin release regenerates it from CI, and by hand it is:

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
the loopback and kept under DPAPI — no password passes through the desktop.
Creating an account, signing the hub in, and how it all works:
[docs/ACCOUNTS.md](docs/ACCOUNTS.md). A target under Program Files makes the CLI
relaunch itself elevated once. The hub (`src/Forge.Hub`, Avalonia) is the same
core with a window, and it is resident: an icon by the clock, a check every four
hours over every engine and project it installed into, a Windows notification
when something new appears, and it replaces itself through the installer when a
newer hub is released. Settings
(`%LOCALAPPDATA%\AutomationForge\settings.json`): channel, start with Windows,
keep running in the tray, notifications, and one extra folder of plugins to look
at.

## What the hub does for a machine

Two tabs and three drawers, added in 0.3.0.

**Plugins** is the catalogue: sets, one action per plugin, engine picker, update
counts. It relaunches itself headless and elevated for engine installs.

**Runners** is what this machine can run. Today that is a Docker container and a
GPU rented by the hour, both belonging to MotionForge Kimodo. Docker state and
container state are read from Docker itself, and Start and Stop act on it — the
point being that starting a runner is something you do *before* opening an
editor, or after one has failed to start.

**Keys**, **Settings** and the account are drawers over one scrim; opening any
closes the others. Keys reach the Windows Credential Manager directly, at exactly
the entries the plugins use, so a key set in either place is set for both.

### How it knows any of that: `Config/ForgeMachine.json`

**The hub cannot ask a plugin anything.** It is a separate process that may run
with no editor open at all. So a plugin *declares* what it needs from the machine
in `Config/ForgeMachine.json`, and the hub reads it — nothing here is hard-coded,
and a plugin written next year needs no change to this application.

```jsonc
{
  "keys": [ {
    "id": "MotionForge.Uthana",
    "displayName": "Uthana",
    "owner": "MotionForge",
    "purpose": "Motion generation through Uthana. Without it this provider cannot be used.",
    "optional": false,
    "helpUrl": "https://www.uthana.com",
    "vaultEntry": "MotionForge/Uthana",              // the Credential Manager target
    "environmentVariable": "MOTIONFORGE_UTHANA_KEY"  // consulted when the vault has nothing
  } ],
  "runners": [ {
    "id": "Kimodo",
    "displayName": "Kimodo",
    "compose": "Runner/docker-compose.yml",   // the editor uses it; the hub only reports it
    "composeProject": "runner",               // how the hub finds the container, by label
    "service": "runner",
    "image": "motionforge/kimodo-runner:1.0",
    "signatureLabel": "com.blackcode.motionforge.runner",
    "health": "http://127.0.0.1:8757/health",
    "cloud": { "provider": "runpod", "keyId": "Kimodo.Runpod", "podName": "motionforge-kimodo" }
  } ]
}
```

The keys block is exactly the static half of the editor's own `FForgeKeyProvider`.
The other half is closures over the plugin's credential store, which cannot cross
a process boundary — so the hub reads the metadata from the file and performs the
operations itself, against the same vault entry.

**The blob is UTF-8 and not null-terminated.** The plugins write it with
`FTCHARToUTF8` and read it with `FUTF8ToTCHAR`, sized by the exact byte count;
anything else touching that row must match, or it writes an entry the editor
decodes as mojibake.

### Where it looks, and what it deliberately does not do

Three roots: **every engine's** `Plugins/AutomationForge`, **everywhere the hub
has installed** (from its own receipts, which covers project installs), and **one
folder named in Settings** — for whoever is writing a plugin, whose working tree
is not a release and never will be one.

Three things stay in the editor, and the reason is the same each time: they need
knowledge the hub does not have.

- **Creating a container.** The compose file interpolates a model cache, a token
  and a signature that only the plugin knows. The hub finds containers by the
  labels compose stamps on them, and starts and stops them by id, which needs
  none of that.
- **Renting a GPU.** Which card, in which region, under what price ceiling is a
  decision made against what a model needs. The hub lists what is rented, says
  what it costs, and stops or releases it.
- **Testing a key.** One authenticated call in the provider's own shape. Offering
  it here would mean a second copy of every provider's authentication.

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
