# The Forge sets

The unit of the product is a **set**, not a plugin — nobody installs
"MotionForge Kimodo" on its own, they install motion generation, which
happens to be a core plugin, a provider and two toolsets. Each set below is:
what it's *for*, what it does *today*, and where the honest edges are. For
role and licence per plugin, see [DISTRIBUTION.md](DISTRIBUTION.md); for the
architecture that ties them together, see [PLAYABLE-OPS.md](PLAYABLE-OPS.md).

Every core plugin ships a matching **toolset** — the same subsystem exposed
as typed Model Context Protocol tools, adding nothing of its own. That's not
a footnote: it's how a person and an agent end up doing the same work
through the same rules.

---

## AutomationForge — the spine

**Goal:** the ledger, the graduation rule and the pipeline executor every
other set plugs into, plus the in-editor Hub menu that ties installs,
updates and API keys together.

- Wire any Forge set into one pipeline — a definition anyone can author, no
  code involved.
- Leave a run going for hours: stepped, resumable and cached, so nothing
  generates twice.
- Price a run before it spends a cent, and gate the decisions a machine
  shouldn't make on its own.
- Trace every generated asset — what made it, what it cost, what depends on
  it — from before the asset exists.
- One rule makes all of it safe to leave running: the moment an asset is
  hand-authored it **graduates**, and the tooling never touches it again.
- One Keys page for every installed plugin — a provider account is entered
  once, into the OS credential vault, and every set that needs it finds it.

Editor and commandlet only. None of it ships inside your packaged game.

**Members:** `AutomationForge` (core), `AutomationForgePipelines` (the
graph editor and user-authored pipelines), `AutomationForgeHub` (the bridge
to the Windows hub app), `AutomationForgeToolset`.

---

## MotionForge — animation at the speed of the idea

**Goal:** text, or a posed performance, becomes an `AnimSequence` on your
own skeleton — generated locally, on a rented card, or through a hosted
provider, with no retarget step.

- Describe a motion and watch it on your character in seconds.
- Import straight onto your skeleton — an ordinary `AnimSequence` the rest
  of the project can't tell was generated.
- Run it three ways behind one interface: your own GPU, a card rented from
  inside the editor, or a hosted API.
- Direct a take with constraints — pose a character in the level, read a
  pose off an existing animation, or harvest every keyed moment in a Level
  Sequence.
- A seeded take regenerates identically on demand, so the definition — not
  the file — is the thing worth keeping.

**Status:** working, beta-quality. Hands and finger animation depend on the
provider — the free local model predicts a body with no fingers, so its
takes ship with a fixed hand pose; the hosted provider animates fingers.

**Members:** `MotionForge` (core + provider registry), `MotionForgeUthana`
(hosted provider), `MotionForgeKimodo` (NVIDIA Kimodo in Docker, your own
GPU), `MotionForgeQuality` (clip measurement), and three toolsets.

---

## SpeechForge — every line in the game, spoken

**Goal:** a written line becomes a `USoundWave`, imported with
character-level timing attached, on your own provider account.

- Voice a whole script in one pass; see the price before a single line
  generates.
- Re-run safely — unchanged lines are skipped, never re-bought.
- Character-level timing comes back with every take, so dialogue can time
  itself to its own audio with no hand-authoring.
- Deliberately stops at the wave: no dialogue system, no subtitles, no
  gameplay framework baked in. Binding it to yours is an add-on's job.

**Status:** working; the product surface is still evolving.

**Members:** `SpeechForge` (core), `SpeechForgeElevenLabs` (hosted
provider, live), `SpeechForgeDeepL` (planned provider), and a toolset.

---

## FaceForge — the face that goes with the voice

**Goal:** spoken audio becomes a performing face — solved locally for
nothing, or on NVIDIA Audio2Face-3D, retargeted into whatever control
vocabulary your rig actually reads, and baked ready to play.

- Solve a performing face straight from a sound wave, locally on CPU, no
  account required.
- Or solve on NVIDIA Audio2Face-3D — your own card, or a GPU rented by the
  hour.
- Emotion as an input, not only a reading: play a line angrier than it was
  recorded, or let Audio2Emotion read the delivery frame by frame instead.
- **Retargets curves between face vocabularies and reports coverage** — the
  problem this set exists to solve, because a solver and a rig speaking
  different vocabularies produce a "successful" solve that animates nothing.
- Bakes to a sequence and a montage, wired for the dialogue that plays it.

**Status:** working; multiple paths verified end to end.

