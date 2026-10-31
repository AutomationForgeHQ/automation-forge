# Automation Forge hub and CLI

Every released version of the hub and the `forge` CLI, newest first. A stable release publishes **one**
section of this file, the one whose heading starts with its version, as its release notes. Write for someone
who installs the update, not for the commit log.

Headings are `## <x.y.z> — <date>`. Use `Added` / `Changed` / `Fixed` / `Compatibility` / `Known issues`, only
the ones that apply. A version that has not shipped says `unreleased` in place of the date; set the date when it
is tagged.

The file started with 0.4.3. The notes for 0.4.1 and 0.4.2 are copied from their release pages. Versions 0.2.0
to 0.4.0 were released without notes, so their sections were written afterwards from the commits that went into
each one.

## 0.4.4 — unreleased

### Fixed
- **Installing into a project or engine on another drive works.** When your project or engine was on a different
  drive from Windows (for example `D:` or `E:`), every install stopped with *"Source and destination path must have
  identical roots. Move will not work across volumes."* and nothing was installed. The hub unpacks downloads on
  your Windows drive, and it now copies the plugin over when the destination is on another one.
- **A failed update keeps the version you had.** If an update could not finish, for example because Unreal Editor
  had the plugin loaded, the hub could already have deleted part of the installed version. Now the installed
  version stays exactly as it was until the new one is completely in place.

## 0.4.3 — 2026-10-28

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

## 0.4.2 — 2026-10-04

### Changed
- **Your account now lives on Kovati's own cloud.** Sign in again, from the hub's avatar menu or at
  [app.kovati.dev](https://app.kovati.dev). Accounts made before 4 October 2026 don't carry over, so create yours
  once more; free plugins go back on with one click. Plugins you already installed stay installed.
- **Hubs older than 0.4.2 can no longer use your account.** Take this update when the hub offers it, or download
  it below.
- The publisher and copyright now read Bojan Andrejek / MetaWorx LLC.

## 0.4.1 — 2026-09-22

### Added
- **What's new**, beside a plugin's Update button. It lists every release since the version you have, the ones you
  skipped included, with each one's notes, so you can read an update before taking it. Paid plugins have notes too.

### Fixed
- After buying a plugin on the web, **Refresh** now shows it as yours. The row used to keep saying Buy until the
  hub restarted.
- Paid plugins' versions come from the paid catalogue, where their builds are now delivered, rather than from the
  public releases.
- `forge install` with a `--project` or `--engine` path that isn't there says so, instead of printing a stack
  trace. The exit code is 2 when something you named isn't there, and 4 for anything about your account.

## 0.4.0 — 2026-09-07

### Added
- **One account for the site, the hub and the CLI.** Every plugin, free or paid, is added to your account before
  it downloads. A free plugin is added with one click, or by itself the first time you install it.
- **Each plugin's row says where it stands:** Sign in, Add & install, Install, or Buy for a paid plugin your
  account doesn't hold. Buy opens that plugin on the account site.
- **`forge claim <set|plugin>`** adds free plugins, and the free plugins they depend on, to your account without
  installing them.

### Changed
- `forge install` and `forge update` need you to be signed in (`forge login`).
- **Install set** installs what your account can have and names the paid plugins it left out, instead of skipping
  them silently.
- `forge update` carries on past a plugin that fails instead of stopping.

### Compatibility
- Scripts can tell the failures apart: exit code 4 means not signed in, 5 means something you asked for is paid
  and not on your account (the rest is still installed).

## 0.3.1 — 2026-08-30

### Changed
- **Keys** shows a key that several plugins share, such as Runpod or Hugging Face, once, in a General section,
  instead of once under every plugin that uses it.

### Added
- A key's card lists the access you must also be granted, with a link for each and how long it takes. A Hugging
  Face token alone does not unlock gated models such as Meta's Llama or NVIDIA's Audio2Emotion.

## 0.3.0 — 2026-08-29

### Added
- **Tabs:** Plugins, as before, and **Runners**. Runners shows each plugin's local runner container with Start
  and Stop, and a GPU rented on Runpod with Start, Stop and Release. It says what each action does to the bill;
  a running pod shows its hourly cost.
- **Keys**, a drawer opened from the header: every key the installed plugins need, set, replaced or cleared in
  the same Windows Credential Manager entry the editor uses, so a key set in either place works in both.
- Plugins tell the hub what they need from the machine (keys and runners) in `Config/ForgeMachine.json`, so the
  hub can help without Unreal open.
- A setting to point the hub at a working tree of plugins, for people writing plugins.
- Without a Runpod key, the Runners tab says a GPU can be rented by the hour and offers to set the key, instead
  of showing an empty list.

### Fixed
- Keys are stored as UTF-8, the way the editor stores them. A key set in the hub before this read back as
  unreadable text in the editor.

## 0.2.3 — 2026-08-28

### Added
- The editor's *Open the hub* opens it on the engine the editor runs from (`--engine <version or path>`).

### Changed
- Before writing into an engine, the hub says when Unreal Editor is running from it, since a plugin the editor has
  loaded can't be replaced. A locked folder fails with that sentence instead of a raw error.

## 0.2.2 — 2026-08-28

### Fixed
- The installer can update a hub that is running in the tray: it asks the hub to quit, and closes it only if it
  won't. Closing from Windows or the installer really closes the hub instead of hiding it.

## 0.2.1 — 2026-08-28

### Added
- **Sign in** from the hub and with `forge login`, through the account site in your browser. Your password never
  passes through the desktop, and the session is kept encrypted for your Windows user. `forge logout` and
  `forge whoami`; `forge login --no-browser` prints the link for remote sessions.
- Your account in the hub's header: your photo or initials, a menu with Account and Sign out, and an account
  panel.
- Installing into an engine also installs the editor plugin (the menu and the update badge in Unreal).

## 0.2.0 — 2026-08-28

### Added
- **First release of the hub and the `forge` CLI:** install, update and remove Automation Forge plugins in an
  engine or a project, from a window or the command line.
- An installer that needs no administrator rights, portable zips and checksums.
- **The hub updates itself**, on a stable or a nightly channel shared with the CLI (`forge channel`).
- It stays in the tray, checks every engine and project it installed into every four hours, and shows a Windows
  notification when something new is out. Settings for starting with Windows, the tray and notifications.
