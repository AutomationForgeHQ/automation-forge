# Playable Ops

*The short version. The long version is the [whitepaper](https://kovati.dev/whitepaper); the values version is [MANIFESTO.md](MANIFESTO.md).*

## The gap Automation Forge is built for

AI made a candidate cheap: an animation, a voice take, a face solve, a mesh.
It did not make that candidate part of a game. The result still has to fit a
character, respect timing, carry gameplay events, survive review, connect to
the right target, remember what produced it, and be replaceable when the real
work arrives.

> **On paper** — "Inject stimpack."
> **A generated candidate** — a four-second character clip.
> **A playable operation** — motion on the target skeleton, prop alignment,
> montage slot, gameplay-event notify, timing, blend and ability binding.

We call the distance between the first and the third the **playable gap**.
Most ambitious first games die there — not because the developer can't
imagine the game or produce any single asset, but because every verb asks
them to become a temporary animation engineer, voice producer, technical
artist and build manager before they can test the design.

**Working definition:** Playable Ops is the operational discipline and
software layer that turns creative intent into content that is playable,
reviewable, traceable and replaceable.

## Two-speed content

Generated and authored content have different jobs, and the system must
never confuse them.

| | Fast / generated / recorded candidate | Committed / authored / final |
|---|---|---|
| **Job** | Let the team feel and test the game | Carry the shipped experience |
| **Cost** | Minutes, cents | Specialist time, vendor budget |
| **Lifetime** | Replaceable without ceremony | Owned and maintained |
| **Editing rule** | Don't silently hand-edit while calling it reproducible | Edit as the work requires |

## Graduation: the rule that keeps speed from becoming debt

Generated assets are never hand-edited invisibly. The moment a developer
adjusts the montage timing, an actor records the line, an animator fixes the
clip, or an artist remodels the prop — **the result has graduated**.
Automation no longer owns that target and must not overwrite it. A
force-regenerate command is not a licence to destroy authored work.

Three honest futures for any candidate: **keep** it, **fix** it by hand (it
graduates), or **replace** it with a mocap take, a recording, a commissioned
mesh or final animation landing in the same target.

## Trinity: people and agents as equal clients

Software used to be a line — software on one side, a person on the other.
Agents add a second user: not a macro clicking a panel built for someone
else, but a client with its own discovery model, failure modes and
permission risks. Automation Forge is built as a triangle instead: **software,
human and agent**, with no core capability written for a person and adapted
for a machine later.

| Human client | Shared production core | Agent client |
|---|---|---|
| Native Unreal panels, previews, take strips | Definitions, typed operations, cost gates, ledger, provenance, permissions | Tool registry, reflected schemas, explicit failures |
| Enters credentials, approves destructive actions | The same boundary regardless of caller | May use approved capabilities; cannot write keys or delete unrecoverable work |
| Judges performance and aesthetics visually | Selections and evidence stay first-class data | Can batch, inspect, compare and retry without inventing hidden state |

**Different clients. Equal reach. One source of truth.**

That is also why every core plugin ships a matching *Toolset* plugin — the
same subsystem exposed as native tool calls, adding nothing of its own. See
[PLUGINS.md](PLUGINS.md).

## Providers are infrastructure, not the product

Today's model is the worst model we will ever ship on. A production system
built around a specific vendor inherits that vendor's quality ceiling,
pricing changes and outages. So core plugins never name a provider — a
provider registers a capability, its inputs, whether it's metered, and its
readiness — and removing one removes a route, not the definitions and
targets built on top of it. A small studio might block out motion locally,
regenerate the hero shots on a provider with better hands, keep secondary VO
hosted, and run faces locally — one project, several origins, one lifecycle.

**The rule:** estimate before spend. Separate expensive generation from free
Unreal-side refinishing wherever possible.

## What Playable Ops is not

- Not "AI makes the whole game." The discipline matters more, not less, when
  output is abundant.
- Not a mandate to ship generated assets — a candidate may ship, be edited,
  be recorded, be commissioned, or be discarded.
- Not one universal graph — different teams keep their own frameworks,
  providers and approval rules.
- Not enterprise process imposed on a solo developer. For one person it
  should feel like good defaults and a clean history, not a procurement
  portal.
- Not provenance as paperwork — the ledger is a by-product of doing the
  work, not another form to fill in.

## Three users, one path

We design against three archetypes, not three market sizes:

- **A first serious solo developer** needs safe defaults, visible cost, and
  a path through work they've never done before.
- **A 5–10 person indie studio** already knows the problem and wants
  throughput without chaos — reusable definitions, provider choice, a
  ledger that stays out of the way.
- **A larger studio and its vendors** eventually need the same lifecycle at
  another scale: production briefs, approvals, audit, source-control-aware
  delivery.

Those are three levels of the same problem, served in that order: help one
developer finish, prove the path with indie teams, grow the same lifecycle
into studio orchestration.
