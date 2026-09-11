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

- Wire the sets that declare pipeline nodes into one pipeline — a definition
  anyone can author, no code involved.
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

Editor-side only; nothing here ships inside your packaged game. Nothing in
the executor needs the editor UI either, so a commandlet of your own can
advance a run — we do not ship one yet.

**Status:** working developer preview. The graph, the gates, the executor
and the ledger all run; systematic reliability testing and the final
authoring surface are still ahead.

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

## SpeechForge — every line in the game, spoken, in every language

**Goal:** a written line becomes a `USoundWave`, imported with
character-level timing attached, on your own provider account — and stays
correct as the script, the cast and the languages around it change.

The editor surface is one Speech Library panel with six pages, each doing
one job in the screenwriter's own order: **Ingest** (pull lines in from an
existing script or dialogue asset), **Cast** (define speakers and the
voice each one speaks in), **Write** (author lines and separately, their
direction), **Produce** (price a run and generate it), **Perform** (hand a
line off to a recorded performance), **Localize** (translate and dub into
other languages).

- Voice a whole script in one pass; see the price before a single line
  generates, and re-run safely — unchanged lines are skipped, never
  re-bought.
- **A line resolves its voice, it doesn't declare one** — walking the
  line's own override, the speaker's character sheet, the bank's default
  and the project default in that order, and every resolution records
  *which* of those answered, so "why is this line in the wrong voice" is
  never a four-asset guessing game.
- **Staleness is precise, not a hash mismatch.** A retuned voice marks its
  whole cast stale, but the report says exactly what changed —
  `stability 0.50 → 0.35`, not "the hash differs" — because what a line
  was generated with is stored alongside it.
- **Localize a whole scene**: one sibling Speech Bank per language, joined
  to the source by line id; updating translates only stale or missing
  lines, billed per character, and a face bank clones alongside it for the
  same language automatically.
- **Dub a recorded performance across languages** without losing it —
  `Dub from Source` carries the actor's original pacing into the target
  language, and remembers exactly which recording it was dubbed from, so a
  later re-record of the source flags the dub as stale instead of silently
  going on speaking a performance the scene has replaced.
- Origin (`Generated` / `Recorded` / `Edited` / `Accepted`) and status
  (`Draft` / `Generating` / `Generated` / `Failed`) are kept as two
  separate facts on purpose — a **recorded** line whose script changed
  underneath it is the one report here with real money attached, and it
  would be inexpressible if the two were collapsed into one status.
- Deliberately stops at the wave: no dialogue system, no subtitles, no
  gameplay framework baked in. Binding it to yours — Narrative Pro's
  `NP_VoiceOver` today — is an adapter's job, not this plugin's.

**Status:** graduated to **beta** — "well tested and established" is the
call actually recorded against this release, covering the full
Ingest/Cast/Write/Produce/Perform/Localize surface, localization and
dubbing.
`SpeechForgeElevenLabs` (the live hosted provider) graduated the same way,
the same day. `SpeechForgeDeepL` (localisation) shipped 2026-09-08 — a
**paid** provider, priced like Performance Forge and the Garment Fit
pipeline rather than a free edge; the built-in, keyless `Pseudo` provider
keeps the Localize page exercisable without any account either way. Since
0.1.2 it owns more than the translation: its banks know what each line is a
translation of, so producing one generates the lines that were synthesised
and **dubs** the ones that were performed, then points the faces serving
them at the new audio — a localised line keeps the acting captured for it.

**Members:** `SpeechForge` (core), `SpeechForgeElevenLabs` (hosted
provider, live, beta), `SpeechForgeDeepL` (paid localisation provider), and
a toolset.

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
  recorded, or let Audio2Emotion read the delivery instead, sampling about
  once a second.
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

## MeshForge — a prop from a prompt, dressed and fitted

**Goal:** a description or a reference image becomes a game-ready static
mesh — collision, lightmap UVs, Nanite policy, real-world scale and a floor
pivot included — with a matching pipeline that fits a generated garment
onto a character instead of leaving it as a separate prop.

- Describe a prop, or drop a reference image, and get mesh candidates back
  from two live hosted providers — **Tripo** and **Meshy** — through one
  registered account each, both wired up as `MeshForgeCloud`.
