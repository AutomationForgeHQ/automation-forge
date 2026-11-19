"""Copy the catalogue and the installer's set of stills into src/Forge.Hub/catalogue (spec 12.4, 13).

    python tools/sync_hub_catalogue.py <catalogue.json> <website v2/public folder>

The catalogue is copied as catalogue/catalogue.json. Every still it names for the tracks (door, hero, film
poster), the capabilities (poster) and the products (card, hero, page film poster) is copied from the website's
public folder to catalogue/media/... under the same relative path, without the ?v= stamp. Videos and the
interface shots are not part of the set: they come from the network later (slice 5).

Run it when the catalogue or the site's pictures change, and commit the result.
"""
import json
import shutil
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent.parent
OUT = HERE / "src" / "Forge.Hub" / "catalogue"


def stills(cat):
    for t in cat.get("tracks", []):
        m = t.get("media", {})
        yield m.get("door")
        yield m.get("hero")
        yield (t.get("film") or {}).get("poster")
        for c in t.get("capabilities", []):
            yield (c.get("media") or {}).get("poster")
    for p in cat.get("products", []):
        m = p.get("media") or {}
        yield m.get("card")
        yield m.get("hero")
        yield ((p.get("page") or {}).get("film") or {}).get("poster")


def main():
    if len(sys.argv) != 3:
        print(__doc__)
        return 2
    src_cat, public = Path(sys.argv[1]), Path(sys.argv[2])
    cat = json.loads(src_cat.read_text(encoding="utf-8"))
    OUT.mkdir(parents=True, exist_ok=True)
    shutil.copyfile(src_cat, OUT / "catalogue.json")

    media = OUT / "media"
    if media.exists():
        shutil.rmtree(media)
    paths = sorted({p.split("?")[0].lstrip("/") for p in stills(cat) if p})
    total, missing = 0, []
    for rel in paths:
        src = public / rel
        if not src.exists():
            missing.append(rel)
            continue
        dst = OUT / rel
        dst.parent.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(src, dst)
        total += src.stat().st_size
    print(f"{len(paths) - len(missing)} stills, {total / 1e6:.1f} MB, into {OUT}")
    for m in missing:
        print(f"  missing: {m}")
    return 1 if missing else 0


if __name__ == "__main__":
    sys.exit(main())
