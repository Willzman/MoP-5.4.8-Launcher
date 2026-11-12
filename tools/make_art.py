"""Generates the launcher's original artwork (background + icon).

Everything is drawn procedurally - no game assets are used.
Usage: python3 tools/make_art.py   (needs Pillow and numpy)
"""
import math
import os
import random

import numpy as np
from PIL import Image, ImageDraw, ImageFilter

W, H = 1920, 1080
OUT = os.path.join(os.path.dirname(__file__), "..", "src", "MoPLauncher", "Assets")
rng = random.Random(548)
nrng = np.random.default_rng(548)


def lerp(a, b, t):
    return tuple(a[i] + (b[i] - a[i]) * t for i in range(3))


def sky():
    y = np.linspace(0, 1, H)[:, None]
    x = np.linspace(0, 1, W)[None, :]
    stops = [(0.0, (6, 20, 26)), (0.30, (14, 52, 54)), (0.52, (44, 104, 88)),
             (0.66, (196, 150, 84)), (0.74, (232, 176, 104)), (1.0, (120, 92, 70))]
    img = np.zeros((H, W, 3), np.float32)
    for (p0, c0), (p1, c1) in zip(stops, stops[1:]):
        t = np.clip((y - p0) / (p1 - p0), 0, 1)
        mask = (y >= p0) & (y <= p1)
        for i in range(3):
            layer = c0[i] + (c1[i] - c0[i]) * t
            img[:, :, i] = np.where(mask, layer, img[:, :, i])
    # sun glow
    sx, sy = 0.56, 0.50
    d = np.sqrt(((x - sx) * W / H) ** 2 + (y - sy) ** 2)
    glow = np.exp(-(d / 0.34) ** 2)[:, :, None]
    img = img + glow * np.array([120, 84, 30], np.float32)
    halo = np.exp(-(d / 0.11) ** 2)[:, :, None]
    img = img + halo * np.array([90, 70, 40], np.float32)
    disc = np.clip((0.062 - d) / 0.003, 0, 1)[:, :, None]
    img = img * (1 - disc) + disc * np.array([255, 236, 190], np.float32)
    return Image.fromarray(np.clip(img, 0, 255).astype(np.uint8))


def ridge(base, amp, peaks, rough, seed):
    r = random.Random(seed)
    xs = np.arange(W, dtype=np.float32)
    h = np.zeros(W, np.float32)
    for _ in range(peaks):
        cx = r.uniform(-0.05, 1.05) * W
        w = r.uniform(0.018, 0.06) * W
        a = r.uniform(0.35, 1.0) * amp
        h = np.maximum(h, a * np.exp(-np.abs((xs - cx) / w) ** 1.6))
    # rolling base + fractal roughness
    for k in range(1, 6):
        f = r.uniform(1.5, 3.0) * k * k
        h += rough * amp / (k * k) * np.sin(xs / W * f * math.pi + r.uniform(0, 6.28))
    return base * H - h * H


def fill_ridge(img, line, color, alpha=255):
    layer = Image.new("RGBA", (W, H), (0, 0, 0, 0))
    d = ImageDraw.Draw(layer)
    pts = [(x, float(line[x])) for x in range(0, W, 2)] + [(W, float(line[-1])), (W, H), (0, H)]
    d.polygon(pts, fill=tuple(int(c) for c in color) + (alpha,))
    img.alpha_composite(layer)


