#!/usr/bin/env python3
"""Build manifest.json from the GitHub Releases on AutomationForgeHQ/releases.

The releases are the source of truth for what is downloadable; the register
(plugins.json in the forge monorepo) is the source of truth for what a plugin
*is* — its set, role and distribution — and each plugin's descriptor names its
dependencies. This script joins the three into the one document the hub, the
forge CLI and the website read.

    python tools/build_manifest.py --forge C:\\UNREAL\\Colony_NP24\\Plugins\\Forge

Nothing is hand-maintained: run it again after a release and commit the result.
Checksums come from GitHub's own per-asset digest, so no asset is downloaded.
"""

from __future__ import annotations

import argparse
import json
import re
import sys
import urllib.error
import urllib.request
from datetime import datetime, timezone
from pathlib import Path

RELEASES_REPO = "AutomationForgeHQ/releases"
API = f"https://api.github.com/repos/{RELEASES_REPO}/releases?per_page=100"
SCHEMA_VERSION = 1

# <Plugin>-<version>-UE5.8-Win64.zip  /  <Plugin>-<version>-UE5.8-Win64-symbols.zip
ASSET = re.compile(r"^(?P<plugin>[A-Za-z0-9]+)-(?P<version>[0-9][^-]*(?:-[^-]+)*?)-UE(?P<engine>\d+\.\d+)-(?P<platform>Win64|Mac|Linux)(?P<symbols>-symbols)?\.zip$")
TAG = re.compile(r"^(?P<plugin>[a-z0-9]+)-v(?P<version>.+)$")


def fetch_releases() -> list[dict]:
    releases: list[dict] = []
    url: str | None = API
    while url:
        req = urllib.request.Request(url, headers={"Accept": "application/vnd.github+json", "User-Agent": "automation-forge-manifest"})
        with urllib.request.urlopen(req) as resp:
            releases.extend(json.load(resp))
            link = resp.headers.get("Link", "")
            nxt = re.search(r'<([^>]+)>;\s*rel="next"', link)
            url = nxt.group(1) if nxt else None
    return releases


def read_register(forge: Path) -> dict:
    return json.loads((forge / "plugins.json").read_text(encoding="utf-8"))


def repo_exists(full_name: str) -> bool:
    """A mirror is listed only once it exists — the manifest never carries a dead link."""
    req = urllib.request.Request(f"https://api.github.com/repos/{full_name}", headers={"User-Agent": "automation-forge-manifest"})
    try:
        with urllib.request.urlopen(req) as resp:
            return resp.status == 200
    except urllib.error.HTTPError:
        return False


def read_dependencies(forge: Path, plugin: str, known: set[str]) -> list[str]:
    """Dependencies on other plugins in the register, from the descriptor."""
    desc = forge / plugin / f"{plugin}.uplugin"
    if not desc.exists():
        return []
    data = json.loads(desc.read_text(encoding="utf-8-sig"))
    return sorted({p["Name"] for p in data.get("Plugins", []) if p.get("Name") in known})


def build(forge: Path) -> dict:
    register = read_register(forge)
    plugins_reg: dict = register["plugins"]
    known = set(plugins_reg)
    by_lower = {name.lower(): name for name in known}

    versions: dict[str, list[dict]] = {name: [] for name in known}

    for rel in fetch_releases():
        m = TAG.match(rel["tag_name"])
        if not m:
            continue
        plugin = by_lower.get(m.group("plugin"))
        if not plugin:
            print(f"skip {rel['tag_name']}: not in the register", file=sys.stderr)
            continue
        assets = {a["name"]: a for a in rel["assets"]}
        for name, asset in assets.items():
            am = ASSET.match(name)
            if not am or am.group("symbols"):
                continue
            digest = (asset.get("digest") or "").removeprefix("sha256:") or None
            symbols = assets.get(name.replace("-Win64.zip", "-Win64-symbols.zip"))
            versions[plugin].append({
                "version": am.group("version"),
                "engine": am.group("engine"),
                "platform": am.group("platform"),
                "channel": "nightly" if rel.get("prerelease") else "stable",
                "url": asset["browser_download_url"],
                "size": asset["size"],
                "sha256": digest,
                "symbols": symbols["browser_download_url"] if symbols else None,
                "releasedAt": rel["published_at"],
                "notes": rel["html_url"],
            })

    plugins_out = []
    for name in sorted(known):
        reg = plugins_reg[name]
        vs = sorted(versions[name], key=lambda v: (v["engine"], v["releasedAt"]), reverse=True)
        plugins_out.append({
            "id": name,
            "set": reg["set"],
            "role": reg["role"],
            "distribution": reg["distribution"],
            "dependencies": read_dependencies(forge, name, known),
            "source": f"https://github.com/{reg['mirror']}" if reg.get("mirror") and repo_exists(reg["mirror"]) else None,
            "versions": vs,
        })

    sets_out = []
    for sid, s in register["sets"].items():
        members = [p["id"] for p in plugins_out if p["set"] == sid]
        sets_out.append({"id": sid, "name": s["name"], "members": members})

    return {
        "schemaVersion": SCHEMA_VERSION,
        "generatedAt": datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
        "releases": f"https://github.com/{RELEASES_REPO}",
        "channels": ["stable", "nightly"],
        "sets": sets_out,
        "plugins": plugins_out,
    }


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__.split("\n")[0])
    ap.add_argument("--forge", required=True, type=Path, help="path to the forge monorepo checkout (plugins.json and the descriptors)")
    ap.add_argument("--out", default=Path(__file__).resolve().parents[1] / "manifest.json", type=Path)
    args = ap.parse_args()

    manifest = build(args.forge)
    args.out.write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8", newline="\n")

    published = sum(1 for p in manifest["plugins"] if p["versions"])
    print(f"{args.out}: {len(manifest['sets'])} sets, {len(manifest['plugins'])} plugins, {published} with a release")
    for p in manifest["plugins"]:
        latest = p["versions"][0]["version"] if p["versions"] else "-"
        print(f"  {p['id']:<28} {p['distribution']:<5} {latest:<10} deps: {', '.join(p['dependencies']) or '-'}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
