"""Cut the static Archivo fonts Kovati Hub embeds, from the variable Archivo (wght 100-900, wdth 62-125).

    pip install fonttools
    python tools/make_archivo_cuts.py <Archivo-Variable.ttf>

Why (slice 0, test 1, HUB_V2_NOTES.md): Avalonia 12.1 draws an embedded variable font at its default instance
only. FontWeight and FontStretch do not move its axes, so every Archivo text came out SemiBold (the file's
default is wght 600) and the site's wide display cut (font-display, width 115 %) was out of reach. Four static
instances fix both:

    Archivo-Regular.ttf          family "Archivo", wght 400
    Archivo-Medium.ttf           family "Archivo", wght 500
    Archivo-SemiBold.ttf         family "Archivo", wght 600
    ArchivoDisplay-SemiBold.ttf  family "Archivo Display", wght 600, wdth 115 (the display-* styles)

The variable file is in git history (src/Forge.Hub/Assets/Fonts/Archivo-Variable.ttf before hub-v2). OFL 1.1.
"""
import sys
from pathlib import Path

from fontTools.ttLib import TTFont
from fontTools.varLib import instancer

OUT = Path(__file__).resolve().parent.parent / "src" / "Forge.Hub" / "Assets" / "Fonts"

CUTS = [
    ("Archivo-Regular.ttf", "Archivo", "Regular", 400, 100),
    ("Archivo-Medium.ttf", "Archivo", "Medium", 500, 100),
    ("Archivo-SemiBold.ttf", "Archivo", "SemiBold", 600, 100),
    ("ArchivoDisplay-SemiBold.ttf", "Archivo Display", "SemiBold", 600, 115),
]


def main():
    if len(sys.argv) != 2:
        print(__doc__)
        return 2
    for file, family, style, wght, wdth in CUTS:
        font = instancer.instantiateVariableFont(TTFont(sys.argv[1]), {"wght": wght, "wdth": wdth}, static=True)
        for rec in font["name"].names:
            if rec.nameID in (1, 16):
                rec.string = family
            elif rec.nameID in (2, 17):
                rec.string = style
            elif rec.nameID == 4:
                rec.string = f"{family} {style}"
            elif rec.nameID == 6:
                rec.string = f"{family.replace(' ', '')}-{style}"
        font["OS/2"].usWeightClass = wght
        font.save(OUT / file)
        print(f"wrote {OUT / file}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
