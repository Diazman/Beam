"""Generates the Beam app icon (PNG + multi-size ICO). Run: python3 tools/make_icon.py"""
import os
from PIL import Image, ImageDraw, ImageFilter

OUT = os.path.join(os.path.dirname(__file__), "..", "src", "Beam.App", "Assets")
S = 1024  # draw large, downsample for crisp edges


def lerp(a, b, t):
    return tuple(int(a[i] + (b[i] - a[i]) * t) for i in range(3))


def make(size_px=S):
    img = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    # Diagonal blue -> indigo gradient
    grad = Image.new("RGBA", (S, S))
    top, bottom = (59, 130, 246), (99, 82, 241)
    px = grad.load()
    for y in range(S):
        for x in range(S):
            t = (x + y) / (2 * S)
            px[x, y] = lerp(top, bottom, t) + (255,)
    mask = Image.new("L", (S, S), 0)
    margin = int(S * 0.06)
    ImageDraw.Draw(mask).rounded_rectangle([margin, margin, S - margin, S - margin], radius=int(S * 0.22), fill=255)
    img.paste(grad, (0, 0), mask)

    # Paper-plane glyph
    glyph = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    d = ImageDraw.Draw(glyph)
    c = S / 2
    pts_main = [(c - 0.30 * S, c - 0.02 * S), (c + 0.30 * S, c - 0.26 * S), (c + 0.06 * S, c + 0.30 * S), (c - 0.02 * S, c + 0.08 * S)]
    d.polygon(pts_main, fill=(255, 255, 255, 255))
    fold = [(c - 0.02 * S, c + 0.08 * S), (c + 0.30 * S, c - 0.26 * S), (c + 0.02 * S, c + 0.02 * S)]
    d.polygon(fold, fill=(206, 220, 255, 255))
    shadow = glyph.split()[3].filter(ImageFilter.GaussianBlur(S * 0.015))
    shade = Image.new("RGBA", (S, S), (20, 20, 80, 90))
    img.paste(shade, (int(S * 0.01), int(S * 0.02)), shadow)
    img.alpha_composite(glyph)
    return img.resize((size_px, size_px), Image.LANCZOS)


if __name__ == "__main__":
    os.makedirs(OUT, exist_ok=True)
    big = make(S)
    big.resize((256, 256), Image.LANCZOS).save(os.path.join(OUT, "beam.png"))
    big.resize((256, 256), Image.LANCZOS).save(os.path.join(OUT, "beam.ico"), sizes=[(16, 16), (20, 20), (24, 24), (32, 32), (40, 40), (48, 48), (64, 64), (128, 128), (256, 256)])
    print("icons written to", os.path.abspath(OUT))
