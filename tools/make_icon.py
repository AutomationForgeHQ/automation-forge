"""The hub's icon, from the mark: three strokes that do not close, amber on ink.

    python tools/make_icon.py

Writes src/Forge.Hub/Assets/forge.ico (every size Windows asks for) and
docs/icon-256.png. The geometry is the website's TriangleMark, verbatim.
"""
from pathlib import Path

from PIL import Image, ImageDraw

ROOT = Path(__file__).resolve().parents[1]
INK = (10, 10, 11, 255)
BONE = (237, 237, 233, 255)
AMBER = (227, 154, 43, 255)
SIZES = [16, 20, 24, 32, 40, 48, 64, 96, 128, 256]


def render(size: int, supersample: int = 8) -> Image.Image:
    n = size * supersample
    img = Image.new("RGBA", (n, n), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    d.rounded_rectangle([0, 0, n - 1, n - 1], radius=n * 0.21, fill=INK)

    # The mark's viewBox is 24 x 22; fit it uniformly with breathing room.
    pad = n * 0.19
    scale = min((n - 2 * pad) / 24, (n - 2 * pad) / 22)
    ox, oy = (n - 24 * scale) / 2, (n - 22 * scale) / 2
    width = max(1.6 * scale * 1.15, n * 0.05)  # a touch heavier than on screen; icons shrink

    def p(x, y):
        return ox + x * scale, oy + y * scale

    def stroke(a, b, colour):
        d.line([p(*a), p(*b)], fill=colour, width=int(round(width)))
        for x, y in (p(*a), p(*b)):
            d.ellipse([x - width / 2, y - width / 2, x + width / 2, y + width / 2], fill=colour)

    stroke((4.6, 20), (19.4, 20), BONE)       # software — human
    stroke((3.3, 17.7), (10.7, 4.8), AMBER)   # software — machine
    stroke((13.3, 4.8), (20.7, 17.7), AMBER)  # machine — human
    return img.resize((size, size), Image.LANCZOS)


def main() -> None:
    frames = {s: render(s) for s in SIZES}
    ico = ROOT / "src" / "Forge.Hub" / "Assets" / "forge.ico"
    ico.parent.mkdir(parents=True, exist_ok=True)
    frames[256].save(ico, format="ICO", sizes=[(s, s) for s in SIZES],
                     append_images=[frames[s] for s in SIZES if s != 256])
    png = ROOT / "docs" / "icon-256.png"
    png.parent.mkdir(parents=True, exist_ok=True)
    frames[256].save(png)
    print(f"wrote {ico} ({ico.stat().st_size} bytes) and {png}")


if __name__ == "__main__":
    main()
