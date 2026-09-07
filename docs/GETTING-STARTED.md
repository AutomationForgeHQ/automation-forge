# Getting started

The short path from nothing installed to a Forge set running in your
project.

## 1. Get the hub

Download from **[kovati.dev/download](https://kovati.dev/download)**, or
take the assets directly from the [latest
release](https://github.com/AutomationForgeHQ/automation-forge/releases/latest):

- **`AutomationForge-Setup.exe`** — the installer. Per-user, no
  administrator prompt. Installs the hub and the `forge` command line, adds
  `forge` to `PATH`, and can start the hub with Windows.
- **Portable**, if you'd rather not install anything: `AutomationForge-Hub-win-x64.zip`
  and `forge-win-x64.zip` separately, plus `SHA256SUMS.txt` to check them.

Requirements: Windows 10 or 11, 64-bit, and Unreal Engine 5.8.

The installer is unsigned until the code-signing certificate arrives —
Windows will ask you to confirm the first run.

## 2. Create an account and sign in

Nothing installs without one. In the hub, top right: **Sign in.** It opens
[app.kovati.dev](https://app.kovati.dev) in your browser — create an
account there with Google, GitHub, or email and password — and say **Yes,
connect it** when asked. No password ever passes through the desktop; see
[ACCOUNTS.md](ACCOUNTS.md) for exactly how that works and what the hub and
CLI show once you're in.

## 3. Add a Forge set

In the hub's **Plugins** tab, pick a set — say, MotionForge — and press
**Add & install**. Free plugins are added to your account and installed in
the same click; a paid member of a set is named and left out until you buy
it. The same works from the CLI:

```
forge install motionforge
```

`forge list` shows every set, what's on your account, and what's installed;
`forge engines` shows which Unreal installations the hub found. The full
command reference is in the [repository root README](../README.md#the-hub-and-the-cli).

## 4. Open Unreal

Open your project — or a fresh 5.8 project — and the plugin is already
enabled. If you installed to a specific project rather than the shared
engine location:

```
forge enable MotionForge --project C:\Games\MyGame
```

Each set's editor panels are under **Automation Forge** in the toolbar; the
same operations are exposed to an agent as MCP tools by that set's toolset
plugin (see [PLUGINS.md](PLUGINS.md)) — nothing here requires the toolset
to also be installed, but installing it is what lets an agent do the same
work.

## 5. Know what you're running

- [PLUGINS.md](PLUGINS.md) — what each set does and its honest status.
- [DISTRIBUTION.md](DISTRIBUTION.md) — which plugins are open, Fab or paid.
- The hub checks for updates every four hours and shows a Windows
  notification when one's ready; `forge update` does the same from the CLI.

## If something needs a provider key or a local runner

Some sets need an API key (a hosted voice or motion provider) or a local
model runner (a Docker container, or a GPU rented by the hour). The hub's
**Keys** and **Runners** drawers cover both — keys go straight into the
Windows Credential Manager, and a runner can be started or stopped from
there before you open the editor. See the root README's [machine
configuration](../README.md#how-it-knows-any-of-that-configforgemachinejson)
section for what a plugin declares and why the hub — not the editor — is
what starts and stops a rented GPU.