**Members:** `FaceForge` (core), `FaceForgeMetaHuman` (registers the
engine's own audio-to-face solver), `FaceForgeACE` (NVIDIA Audio2Face-3D,
containerised), and two toolsets.

---

## MontageForge — from clip to gameplay, without the busywork

**Goal:** an animation sequence becomes something an ability can actually
play — the montage built from a recipe, with gameplay-event notifies at
authored times.

- Build a montage from a recipe in one step: asset, slot, notifies, blend
  settings.
- Fire gameplay events at authored moments — the notify almost every GAS
  project rewrites from scratch, shipped once.
- Author timings as fractions of the clip, so a regenerated animation
  can't quietly break them.
- Capture hand-tuned timings back into the recipe, instead of losing them
  on the next rebuild.
- Nothing rebuilds unless told — hand-tuning is never silently overwritten.

There is no generation in this set at all. It's ordinary production
tooling, just as useful when every clip in the project came from mocap.

**Status:** working, and proven in a live game, not only in the editor.

**Members:** `MontageForge` (core), `MontageForgeToolset`.

---

## MeshForge — a prop from a prompt

**Goal:** a description or a reference image becomes a game-ready static
mesh — textures, materials, collision and lightmap UVs included.

- Describe a prop, or drop a reference image, and get mesh candidates back.
- Import with real-world scale and a floor pivot already set.
- Choose candidates without paying for generation again on a re-import.

**Status:** developer preview. Concept generation, mesh generation and
import work today; the free Unreal-side refinishing pass — collision,
lightmap UVs, Nanite policy — is designed but **not yet shipped**. The
fitted-garment pipeline that sits on top of a mesh (pose-matched clothing)
is a separate, paid plugin: `MeshForgeGarment`.

**Members:** `MeshForge` (core), `MeshForgeCloud` and `MeshForgeTrellis`
(providers), `MeshForgeGarment` (paid post-process), two toolsets.

---

## SurfaceForge — materials without leaving the editor

**Goal:** a prompt or a reference image becomes reviewable PBR material
candidates, one of which graduates into an Unreal material asset.

- Turn a prompt or a reference image into material candidates.
- Graduate a chosen candidate into a full PBR map set.
- Provider-neutral and built around local-first generation — the same
  candidate/review/graduate shape as every other set, applied to materials.

**Status:** newest set in the family, editor-only. Treat it as early —
this page will be corrected as it's used on real content.

**Members:** `SurfaceForge` (core), `SurfaceForgeLocal` (StableMaterials /
MatFuse on your own GPU), `SurfaceForgePatina` (hosted, metered), a toolset.

---

## PerformanceForge — one performance, end to end

**Goal:** a miniature performance stage inside Unreal — record microphone
and optional webcam takes, route them through speech and face processing or
keep the performance as-is, and bake the chosen result onto a playable
character.

- Record voice and optional video without leaving the editor; the take
  clock is measured, not assumed (real sample rate, real fps, real stream
  offsets).
- A planned session can save every recording and explicitly forbid provider
  spend until someone reviews and chooses — "record now" and "spend now"
  are different decisions.
- Layers control vocabularies so mouth motion doesn't flatten the brows or
  erase the rest of the performance.
- Keeps recorded and generated alternatives side by side; choosing one
  never deletes the others.
- Bakes onto the target character, then marks the line "recorded" while
  keeping its generated ancestry.

**Status:** working development build, and the family's clearest proof
path (a person to a playable character, verified end to end). This is
Automation Forge's **paid** production-layer plugin — the core has no
public source; the planning-and-take-review surface is open as a toolset,
because publishing the agent surface is the point even where the
implementation is sold.

**Members:** `PerformanceForge` (paid core), `PerformanceForgeToolset`.

---

## Tools — MeshWeightRemap

**Goal:** a utility rather than a full set — remaps skin weights from a
leader pose across a whole folder of garments in one pass, instead of one
right-click at a time.

**Status:** working. Its toolset lets an agent batch a whole wardrobe
folder in one job.

**Members:** `MeshWeightRemap` (core), `MeshWeightRemapToolset`.

---

## What's next

The whitepaper names three more sets on the roadmap: **VisualForge**
(images and textures on the same provider registry), **ArtForge** (one
versioned style source every other Forge generates against), and continued
work hardening `AutomationForge`'s executor and ledger. None of that is a
shipping claim — see the honesty rule in [MANIFESTO.md](MANIFESTO.md) §12.