- **Free Unreal-side refinishing, already shipped.** Collision, hull count,
  lightmap resolution, Nanite and real-world size/pivot all change in about
  a second with no regeneration — the generation loop and the refinishing
  loop are deliberately separate, so a crate that's the right shape but the
  wrong size never needs paying for again.
- Meshy's **Smart Topology** mode generates a low-polygon mesh instead of
  decimating one — measured at ~6,275 triangles for 15 credits against
  ~1.9M for a 4K PBR pass on the same reference image, visually close at
  normal distance.
- A free, local, seeded provider — **Microsoft TRELLIS.2** in Docker,
  released 2026-09-08. The model's code and weights are MIT licensed, with
  one real asterisk: it conditions on a Meta-gated encoder, so you accept
  those terms and request access once before anything generates.

### Paired with Garment Fit: a small, from-scratch fitting pipeline

`MeshForgeGarment` is a post-processing step, not a separate product: point
it at a character's body mesh and a generated or DCC-made garment, and it
exports the body in its reference pose, hands both to a Blender fitting
runner, and imports the garment back wrapped around that body with its
vertices, UVs and materials untouched. Rigging and skin weights stay in
Unreal, where the engine's own weight transfer does them.

The core idea — pose the garment and a handful of the body's joints by
hand or from a preset, let the solver bind the cloth to that posed skin,
then straighten the body back to its reference pose and carry the garment
with it — is the same shape of idea academic work on garment fitting calls
*pose matching*. This is Automation Forge's own small implementation of
that idea, built from scratch against our own solver and our own runner;
no claim is made to reproducing anyone's proprietary system, only to the
same underlying trick, at a much smaller scope.

Two fit modes: **Place** (scale, align, clear the skin — for a garment
already made for this exact body, which keeps its drape) and **Fit**
(articulate, project, relax — for a garment made for some other body, at
the cost of some drape). The route that keeps the most drape end to end:
render the character as a grey mannequin, dress that picture with Tripo's
image editor, extract the garment, generate it in Meshy at low-poly and
in the character's own pose, then **Place** it — about 35 Tripo + Meshy
credits, measured start to finish.

Measured (2026-09-06, Blender 5.2): a Tripo T-shirt (10,130 vertices)
fitted in 1.9s with no vertex inside the body — though nine triangle
centres still penetrate, and the pipeline's own `PRESERVATION_RESULTS.md`
makes no collision-free or animation-ready claim; a Meshy jacket in 1.8s
with its pockets and buttons where they were generated; a skeletal CC5
shirt (13,610 triangles, 62 influenced bones) validated triangle-for-
triangle in UE 5.8 after skinning.

**Status:** `MeshForge` and `MeshForgeCloud` (Tripo + Meshy) graduated to
**beta** — well tested and established, not experimental. `MeshForgeTrellis`
shipped on 2026-09-08, its first release, and stays **experimental**.
`MeshForgeGarment` is **working and experimental**, sold as a paid plugin
rather than free — see [DISTRIBUTION.md](DISTRIBUTION.md) for why it's
priced like Performance Forge rather than shipped as a free add-on.

**Members:** `MeshForge` (core), `MeshForgeCloud` (Tripo + Meshy provider),
`MeshForgeTrellis` (local, free), `MeshForgeGarment` (paid
garment fitting), two toolsets.

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

**Goal:** a utility rather than a full set — moves skin weights off every
bone a leader mesh will never drive onto the nearest ancestor it does, so a
garment can be leader-posed without its cuffs staying behind.

**Status:** working. The right-click dialog takes a multi-selection, and
the toolset exposes the same three calls to an agent — there is deliberately
no single batch tool, so a whole wardrobe is a loop over a folder rather
than one call.

**Members:** `MeshWeightRemap` (core), `MeshWeightRemapToolset`.

---

## What's next

The whitepaper named **VisualForge** as a roadmap set; that promise has
since been kept under other names — hosted image generation went to
MeshForge Cloud and materials to SurfaceForge, both shipped. What remains
on the roadmap is **ArtForge** (one versioned style source every other
Forge generates against) and continued work hardening `AutomationForge`'s
executor and ledger. None of that is a
shipping claim — see the honesty rule in [MANIFESTO.md](MANIFESTO.md) §12.
