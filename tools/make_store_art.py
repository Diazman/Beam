"""Microsoft Store listing artwork (Partner Center > Store listing > Store logos).

Run: python3 tools/make_store_art.py  ->  packaging/store/logos/
  BoxArt-1x1-2160.png      1:1 box art (2160x2160), the main Store image on Windows 10/11
  Poster-2x3-1440x2160.png 2:3 poster art (1440x2160)
  AppTileIcon-300.png      1:1 app tile icon (300x300)
"""
import os
from PIL import Image, ImageDraw, ImageFont
from make_icon import make, lerp

ROOT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..")
OUT = os.path.join(ROOT, "packaging", "store", "logos")
FONTS = "/usr/share/fonts/opentype/inter"


def background(w, h):
    """Full-bleed dark navy -> indigo gradient (the Store shows art edge to edge, so no transparency)."""
    small = Image.new("RGB", (w // 8, h // 8))
    px = small.load()
    a, b = (17, 24, 64), (49, 46, 129)
    for y in range(small.height):
        for x in range(small.width):
            px[x, y] = lerp(a, b, (x / small.width + y / small.height) / 2)
    return small.resize((w, h), Image.BICUBIC).convert("RGBA")


def font(weight, size):
    return ImageFont.truetype(os.path.join(FONTS, f"Inter-{weight}.otf"), size)


def centered(draw, w, y, text, f, fill):
    width = draw.textlength(text, font=f)
    draw.text(((w - width) / 2, y), text, font=f, fill=fill)


def art(w, h, icon_px, icon_y, title_y=None, tagline_y=None):
    img = background(w, h)
    icon = make(1024).resize((icon_px, icon_px), Image.LANCZOS)
    img.alpha_composite(icon, ((w - icon_px) // 2, icon_y))
    d = ImageDraw.Draw(img)
    if title_y is not None:
        centered(d, w, title_y, "Beam", font("Bold", int(w * 0.13)), (255, 255, 255))
    if tagline_y is not None:
        f = font("Medium", int(w * 0.04))
        centered(d, w, tagline_y, "Send files to nearby computers and phones", f, (199, 210, 254))
        centered(d, w, tagline_y + int(w * 0.06), "No cables. No accounts. No internet.", f, (165, 180, 252))
    return img.convert("RGB")


if __name__ == "__main__":
    os.makedirs(OUT, exist_ok=True)
    art(2160, 2160, 1100, 380, title_y=1520).save(os.path.join(OUT, "BoxArt-1x1-2160.png"))
    art(1440, 2160, 820, 420, title_y=1300, tagline_y=1560).save(os.path.join(OUT, "Poster-2x3-1440x2160.png"))
    make(1024).resize((300, 300), Image.LANCZOS).save(os.path.join(OUT, "AppTileIcon-300.png"))
    print("store art written to", os.path.abspath(OUT))
