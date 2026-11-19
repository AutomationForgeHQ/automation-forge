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
