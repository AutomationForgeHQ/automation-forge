# Kovati Hub v2: build notes

Working notes for the `hub-v2` branch, built from `KOVATI_HUB_V2_UX_SPEC.md` (19 November 2026). Local only:
nothing on this branch is released, pushed or published.

## Slice 0: three technical tests (19 November 2026, 02:30)

Run with the headless renderer in `tests/Forge.Hub.Snapshots` (Avalonia 12.1.1 headless platform, drawn by Skia,
no desktop window):

    dotnet run --project tests/Forge.Hub.Snapshots -- <out dir> slice0 <loop.webp> <still.jpg>

| # | Question | Answer | Evidence |
|---|---|---|---|
| 1 | Does `FontStretch="SemiExpanded"` widen the embedded variable Archivo? | **No.** Neither does `FontWeight`: Avalonia draws an embedded variable font at its default instance only (wght 600, wdth 100). Every Archivo text in 0.4.4 was SemiBold. "What do you want to make?" at 40 px measured 502 px at Normal, SemiExpanded and Regular alike. | `hub_slice0_1_fontstretch.png` |
| 1 | Fallback taken | The written fallback, extended: four static cuts made with fontTools (`tools/make_archivo_cuts.py`): `Archivo` Regular 400, Medium 500, SemiBold 600, and `Archivo Display` SemiBold 600 at width 115 for the `display-*` styles. After the swap Regular measures 486 px, SemiBold 502 px, the display cut 592 px. The variable file is removed from the assets. | `hub_slice0_1b_static_cuts.png` |
| 2 | 264 × 330 card, radius 16, `ClipToBounds`, picture at 1.04, amber glow: clean corners at 100 % and 150 %? | **Yes**, with one rule: the ring is its own `Border` drawn over the clipped picture (a ring on the clipping `Border` is painted under a picture that overflows it), and the glow sits on an outer `Border` that does not clip. Both a `ScaleTransform` and a negative margin give clean corners. 150 % was rendered as the whole tree under a 1.5 scale, which is what a 150 % window applies at its root; an off-screen `RenderTargetBitmap` at 144 dpi draws clips and transforms at mixed scales in headless, and is not a test of a window. | `hub_slice0_2_card_100.png`, `hub_slice0_2_card_150.png`, `hub_slice0_2_card_150_rendertransform.png` |
| 3 | `SKCodec` on an animated WebP from `v2/public/media/motionforge/card.mp4` (12.2's ffmpeg line) | **Yes.** SkiaSharp 3.119.4 opens it as WebP: 480 × 600, 67 frames, 83/84 ms each (5.58 s), 447 KB. Frame 33 decoded with its required frame and drawn into an Avalonia `Bitmap`. The film-strip fallback is not needed. | `hub_slice0_3_webp_frame.png` |

The images are in `C:\UNREAL\Colony_NP24\.playwright-mcp\` (outside this repository).

## What is built (slices 1 to 4)

Slice 5 is built for the catalogue and the stills (below); its loops (`LoopImage`) and the film overlay (web view)
are not: the site has no WebP loops yet, so cards keep their stills and "The film" still opens kovati.dev. Slice 6
(the cart, the offline strip, `kovatihub:`) is not started: it waits on the account app's cart.

## Slice 5: the catalogue and its pictures from the website (19 November 2026, evening)

- The website writes the catalogue: `npm run hub-catalogue` in `AutomationForge-website/v2` builds
  `v2/public/hub/catalogue.json` from the site's own data (`lib/catalogue.ts`, the track files, `lib/tools/*`,
  `lib/featured.ts`) plus `lib/hub.ts` (only what the site has no field for: capabilities, agent components, needs,
  runners, `always`). It refuses a component the manifest lacks, a product claimed twice, a dangling capability.
  It deploys with the site at `https://kovati.dev/hub/catalogue.json`.
- The Hub fetches it when the window opens and with every update check (`CatalogueClient`, ETag, 30 s timeout),
  keeps it in the data folder, and uses the newest `generatedAt` of that copy and the installer's. A newer copy
  reopens the pages in place. Offline or failing, nothing changes and nothing is said.
- Stills: dev folder, disk cache (`<data>\media`, keyed by the whole path with its `?v=`), installer's set, then
  a download from the site's own host (`MediaCache`: images only, 12 MB a file, three at a time, 200 MB in all).
- The Kovati foundation: `always` holds AutomationForgeHub (the menu and the keys) and AutomationForge (the
  ledger), with the ledger's agent tools behind the agents switch. Every engine install carries them first;
  Library and Components show them as KOVATI FOUNDATION.
- `python tools/sync_hub_catalogue.py` (no arguments) copies the generated file and its stills into
  `src/Forge.Hub/catalogue`: the first-run and offline copy only.

### Adding a product or a kit, without a Hub release

1. Website: add it to `v2/lib/catalogue.ts` (and its page in `v2/lib/tools/` if it has one); in `v2/lib/hub.ts` add
   only what the site lacks (agent components, needs, a capability on a track). Its pictures go under `v2/public/media`.
2. Its plugin must be in the release manifest (released the usual way); the Hub installs through the manifest.
3. `npm run hub-catalogue` in `v2/`, commit the files it names, deploy the site. Hubs pick it up at their next start
   or update check (every 15 minutes, with ETags: an unchanged catalogue is a 304).

### Switches for local work

    $env:FORGE_SITE_URL = 'http://localhost:3002'      # catalogue and pictures from the v2 dev server
    $env:FORGE_CATALOGUE_URL = 'off'                   # or another address for the catalogue alone
    $env:FORGE_HUB_OPEN = 'product:roads-kit'          # browse | library | components | track:<slug> | product:<slug>

## 0.5.0-beta.3: Unreal Engine versions and projects (20 November 2026)

Built from `HUB_ENGINES_PROJECTS_DESIGN.md` (slices 1, 2, 3 and 5 of it; the CI matrix of slice 4 is not here).

- **Targets.** The footer, a track's "Install into" and a product page choose from one list: every Unreal Engine
  ("Unreal Engine 5.8.4 (all projects)"; "· no Kovati builds yet" when the manifest has none for it) and every
  project whose engine is on this computer ("Colony_NP24 · project on Unreal Engine 5.8.4"). Choosing a project
  selects its engine too. `HubState.LastTarget` is the target's plugin root, an engine's or a project's.
- **Availability per version.** `TrackPlanner.AvailabilityOf`: built, not built for this version, or not released.
  Rows, cards and product pages say "Not built for Unreal Engine 5.7 yet." and are not installable; a track on
  an engine without any build says "No builds for Unreal Engine 5.7 yet" and which versions have them.
- **Projects page** (a place in the bar, between Library and Browse: Library is about the one chosen target, this
  page is about every project, and a project is itself a target). Per project: engine (from EngineAssociation:
  a version, a source build's GUID through `HKCU\...\Unreal Engine\Builds`, or the engine folder it sits in; "not
  installed" otherwise), C++ or Blueprint, source control, read-only, open in Unreal; per Kovati tool: in the
  project (Kovati Hub's own folder or a copy of the project's own), from the engine, or both (the project copy
  overrides), on or off and why, version, update. Actions: turn on/off (one .uproject entry), update or remove Kovati
  Hub's own copy, "Install tools here" (the project becomes the target), open folder, open in Unreal.
- **Discovery** (`ProjectLocator`, the same for `forge projects`), never a disk search: every
  `%LOCALAPPDATA%\UnrealEngine\<version>\Saved\Config\WindowsEditor\EditorSettings.ini` (`RecentlyOpenedProjectFiles`,
  `CreatedProjectPaths` one level deep), the launcher's `%LOCALAPPDATA%\EpicGamesLauncher\Saved\Config\WindowsEditor\GameUserSettings.ini`
  `[Launcher] CreatedProjectPaths`, `Documents\Unreal Projects`, the folders in Settings → Project folders (two
  levels, 4,000 folders, 4 seconds each, then it says where it stopped), the receipts and the projects chosen
  before. Merged by path; gone, `Saved\`, `Intermediate\` and `HostProject` projects drop out. Off the UI thread, at
  start (after the list of tools) and on Check now; the last result is kept in `hub-state.json` (`projectCache`,
  `projectFolders`; unknown fields are now kept when the file is written).
- **Safety.** Nothing is written to a project whose editor has it open (`EditorProcesses`: the .uproject on an
  UnrealEditor command line). An editor whose command line cannot be read asks first. A read-only .uproject is
  refused, never made writable. C++ projects: "rebuild it in your IDE"; Binaries and Intermediate are never
  touched. Source control: a line about the changed .uproject or the new Plugins/AutomationForge; no VCS command.
- **Foundation policy for projects.** A project install carries the Kovati foundation (`always`) into the project,
  each piece its engine does not already have (`PlanInputs.FoundationInProject`); what the project has from its
  engine or a copy of its own is never installed a second time (Unreal refuses two plugins of one name) and never
  offered as a project update.
- **.uproject edits** keep the file as it was: one token, or one entry in the shape of the others (tabs/CRLF,
  spaces/LF, one-line files), BOM or none, key order, trailing newline; a temp file and a move.
- **CLI.** `forge projects [--json]`; `forge engines` shows builds per version; `--project` resolves a GUID
  association through the registry (before, a GUID was taken for a version and found no build); writes into a
  project refuse while its editor is open (`--yes` when it cannot be told).

Switches: `FORGE_HUB_OPEN=projects`, `FORGE_HUB_TARGET=<project name or .uproject>`, `FORGE_UNREAL_RECORDS=off|<folder>`
(no Unreal records, or a folder read in place of `%LOCALAPPDATA%`). Screens from fixed data:

    dotnet build tests/Forge.Hub.Snapshots -o D:\UNREAL\ColonyDownloads\hub-b3-snapshots-bin
    $env:FORGE_REPO = 'C:\UNREAL\AutomationForge'
    D:\UNREAL\ColonyDownloads\hub-b3-snapshots-bin\Forge.Hub.Snapshots.exe <out dir> beta3

**Before another Unreal version can be offered** the manifest must carry builds with `engine: "5.7"` (or 5.9):
the forge monorepo's `build-plugin.yml` builds 5.8 only (one `UE_ROOT`, one runner label). It needs a matrix per
version, runners with that Unreal installed, an add-an-asset dispatch for a version built later, and
`build_manifest.py`/`refresh_paid_manifest.py` already read the version from the asset name. Declared statuses
("planned", "preview") and `compatibleChangelist` are not in the manifest yet, so the Hub can only say "not built".

## The signed licence (licensing plan step 2, 20 November 2026; not released yet)

Built from `LICENSING_STRATEGY.md` (step 2) and `KOVATI_HUB_PLAN.md` 5.3, against the account API's `GET /licence`.
Code: `src/Forge.Core/Licence/`; the window in `MainViewModel.Licence.cs`; the CLI `forge licence`.

- **When.** At sign-in (always), at start and from the 15-minute watcher (only once the last good fetch is 20 hours old),
  and always on Check now, Restore licence and `forge licence refresh`. A 429's `Retry-After` holds for every call
  (the server allows 20 an hour per account). After a 429, 5xx or licence-off the automatic check waits an hour.
- **Outcomes.** 200: verified, for this account, newer: stored. 401/403: the file is removed. 503 `licence-off` or a
  404 (route not there): "licences are off", nothing alarming. 429, 5xx, offline, a timeout: the file stays, nothing
  is said. A licence that does not verify or is for another account is never stored.
- **Keys.** `LicenceKeys.BuiltIn` (kid to base64url raw 32-byte Ed25519 key); `k1` is the live key. A kid holding
  `LicenceKeys.Placeholder`, or any key that is not 32 bytes, verifies nothing. Ed25519 is `BouncyCastle.Cryptography`
  (managed, no native library). `FORGE_LICENCE_DEV_KEYS=kid=base64url;...` adds keys only in a Debug build of
  Forge.Core or when `FORGE_HUB_INSTANCE` is set, never under a built-in kid, and a licence verified only by a dev key
  is never written into the real `%LOCALAPPDATA%\AutomationForge` (only into a `FORGE_DATA_DIR`).
- **Words.** `LicenceWords.Line`: "Licensed · bought 20 Nov 2026", "Test licence"; "Offline, 7 days left", "Running as
  Lite until you are online", "Not licensed: this runs as Lite" and "Sign in to get Full back" only for a product whose
  catalogue entry has `freeTierLive: true` (none today). Settings → LICENCE: account, what it covers, last refreshed,
  valid offline until, key, and Restore licence.
- **Screens.** `Forge.Hub.Snapshots.exe <out dir> licence` (headless, throwaway key). A real window against a fake API:
  `dotnet run --project tools/licence-fake-server -- --port 8791 --data D:\UNREAL\ColonyDownloads\hub-lictest-data`
  prints the variables for a dev instance (it writes a fake sign-in into that folder and refuses the real one); its
  modes (`ok empty grace expired other badsig off 401 429 500`) switch with `GET /_mode/<mode>`.

### The licence file: a contract for paid plugins

**Path.** `%LOCALAPPDATA%\AutomationForge\licence.json` (`forge licence path` prints it; under `FORGE_DATA_DIR` it is
in that folder). Written by Kovati Hub and `forge`; plugins only read it. It is replaced through a temporary file and a
move: read it whole and close it at once (a reader holding it open blocks the replace; the Hub retries for half a second
and keeps the old file).

**Format.** Exactly what `GET {api}/licence` returned, byte for byte:

    { "format": "kovati-licence", "v": 1, "kid": "k1", "payload": "<base64url>", "sig": "<base64url>" }

`payload` decodes to UTF-8 JSON:

    { "v": 1, "iss": "kovati", "sub": "<32 hex>", "issuedAt": "<RFC 3339 UTC>", "offlineUntil": "<issuedAt + 30 days>",
      "products": { "<pluginId>": { "how": "bought|granted|fab|pro|test", "since": "<RFC 3339 UTC>", "maxMajor": 1 } },
      "test": false }

`products` lists only paid products the account is licensed for (a plugin claimed free in a preview is not listed);
plugin ids compare without case. `sub` is the first 32 hex digits of SHA-256(`"kovati-licence:" + uid`), never the uid.
Unknown fields are ignored.

**Verify.** 1. `format == "kovati-licence"` and `v == 1`. 2. Look up `kid` in the plugin's own built-in table of public
keys (raw 32-byte Ed25519, base64url; `k1` = `NfOKjVJCzXOHKoc6EW24UqZzIYQ6HRJ-eoMysFYm76g`); an unknown kid fails.
3. Base64url-decode `payload` and `sig` (64 bytes); verify Ed25519 over the **decoded payload bytes**, never a
re-serialised copy. 4. Parse the payload: `v == 1`, `iss == "kovati"`.

**The 30-day rule.** Valid while `issuedAt - 15 min <= now < min(offlineUntil, issuedAt + 30 days)`. A clock earlier
than `issuedAt - 15 min` is not valid (it needs a refresh; it is never valid forever). From day 23 after `issuedAt`
show a quiet "offline, N days left". A product counts as licensed when it is listed, the licence is valid, and its
major version is at most `maxMajor`. Otherwise it runs as its free tier (Lite, or trial without export) and nothing
breaks. A plugin that is not signed in does not need the account: the file is the whole answer.

**Refresh from a plugin.** When the file is missing or more than a day old and the Hub may not be running, run the
installed `forge licence refresh` (`--json` prints `{ outcome, exitCode, path, message }`), then read the file again:

| Exit | Meaning | The file |
|---|---|---|
| 0 | A current licence is on disk (fetched now, or the same one again) | read it |
| 4 | Not signed in, or the account was refused (401/403) | removed, or never there |
| 5 | Not now: offline, rate limited (Retry-After), server trouble, the file was held open | as it was |
| 7 | The answer did not verify, or was for another account | as it was |
| 8 | Licences are not switched on at Kovati yet | as it was |

`forge licence [--json]` shows the state; `forge login` fetches a licence, `forge logout` removes it.

## Run it beside the installed Hub

    git -C C:\UNREAL\AutomationForge switch hub-v2
    dotnet build C:\UNREAL\AutomationForge\Forge.sln
    $env:FORGE_HUB_INSTANCE = 'v2'                                   # its own mutex and events: the installed Hub is left alone
    $env:FORGE_DATA_DIR = 'D:\UNREAL\ColonyDownloads\hub-v2-data'    # a throwaway data folder: signed out, no receipts
    $env:FORGE_MEDIA_URL = 'C:\UNREAL\AutomationForge-website\v2\public'   # optional: the products' interface shots
    & C:\UNREAL\AutomationForge\src\Forge.Hub\bin\Debug\net9.0-windows10.0.17763.0\AutomationForgeHub.exe

Quit it from its tray icon, or with the same `FORGE_HUB_INSTANCE`: `AutomationForgeHub.exe --quit`. Never run
`--quit` without `FORGE_HUB_INSTANCE`: a bare one asks the installed beta (`Local\KovatiHub`) to quit.
A build from source is still `AutomationForgeHub.exe` (the assembly keeps its name for the `avares://` resources);
only `tools/publish.ps1` renames the published program file to `KovatiHub.exe`.
Leave `FORGE_DATA_DIR` out to see your real receipts and sign-in (it then writes `hub-state.json` beside them; an
older Hub ignores that file). `FORGE_ENGINES=<folder>` limits it to one engine folder, e.g. a throwaway one.

Other switches for local work: `FORGE_CATALOGUE=<file>`, `FORGE_MANIFEST_URL=<file>`, `FORGE_RELEASES_API=off`,
`FORGE_HUB_SNAPSHOT=<file.png>` (draws the window itself into a PNG eight seconds after it opens).

## The beta beside the Automation Forge Hub (0.5.0-beta.1, 19 November 2026)

v2 ships as **Kovati Hub (beta)**, installed beside the Automation Forge Hub 0.4.x, which keeps working. Every name
the two could share is the beta's own, in `src/Forge.Core/HubIdentity.cs` (and its copy in the installer):

| | Automation Forge Hub 0.4.x | Kovati Hub (beta) |
|---|---|---|
| Installer AppId | `{4F0B9C7E-2A6D-4B1F-9E3C-7D5A1F0C8B21}` | `{E13BC642-3BBA-4D78-88D9-8D3767887CAE}` |
| Folder | `%LOCALAPPDATA%\Programs\Automation Forge` | `%LOCALAPPDATA%\Programs\Kovati Hub` |
| Program file (what each installer closes) | `AutomationForgeHub.exe` | `KovatiHub.exe` |
| Mutex, Show and Quit events | `Local\AutomationForgeHub` | `Local\KovatiHub` (+ `.<FORGE_HUB_INSTANCE>`) |
| Start with Windows (HKCU Run value) | `AutomationForge`, on by default | `KovatiHub`, off by default |
| forge on PATH | added by default | not added unless ticked |
| Release tags, installer asset | `v<semver>`, `AutomationForge-Setup.exe` | `kovati-hub-v<semver>`, `KovatiHub-Setup.exe` |
| Start menu | Automation Forge | Kovati Hub (beta) |
| Toasts (the toolkit keys them by program path) | its path, "Automation Forge" | its path, "Kovati Hub (beta)" |
| Beta-only files in the shared data folder | | `kovati-hub-notified.json`, `kovati-hub-releases.*`, `kovati-hub-elevated.log`, `hub-state.json` |

Shared on purpose: `%LOCALAPPDATA%\AutomationForge` (receipts, sign-in, settings, caches) and the engines.
`installed.json` is changed by reading it again under a lock file and writing it atomically; `settings.json` gets
only the fields this Hub changed. A 0.4.x Hub reads the same release list and skips `kovati-hub-v*` tags (its
`SemVer.TryParse` refuses them; `BetaSideBySideTests`).

A beta release must be a GitHub **pre-release, not latest**: the website's download buttons use
`releases/latest/download/AutomationForge-Setup.exe`. Never tag this branch `v*` (release.yml would build it as a
0.4.x-line release).

    tools/publish.ps1 -OutDir D:\UNREAL\ColonyDownloads\hub-beta    # KovatiHub-Setup.exe, KovatiHub-win-x64.zip, forge-win-x64.zip

"Check now" (gear menu, account menu, Library, Components) refreshes the manifest and releases, the catalogue,
the account's profile and what it owns, and the Hub's own releases, and ends on "Checked HH:mm"; the footer always
shows when the Hub last looked.

## Checks

    dotnet test tests/Forge.Core.Tests                               # planner, catalogue, checklist, updates, hub-state
    src/forge/bin/Debug/net9.0/forge.exe install --track animation --dry-run [--agents]
    dotnet run --project tests/Forge.Hub.Snapshots -- <out dir> screens   # every screen, headless, from fixed data
