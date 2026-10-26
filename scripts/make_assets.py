#!/usr/bin/env python3
"""Generates the MSIX tile assets and tray icon. Run: python3 scripts/make_assets.py"""
from pathlib import Path
from PIL import Image, ImageDraw

OUT = Path(__file__).resolve().parent.parent / "src" / "MaSatellite.App" / "Assets"
OUT.mkdir(parents=True, exist_ok=True)

BG_TOP = (24, 32, 48)
BG_BOTTOM = (10, 14, 24)
ACCENT = (64, 214, 190)
SS = 4  # supersampling


def glyph(size: int, with_bg: bool = True) -> Image.Image:
    s = size * SS
    img = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    if with_bg:
        bg = Image.new("RGBA", (s, s))
        px = bg.load()
        for y in range(s):
            t = y / max(s - 1, 1)
            c = tuple(int(BG_TOP[i] + (BG_BOTTOM[i] - BG_TOP[i]) * t) for i in range(3)) + (255,)
            for x in range(s):
                px[x, y] = c
        mask = Image.new("L", (s, s), 0)
        ImageDraw.Draw(mask).rounded_rectangle([0, 0, s - 1, s - 1], radius=int(s * 0.22), fill=255)
        img.paste(bg, (0, 0), mask)
        d = ImageDraw.Draw(img)
    cx, cy = s * 0.36, s * 0.5
    r = s * 0.11
    d.ellipse([cx - r, cy - r, cx + r, cy + r], fill=ACCENT)
    for i, rad in enumerate((0.20, 0.32, 0.44)):
        rr = s * rad
        w = max(int(s * 0.05), 1)
        d.arc([cx - rr, cy - rr, cx + rr, cy + rr], start=-48, end=48, fill=ACCENT, width=w)
    return img.resize((size, size), Image.LANCZOS)


def save(name: str, w: int, h: int | None = None) -> None:
    h = h or w
    if w == h:
        glyph(w).save(OUT / name)
        return
    canvas = Image.new("RGBA", (w, h), BG_BOTTOM + (255,))
    g = glyph(min(w, h), with_bg=False)
    canvas.paste(g, ((w - g.width) // 2, (h - g.height) // 2), g)
    canvas.save(OUT / name)


save("StoreLogo.png", 50)
save("Square44x44Logo.png", 44)
save("Square44x44Logo.targetsize-48_altform-unplated.png", 48)
save("Square150x150Logo.png", 150)
save("Wide310x150Logo.png", 310, 150)
save("SplashScreen.png", 620, 300)

sizes = [16, 20, 24, 32, 40, 48, 64, 256]
glyph(256).save(OUT / "tray.ico", sizes=[(s, s) for s in sizes])
print("assets written to", OUT)
