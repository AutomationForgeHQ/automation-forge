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

Slice 5 (loops and the film overlay) and slice 6 (the cart, the offline strip, `kovatihub:`) are not started: the
first waits on the website's WebP loops and a public catalogue address, the second on the account app's cart.

## Run it beside the installed Hub

    git -C C:\UNREAL\AutomationForge switch hub-v2
    dotnet build C:\UNREAL\AutomationForge\Forge.sln
    $env:FORGE_HUB_INSTANCE = 'v2'                                   # its own mutex and events: the installed Hub is left alone
    $env:FORGE_DATA_DIR = 'D:\UNREAL\ColonyDownloads\hub-v2-data'    # a throwaway data folder: signed out, no receipts
    $env:FORGE_MEDIA_URL = 'C:\UNREAL\AutomationForge-website\v2\public'   # optional: the products' interface shots
    & C:\UNREAL\AutomationForge\src\Forge.Hub\bin\Debug\net9.0-windows10.0.17763.0\AutomationForgeHub.exe

Quit it from its tray icon, or with the same `FORGE_HUB_INSTANCE`: `AutomationForgeHub.exe --quit`.
Leave `FORGE_DATA_DIR` out to see your real receipts and sign-in (it then writes `hub-state.json` beside them; an
older Hub ignores that file). `FORGE_ENGINES=<folder>` limits it to one engine folder, e.g. a throwaway one.

Other switches for local work: `FORGE_CATALOGUE=<file>`, `FORGE_MANIFEST_URL=<file>`, `FORGE_RELEASES_API=off`,
`FORGE_HUB_SNAPSHOT=<file.png>` (draws the window itself into a PNG eight seconds after it opens).

## Checks

    dotnet test tests/Forge.Core.Tests                               # planner, catalogue, checklist, updates, hub-state
    src/forge/bin/Debug/net9.0/forge.exe install --track animation --dry-run [--agents]
    dotnet run --project tests/Forge.Hub.Snapshots -- <out dir> screens   # every screen, headless, from fixed data
