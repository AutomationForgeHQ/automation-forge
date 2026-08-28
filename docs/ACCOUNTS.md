# Accounts

One account, three surfaces: the account site, the hub, and the `forge`
command line. Free sets never need it. An account is for what is paid, for
seeing the same "yours" everywhere, and — in time — for the workspace.

## Creating an account

Go to **https://automation-forge-app.web.app** and pick one:

- **Continue with Google** or **Continue with GitHub** — a normal OAuth pop-up;
  nothing to type. The account takes the name and photo from there.
- **Email and password** — type both, press *No account yet? Create one.*, then
  *Create the account*. Eight characters at least. *Forgot the password?* sends
  a reset link.

Whichever you use first is your account; signing in later with the same email
through a different provider joins it to the same account.

## What you see there

- **Account** — email, how you sign in, since when, the account id.
- **Downloads** — the hub and the command line, then every set and plugin with
  what this account owns marked *yours*. Free plugins are always yours.
- **Sign out**, top right.

## Signing the hub in

In the hub, top right: **Sign in.** The browser opens the account site — sign
in there if you are not already — and asks *Sign the hub in as …?* Say **Yes,
connect it.** The browser shows *Connected*, the hub is signed in, and the tab
can be closed. Your photo (or a person glyph) replaces the button; click it for
**Account** — name, email, how you sign in, what is yours, *Manage on the web*
— or **Sign out**.

The same for the command line:

```
forge login              opens the browser; finishes when you say yes there
forge login --no-browser prints the link instead (remote sessions)
forge whoami             who this machine is signed in as, and what it owns
forge logout             forget the sign-in kept on this machine
```

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
ID token whenever it needs one, and reads your account's record — what you own
— from the project's database. Signing out deletes the file; the account site
and other machines are untouched.

Providers are switched on for the project: Google, GitHub, email and password.
The project is Firebase `automation-forge-hq`; the site's public
configuration is in the `website` repository, `account/.env.production`.

## What waits for the paid backend

- **Delivery** of a paid plugin the account owns — a short-lived download URL
  issued per request. Until then a paid plugin is recognised as yours but not
  fetched.
- **Purchases** — Stripe on the site, and Fab purchases linked from inside the
  editor.
- **The editor plugin** reading the same sign-in.
