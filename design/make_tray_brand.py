"""
Regenerate the Windows app's brand assets from the founder's logo files (needs numpy + pillow):

    python design/make_tray_brand.py [path-to-logo-folder]

The folder defaults to the local project brain, ../.cloude/UI-UX/logo (logo.jpg = wordmark,
logo-mark.png = the Z). Writes into src/Zinwe.Agent.Tray/Assets:

    zinwe-logo.png   wordmark (orange Z + grey "inwe"), transparent, 144px tall (crisp up to 6x at 24px)
    zinwe-mark.png   the Z alone, transparent, 256x256 (tray icon source)
    zinwe.ico        app / window / taskbar icon, 16..256

Same matting as the website's design/brand/make_brand.py: every pixel is fitted as "brand colour over
white", giving true alpha (no white fringe) with RGB snapped to the exact brand colours.
"""
from __future__ import annotations

import sys
from pathlib import Path

import numpy as np
from PIL import Image, ImageFilter

HERE = Path(__file__).resolve().parent
SRC = Path(sys.argv[1]) if len(sys.argv) > 1 else HERE.parent.parent / ".cloude" / "UI-UX" / "logo"
OUT = HERE.parent / "src" / "Zinwe.Agent.Tray" / "Assets"

ORANGE = np.array([253, 109, 2], dtype=np.float64)   # #FD6D02, the logo orange
GREY = np.array([43, 43, 42], dtype=np.float64)      # #2B2B2A, "inwe"
WHITE = 255.0


def matte(path: Path, colours: list[np.ndarray]) -> tuple[np.ndarray, np.ndarray]:
    """(alpha HxW in 0..1, index of the best-fitting brand colour) for a logo on a white background."""
    c = np.asarray(Image.open(path).convert("RGB"), dtype=np.float64)
    best_err = best_a = best_i = None
    for i, f in enumerate(colours):
        denom = WHITE - f
        a = np.clip(((WHITE - c) * denom).sum(-1) / (denom ** 2).sum(), 0, 1)
        err = (((a[..., None] * f + (1 - a[..., None]) * WHITE) - c) ** 2).sum(-1)
        if best_err is None:
            best_err, best_a, best_i = err, a, np.zeros(a.shape, int)
        else:
            better = err < best_err
            best_err = np.where(better, err, best_err)
            best_a = np.where(better, a, best_a)
            best_i = np.where(better, i, best_i)
    # Clearly non-white pixels, dilated: anything outside is background / JPEG noise.
    core = (c.min(-1) < 200).astype(np.uint8) * 255
    mask = np.asarray(Image.fromarray(core).filter(ImageFilter.MaxFilter(7))) > 0
    a = np.where(mask, best_a, 0.0)
    a = np.where(a < 0.03, 0.0, np.where(a > 0.97, 1.0, a))
    return a, best_i


def compose(alpha: np.ndarray, idx: np.ndarray, palette: list[tuple[int, int, int]]) -> Image.Image:
    rgb = np.zeros(alpha.shape + (3,), dtype=np.uint8)
    for i, col in enumerate(palette):
        rgb[idx == i] = col
    img = Image.fromarray(np.dstack([rgb, (alpha * 255).round().astype(np.uint8)]), "RGBA")
    return img.crop(img.getchannel("A").getbbox())


def square(img: Image.Image, pad: float) -> Image.Image:
    side = round(max(img.size) * (1 + 2 * pad))
    canvas = Image.new("RGBA", (side, side), (0, 0, 0, 0))
    canvas.paste(img, ((side - img.width) // 2, (side - img.height) // 2), img)
    return canvas


def main() -> None:
    wordmark_src = next((SRC / n for n in ("logo.jpg", "logo.png") if (SRC / n).exists()), None)
    mark_src = SRC / "logo-mark.png"
    if wordmark_src is None or not mark_src.exists():
        sys.exit(f"Logo files not found in {SRC} (need logo.jpg|logo.png and logo-mark.png)")

    a, i = matte(wordmark_src, [ORANGE, GREY])
    logo = compose(a, i, [tuple(map(int, ORANGE)), tuple(map(int, GREY))])
    h = 144
    logo.resize((round(logo.width * h / logo.height), h), Image.LANCZOS).save(OUT / "zinwe-logo.png", optimize=True)

    a, i = matte(mark_src, [ORANGE])
    mark = compose(a, i, [tuple(map(int, ORANGE))])
    square(mark, 0.0).resize((256, 256), Image.LANCZOS).save(OUT / "zinwe-mark.png", optimize=True)
    # A little breathing room so the Z isn't clipped at 16px in the taskbar.
    square(mark, 0.06).resize((256, 256), Image.LANCZOS).save(
        OUT / "zinwe.ico", sizes=[(16, 16), (20, 20), (24, 24), (32, 32), (40, 40), (48, 48), (64, 64), (256, 256)])

    print(f"from {SRC}")
    for name in ("zinwe-logo.png", "zinwe-mark.png", "zinwe.ico"):
        p = OUT / name
        print(f"  {name:16} {p.stat().st_size // 1024} KB" + (f"  {Image.open(p).size}" if p.suffix == ".png" else ""))


if __name__ == "__main__":
    main()
