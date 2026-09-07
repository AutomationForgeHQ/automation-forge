# Accounts

One account, three surfaces: the account site, the hub, and the `forge`
command line. **Nothing installs without it.** Every plugin — free or paid —
is added to an account first; then the hub installs what the account holds.

- A **free** plugin is added with one click, on the site or by the hub's
  *Install* itself, the first time.
- A **paid** plugin is bought on the account site, or bought on Fab and linked.
- Once a plugin is on the account, it is *yours* everywhere: the site says so,
  the hub shows *Install*, `forge whoami` lists it.

## Creating an account

Go to **https://app.kovati.dev** and pick one:

- **Continue with Google** or **Continue with GitHub** — a normal OAuth pop-up;
  nothing to type. The account takes the name and photo from there.
- **Email and password** — type both, press *No account yet? Create one.*, then
  *Create the account*. Eight characters at least. *Forgot the password?* sends
  a reset link.

Whichever you use first is your account; signing in later with the same email
through a different provider joins it to the same account.

## What you see there

- **Account** — name, email, how you sign in, since when, the account id.
- **Plugins** — every set and plugin, with *Add to account* on the free ones,
  *Buy* on the paid ones, and *yours* on what the account holds. *Add all free*
  per set. Adding a toolset adds the plugins it depends on too.
- **Hub** — the hub installer and the command line. Plugins are not downloaded
  from the site; the hub installs them.
- **Orders** — what was bought, whether it is paid.
- **Sign out**, top right.

## Signing the hub in

In the hub, top right: **Sign in.** The browser opens the account site — sign
in there if you are not already — and asks *Sign the hub in as …?* Say **Yes,
connect it.** The browser shows *Connected*, the hub is signed in, and the tab
can be closed. Your photo (or a person glyph) replaces the button; click it for
**Account** — name, email, what is on the account, *Manage on the web* — or
**Sign out**.

Signed out, every row says *Sign in* and that button starts the sign-in.
Signed in, a row says one of:

| Row | Meaning |
|---|---|
| **Add & install** | free, not on your account yet — one click adds it and installs it |
| **Install** | on your account, not installed here |
| **Buy** | paid, not on your account — opens the account site at that plugin |
| **Update** / **Installed** | as before |

*Install set* installs every free member and every paid member the account
holds, and says which paid ones it left out.

The same for the command line:

```
forge login              opens the browser; finishes when you say yes there
forge login --no-browser prints the link instead (remote sessions)
forge whoami             who this machine is signed in as, and what the account holds
forge list               every plugin: yours · free · paid, with what is installed
forge claim <set|plugin> add free plugins to the account without installing them
forge install <set>      adds free plugins to the account as it goes; names paid ones it cannot
forge logout             forget the sign-in kept on this machine
```

Exit codes: `2` what you asked for is not there — a mistyped `--project`,
an engine that is not installed, an unknown plugin; `4` not signed in, or the
account service could not answer; `5` something in the request is paid and not
on the account (the rest was installed).

The hub and the CLI share the sign-in: sign in with one and the other knows.

## How it works

No password ever passes through the desktop. The hub (or `forge`) listens on a
loopback port, opens `/connect/?port=…&state=…` on the account site, and waits.
When you say yes, the page posts your session — a refresh token, your id,
email, name and photo — to that port, and the hub answers by sending the
browser back to the *Connected* page. The one-time `state` ties the answer to
the request that asked for it; anything else is refused.

The session is kept in `%LOCALAPPDATA%\AutomationForge\account.bin`, protected
with Windows DPAPI under your Windows user. From it the hub mints an hour-long
ID token whenever it needs one, reads your account's record (what it holds)
from the project's database, and asks the account API for each download: the
API checks the account holds the plugin and answers with a URL that lives for
a few minutes. The manifest's own URLs are where the API finds the package,
not where a client fetches it. Signing out deletes the file; the account site
and other machines are untouched.

Providers are switched on for the project: Google, GitHub, email and password.
The project is Firebase `automation-forge-hq`; the account API is
`website/functions/` (its README has the routes); the site's public
configuration is in `website/account/.env`.

## Fab

A plugin bought on Fab is linked from inside Unreal Editor — *Automation Forge
→ Link my Fab purchases* — through Epic's own ownership check (`PluginWarden`,
which asks the Epic Games Launcher). The link is designed (HUB_PLAN.md §4) and
not built yet; until it is, send the Fab order id to bojan@blackcode.ch and
the plugin is added to the account by hand. There is no seller-side Fab API,
so the launcher, once, is the only way Epic sanctions.

## For developers

`FORGE_ACCOUNT_URL`, `FORGE_API_URL`, `FORGE_FIREBASE_EMULATOR`,
`FORGE_FIRESTORE_EMULATOR`, `FORGE_MANIFEST_URL` and `FORGE_DATA_DIR` point the
hub and CLI at the Firebase emulators and a scratch data directory, so a test
never touches the real sign-in. `website/functions/README.md` shows the set.
