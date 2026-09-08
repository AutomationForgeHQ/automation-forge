# Open, Fab and paid

Every plugin in the family is exactly one of three things. It's decided per
plugin, at the point it's built, and it doesn't change with who's using it
or which project it's in.

| | Source | Cost | Where you get it |
|---|---|---|---|
| **Open** | Public, mirrored to this organisation on every release | Free | Here, and packaged on [Fab](https://www.fab.com) |
| **Fab** | Stays in the private monorepo | Free | Packaged on Fab, or through [the account app](https://app.kovati.dev) |
| **Paid** | Stays in the private monorepo | Paid | The account app, and Fab where listed |

Rule of thumb, from the [manifesto](MANIFESTO.md): **if it runs on your own
machine and costs us nothing to operate, it stays open and useful on its
own.** Paid value begins only where we operate something on your behalf —
today that's chiefly the production-grade plugins (Performance Forge, the
fitted-garment pipeline); tomorrow it's the shared workspace the whitepaper
calls Kovati Pro. Fab-only plugins sit in between: free to use, but their
implementation is closed — usually because they wrap a provider's own SDK
or container image, or because they were written from the start as a paid
plugin's neighbour rather than extracted from an open core.

Whichever tier a plugin is, it goes through the same door: **nothing
installs without an account.** A free plugin is added with one click; a
paid one is bought once. See [GETTING-STARTED.md](GETTING-STARTED.md) and
[ACCOUNTS.md](ACCOUNTS.md).

## Who's behind it

- **Kovati** — the product brand.
- **MetaWorx LLC** — founded Kovati and holds its IP.
- **Blackcode SA** (Switzerland) — backs Kovati and currently runs the
  infrastructure the account, the site and the plugins' cloud services sit
  on. This is the entity your account data actually sits with today; it
  moves only if the infrastructure does.
- **Colony Origins** — the real Unreal Engine production Automation Forge
  is built and proven inside, before anything ships.

Unreal Engine is a trademark of Epic Games, Inc. Automation Forge is an
independent product and is not endorsed by Epic Games, Inc.

## Every plugin

One row per plugin. **Repo** links only where the source is actually public
today — a blank means the distribution is Fab or paid and there is nothing
to link to.

### AutomationForge

| Plugin | Role | Distribution | Repo |
|---|---|---|---|
| AutomationForge | core | Fab | — |
| AutomationForgePipelines | add-on | Fab | — |
| AutomationForgeHub | hub bridge | Open | [AutomationForgeHub](https://github.com/AutomationForgeHQ/AutomationForgeHub) |
| AutomationForgeToolset | toolset | Open | [AutomationForgeToolset](https://github.com/AutomationForgeHQ/AutomationForgeToolset) |

### MotionForge

| Plugin | Role | Distribution | Repo |
|---|---|---|---|
| MotionForge | core | Open | [MotionForge](https://github.com/AutomationForgeHQ/MotionForge) |
| MotionForgeUthana | provider | Open | [MotionForgeUthana](https://github.com/AutomationForgeHQ/MotionForgeUthana) |
| MotionForgeKimodo | provider | Fab | — |
| MotionForgeQuality | add-on | Fab | — |
| MotionForgeToolset | toolset | Open | [MotionForgeToolset](https://github.com/AutomationForgeHQ/MotionForgeToolset) |
| MotionForgeKimodoToolset | toolset | Open | [MotionForgeKimodoToolset](https://github.com/AutomationForgeHQ/MotionForgeKimodoToolset) |
| MotionForgeQualityToolset | toolset | Open | [MotionForgeQualityToolset](https://github.com/AutomationForgeHQ/MotionForgeQualityToolset) |

### SpeechForge

| Plugin | Role | Distribution | Repo |
|---|---|---|---|
| SpeechForge | core | Open | [SpeechForge](https://github.com/AutomationForgeHQ/SpeechForge) |
| SpeechForgeElevenLabs | provider | Open | [SpeechForgeElevenLabs](https://github.com/AutomationForgeHQ/SpeechForgeElevenLabs) |
| SpeechForgeDeepL | provider | **Paid** | — |
| SpeechForgeToolset | toolset | Open | [SpeechForgeToolset](https://github.com/AutomationForgeHQ/SpeechForgeToolset) |

### FaceForge

| Plugin | Role | Distribution | Repo |
|---|---|---|---|
| FaceForge | core | Open | [FaceForge](https://github.com/AutomationForgeHQ/FaceForge) |
| FaceForgeMetaHuman | adapter | Open | [FaceForgeMetaHuman](https://github.com/AutomationForgeHQ/FaceForgeMetaHuman) |
| FaceForgeACE | provider | Fab | — |
| FaceForgeACEToolset | toolset | Open | [FaceForgeACEToolset](https://github.com/AutomationForgeHQ/FaceForgeACEToolset) |
| FaceForgeToolset | toolset | Open | [FaceForgeToolset](https://github.com/AutomationForgeHQ/FaceForgeToolset) |

### MontageForge

| Plugin | Role | Distribution | Repo |
|---|---|---|---|
| MontageForge | core | Open | [MontageForge](https://github.com/AutomationForgeHQ/MontageForge) |
| MontageForgeToolset | toolset | Open | [MontageForgeToolset](https://github.com/AutomationForgeHQ/MontageForgeToolset) |

### MeshForge

| Plugin | Role | Distribution | Repo |
|---|---|---|---|
| MeshForge | core | Open | [MeshForge](https://github.com/AutomationForgeHQ/MeshForge) |
| MeshForgeCloud | provider | Fab | — |
| MeshForgeTrellis | provider | Fab | — |
| MeshForgeGarment | post-process | **Paid** | — |
| MeshForgeToolset | toolset | Open | [MeshForgeToolset](https://github.com/AutomationForgeHQ/MeshForgeToolset) |
| MeshForgeTrellisToolset | toolset | Open | [MeshForgeTrellisToolset](https://github.com/AutomationForgeHQ/MeshForgeTrellisToolset) |

`MeshForgeCloud` is one plugin covering two hosted vendors — **Tripo** and
**Meshy** — registered as separate providers under it, not two plugins.
`MeshForgeTrellis` (Microsoft TRELLIS.2, local and free) is tested and
working but not yet released — the one member of this set still without a
public build.

`MeshForgeToolset` and `MeshForgeTrellisToolset` moved from Fab to Open on
2026-09-07 — the open question noted here previously is resolved: every
set's toolset is open regardless of its core's tier, the toolset is the MCP
surface, and this set was the one exception. Neither has a mirror yet;
`tools/mirror.ps1` creates one on first publish.

### SurfaceForge

| Plugin | Role | Distribution | Repo |
|---|---|---|---|
| SurfaceForge | core | Open | [SurfaceForge](https://github.com/AutomationForgeHQ/SurfaceForge) |
| SurfaceForgeLocal | provider | Fab | — |
| SurfaceForgePatina | provider | Fab | — |
| SurfaceForgeToolset | toolset | Open | [SurfaceForgeToolset](https://github.com/AutomationForgeHQ/SurfaceForgeToolset) |

### PerformanceForge

| Plugin | Role | Distribution | Repo |
|---|---|---|---|
| PerformanceForge | core | **Paid** | — |
| PerformanceForgeToolset | toolset | Open | [PerformanceForgeToolset](https://github.com/AutomationForgeHQ/PerformanceForgeToolset) |

Performance Forge is the first plugin priced from day one, and
`MeshForgeGarment` and `SpeechForgeDeepL` followed it: performance capture,
take ledgers, a fitted-garment solve and voice-over localisation are all
production-layer work, not prototyping — see the whitepaper's "from indie
tool to studio layer". The agent surface stays open regardless: Performance
Forge and MeshForgeGarment each keep a dedicated Toolset sibling open on
purpose (the toolset is the MCP surface, and publishing the surface is the
point even when the implementation behind it is sold), and SpeechForgeDeepL
needs no equivalent move — it has no toolset of its own, and was already
reachable through the always-open `SpeechForgeToolset`.

### Tools

| Plugin | Role | Distribution | Repo |
|---|---|---|---|
| MeshWeightRemap | core | Open | [MeshWeightRemap](https://github.com/AutomationForgeHQ/MeshWeightRemap) |
| MeshWeightRemapToolset | toolset | Open | [MeshWeightRemapToolset](https://github.com/AutomationForgeHQ/MeshWeightRemapToolset) |

`MeshWeightRemap` moved from Fab to Open on 2026-09-07 (0.1.1) — a
distribution-only change, no code behind it: standalone, no vendor keys and
no paid runner, the same shape its toolset has been in all along.

## Reading a "Fab" row

Fab-only doesn't mean hidden away — it means the plugin is free to use but
its implementation isn't published. In practice that's almost always one of:

- **It wraps a provider's own container or SDK** (NVIDIA Kimodo, NVIDIA
  Audio2Face-3D, Microsoft TRELLIS.2, StableMaterials/MatFuse) where the
  packaged binary plus a public `Runner/` folder is what a customer needs,
  not the glue code.
- **It's a measurement or add-on pass** (MotionForge Quality) with nothing
  a project would fork.
- **It was written new, next to a paid sibling**, rather than extracted
  from an already-open core (SpeechForge DeepL, MeshForge's Trellis
  toolset) — "cores open, edges Fab."

None of that changes the account rule: whether a plugin is open, Fab or
paid, it still has to be on your account before the hub will install it.

## Keeping this page current

This page is a hand-maintained snapshot, not a generated one — it can lag.
The source of truth for every plugin's distribution and version is
`plugins.json` (the register) in the private `forge` monorepo, and
`manifest.json` (the built, hub-facing manifest, regenerated on every
release) in this repository. Both live in the AutomationForgeHQ GitHub
org; check them, not this table, when the two disagree.