def mist(img, y, thickness, strength, color=(236, 226, 200)):
    arr = nrng.random((H // 24, W // 48)).astype(np.float32)
    noise = Image.fromarray((arr * 255).astype(np.uint8)).resize((W, H), Image.BICUBIC)
    n = np.asarray(noise, np.float32) / 255
    yy = np.arange(H, dtype=np.float32)[:, None] / H
    band = np.exp(-((yy - y) / thickness) ** 2)
    a = np.clip(band * (0.45 + 0.9 * n) * strength, 0, 1)
    layer = np.zeros((H, W, 4), np.uint8)
    layer[:, :, :3] = color
    layer[:, :, 3] = (a * 255).astype(np.uint8)
    img.alpha_composite(Image.fromarray(layer, "RGBA").filter(ImageFilter.GaussianBlur(14)))


def pagoda(img, cx, base_y, s, color, tiers=5, lights=True):
    layer = Image.new("RGBA", (W, H), (0, 0, 0, 0))
    d = ImageDraw.Draw(layer)
    col = tuple(int(c) for c in color) + (255,)
    y = base_y
    bw = 1.0 * s
    windows = []
    # stone base
    d.polygon([(cx - bw * 0.75, y), (cx + bw * 0.75, y), (cx + bw * 0.62, y - s * 0.16), (cx - bw * 0.62, y - s * 0.16)], fill=col)
    y -= s * 0.16
    for i in range(tiers):
        bh = s * 0.34 * (1 - i * 0.07)
        d.rectangle([cx - bw / 2, y - bh, cx + bw / 2, y], fill=col)
        windows.append((cx, y - bh * 0.45, bw, bh))
        y -= bh
        rw = bw * 0.98
        rh = s * 0.20 * (1 - i * 0.05)
        d.polygon([
            (cx - bw * 0.36, y - rh), (cx + bw * 0.36, y - rh),
            (cx + rw * 0.72, y - rh * 0.28), (cx + rw, y - rh * 0.62),
            (cx + rw * 0.98, y - rh * 0.30), (cx + rw * 0.70, y + rh * 0.10),
            (cx - rw * 0.70, y + rh * 0.10), (cx - rw * 0.98, y - rh * 0.30),
            (cx - rw, y - rh * 0.62), (cx - rw * 0.72, y - rh * 0.28)], fill=col)
        y -= rh
        bw *= 0.80
    d.polygon([(cx - bw * 0.20, y), (cx + bw * 0.20, y), (cx, y - s * 0.55)], fill=col)
    d.ellipse([cx - s * 0.05, y - s * 0.62, cx + s * 0.05, y - s * 0.52], fill=col)
    img.alpha_composite(layer)
    if lights:
        glow = Image.new("RGBA", (W, H), (0, 0, 0, 0))
        g = ImageDraw.Draw(glow)
        for (wx, wy, wbw, wbh) in windows:
            for off in (-0.26, 0, 0.26):
                ww, wh = wbw * 0.09, wbh * 0.30
                x0 = wx + off * wbw
                g.rectangle([x0 - ww, wy - wh, x0 + ww, wy + wh], fill=(255, 196, 96, 255))
        img.alpha_composite(glow.filter(ImageFilter.GaussianBlur(max(1.5, s * 0.05))))
        img.alpha_composite(glow.filter(ImageFilter.GaussianBlur(0.6)))


def tree(img, x, y, length, angle, depth, d, tips):
    if depth == 0 or length < 6:
        tips.append((x, y))
        return
    x2 = x + math.cos(angle) * length
    y2 = y - math.sin(angle) * length
    d.line([(x, y), (x2, y2)], fill=(10, 14, 14, 255), width=max(1, int(depth * 2.6)))
    if depth <= 4:
        tips.append((x2, y2))
    n = 2 if rng.random() < 0.75 else 3
    for _ in range(n):
        tree(img, x2, y2, length * rng.uniform(0.62, 0.82), angle + rng.uniform(-0.75, 0.75) + 0.05, depth - 1, d, tips)


def blossoms(img, tips):
    soft = Image.new("RGBA", (W, H), (0, 0, 0, 0))
    crisp = Image.new("RGBA", (W, H), (0, 0, 0, 0))
    ds, dc = ImageDraw.Draw(soft), ImageDraw.Draw(crisp)
    for (x, y) in tips:
        for _ in range(5):
            r = rng.uniform(10, 26)
            ox, oy = rng.gauss(0, 16), rng.gauss(0, 13)
            c = rng.choice([(232, 132, 150), (244, 170, 180), (214, 104, 132), (250, 200, 204)])
            ds.ellipse([x + ox - r, y + oy - r, x + ox + r, y + oy + r], fill=c + (70,))
        for _ in range(7):
            r = rng.uniform(2.5, 5.5)
            ox, oy = rng.gauss(0, 20), rng.gauss(0, 16)
            c = rng.choice([(248, 186, 196), (255, 214, 218), (236, 140, 160)])
            dc.ellipse([x + ox - r, y + oy - r, x + ox + r, y + oy + r], fill=c + (225,))
    img.alpha_composite(soft.filter(ImageFilter.GaussianBlur(7)))
    img.alpha_composite(crisp.filter(ImageFilter.GaussianBlur(0.7)))


def peak_near(line, fx, span=0.09):
    a, b = int(W * (fx - span)), int(W * (fx + span))
    return a + int(np.argmin(line[a:b]))


def background():
    img = sky().convert("RGBA")
    mist_c = (214, 190, 140)
    far = [
        (0.70, 0.30, 16, 0.020, (84, 124, 108), 0.62, 11),
        (0.76, 0.34, 13, 0.025, (52, 96, 88), 0.69, 12),
        (0.84, 0.36, 11, 0.030, (30, 68, 66), 0.77, 13),
    ]
    for i, (base, amp, peaks, rough, color, misty, seed) in enumerate(far):
        line = ridge(base, amp, peaks, rough, seed)
        fill_ridge(img, line, color)
        if i == 1:
            x = peak_near(line, 0.50)
            pagoda(img, x, float(line[x]) + 20, 64, (52, 96, 88))
        mist(img, misty, 0.050, 0.55, mist_c)
    # near ridge with the main pagoda
    line = ridge(0.95, 0.30, 7, 0.035, 21)
    fill_ridge(img, line, (14, 38, 40))
    x = peak_near(line, 0.64)
    pagoda(img, x, float(line[x]) + 40, 132, (14, 38, 40))
    mist(img, 0.88, 0.045, 0.42, (190, 176, 140))
    # foreground slope and blossom tree
    xs = np.arange(W, dtype=np.float32)
    fg = H * (0.80 + 0.19 * (xs / W) ** 0.8 + 0.012 * np.sin(xs / 90.0))
    fill_ridge(img, fg, (6, 12, 13))
    d = ImageDraw.Draw(img)
    tips = []
    tree(img, W * 0.085, H * 0.86, 150, math.radians(78), 7, d, tips)
    blossoms(img, tips)
    # Falling petals are not painted in: the launcher animates them (PetalLayer.cs).
    # vignette, heavier on the left and bottom where the UI text sits
    yy, xx = np.mgrid[0:H, 0:W].astype(np.float32)
    xx /= W
    yy /= H
    v = 1 - 0.55 * np.clip(((xx - 0.55) / 0.75) ** 2 + ((yy - 0.45) / 0.70) ** 2, 0, 1)
    v *= 1 - 0.30 * np.clip(1 - xx / 0.45, 0, 1) * np.clip(1 - yy / 0.5, 0, 1)
    arr = np.asarray(img.convert("RGB"), np.float32) * v[:, :, None]
    grain = nrng.normal(0, 2.2, (H, W, 1)).astype(np.float32)
    out = Image.fromarray(np.clip(arr + grain, 0, 255).astype(np.uint8))
    out.save(os.path.join(OUT, "background.jpg"), quality=90)


def icon():
    S = 512
    img = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    d.ellipse([8, 8, S - 8, S - 8], fill=(212, 168, 78, 255))
    d.ellipse([20, 20, S - 20, S - 20], fill=(120, 84, 30, 255))
    d.ellipse([30, 30, S - 30, S - 30], fill=(18, 78, 70, 255))
    # inner glow
    g = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    ImageDraw.Draw(g).ellipse([110, 150, S - 110, S - 70], fill=(240, 190, 110, 150))
    g = g.filter(ImageFilter.GaussianBlur(50))
    mask = Image.new("L", (S, S), 0)
    ImageDraw.Draw(mask).ellipse([30, 30, S - 30, S - 30], fill=255)
    img.paste(Image.alpha_composite(img, g), (0, 0), mask)
    # pagoda silhouette
    global W, H
    w0, h0 = W, H
    W, H = S, S
    layer = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    pagoda(layer, S / 2, S * 0.83, 150, (8, 28, 28), tiers=4, lights=False)
    W, H = w0, h0
    img.paste(Image.alpha_composite(img, layer), (0, 0), mask)
    ImageDraw.Draw(img).ellipse([30, 30, S - 30, S - 30], outline=(240, 206, 120, 255), width=5)
    img.save(os.path.join(OUT, "icon.png"))
    img.save(os.path.join(OUT, "icon.ico"), sizes=[(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)])


if __name__ == "__main__":
    background()
    icon()
