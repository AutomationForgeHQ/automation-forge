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
