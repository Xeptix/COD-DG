"""Draws the flags the window shows beside each Steam language, simplified, as PNGs in src/CODDowngrader/Assets/Flags.

    python tools/make_flags.py

Each is drawn at four times its size and scaled down, so the edges are smooth. The file is named after Steam's language code
(english, french, schinese...), which is what product info calls a depot's language.
"""
import math
from pathlib import Path

from PIL import Image, ImageDraw

ROOT = Path(__file__).resolve().parent.parent
OUT = ROOT / "src" / "CODDowngrader" / "Assets" / "Flags"
W, H = 48, 32
S = 4  # drawn at this many times the size, then scaled down

RED = (206, 17, 38)
WHITE = (255, 255, 255)
BLUE = (0, 57, 166)
NAVY = (1, 33, 105)
GREEN = (0, 146, 70)
YELLOW = (255, 206, 0)
BLACK = (0, 0, 0)


def canvas():
    image = Image.new("RGB", (W * S, H * S), WHITE)
    return image, ImageDraw.Draw(image)


def w(): return W * S
def h(): return H * S


def stripes(colours, vertical=False):
    image, d = canvas()
    n = len(colours)
    for i, c in enumerate(colours):
        if vertical:
            d.rectangle((w() * i / n, 0, w() * (i + 1) / n, h()), fill=c)
        else:
            d.rectangle((0, h() * i / n, w(), h() * (i + 1) / n), fill=c)
    return image, d


def star(d, cx, cy, r, fill, rotation=-90):
    points = []
    for i in range(10):
        radius = r if i % 2 == 0 else r * 0.382
        angle = math.radians(rotation + i * 36)
        points.append((cx + radius * math.cos(angle), cy + radius * math.sin(angle)))
    d.polygon(points, fill=fill)


def nordic(background, cross, inner=None):
    image, d = canvas()
    d.rectangle((0, 0, w(), h()), fill=background)
    t = h() * (0.25 if inner else 0.2)
    x = w() * 0.36
    d.rectangle((x - t / 2, 0, x + t / 2, h()), fill=cross)
    d.rectangle((0, h() / 2 - t / 2, w(), h() / 2 + t / 2), fill=cross)
    if inner:
        t2 = t * 0.5
        d.rectangle((x - t2 / 2, 0, x + t2 / 2, h()), fill=inner)
        d.rectangle((0, h() / 2 - t2 / 2, w(), h() / 2 + t2 / 2), fill=inner)
    return image


def united_kingdom():
    image, d = canvas()
    d.rectangle((0, 0, w(), h()), fill=NAVY)
    width = h() * 0.2
    d.line((0, 0, w(), h()), fill=WHITE, width=int(width))
    d.line((0, h(), w(), 0), fill=WHITE, width=int(width))
    d.line((0, 0, w(), h()), fill=RED, width=int(width * 0.4))
    d.line((0, h(), w(), 0), fill=RED, width=int(width * 0.4))
    d.rectangle((w() / 2 - h() * 0.17, 0, w() / 2 + h() * 0.17, h()), fill=WHITE)
    d.rectangle((0, h() / 2 - h() * 0.17, w(), h() / 2 + h() * 0.17), fill=WHITE)
    d.rectangle((w() / 2 - h() * 0.1, 0, w() / 2 + h() * 0.1, h()), fill=RED)
    d.rectangle((0, h() / 2 - h() * 0.1, w(), h() / 2 + h() * 0.1), fill=RED)
    return image


def japan():
    image, d = canvas()
    r = h() * 0.3
    d.ellipse((w() / 2 - r, h() / 2 - r, w() / 2 + r, h() / 2 + r), fill=(188, 0, 45))
    return image


def korea():
    image, d = canvas()
    r = h() * 0.25
    cx, cy = w() / 2, h() / 2
    d.pieslice((cx - r, cy - r, cx + r, cy + r), 180, 360, fill=(205, 46, 58))
    d.pieslice((cx - r, cy - r, cx + r, cy + r), 0, 180, fill=(0, 71, 160))
    for sx, sy in ((-1, -1), (1, 1), (1, -1), (-1, 1)):
        for k in range(3):
            x = cx + sx * w() * 0.3
            y = cy + sy * h() * 0.3
            d.line((x - h() * 0.08, y - h() * 0.1 + k * h() * 0.07, x + h() * 0.08, y - h() * 0.1 + k * h() * 0.07), fill=BLACK, width=int(h() * 0.035))
    return image


def china():
    image, d = canvas()
    d.rectangle((0, 0, w(), h()), fill=(238, 28, 37))
    star(d, w() * 0.17, h() * 0.27, h() * 0.15, (255, 255, 0))
    for fx, fy in ((0.33, 0.1), (0.4, 0.2), (0.4, 0.35), (0.33, 0.45)):
        star(d, w() * fx, h() * fy, h() * 0.05, (255, 255, 0))
    return image


def taiwan():
    image, d = canvas()
    d.rectangle((0, 0, w(), h()), fill=(254, 0, 0))
    d.rectangle((0, 0, w() / 2, h() / 2), fill=(0, 0, 149))
    cx, cy, r = w() / 4, h() / 4, h() * 0.13
    for i in range(12):
        a = math.radians(i * 30)
        d.polygon([(cx + r * 1.4 * math.cos(a), cy + r * 1.4 * math.sin(a)),
                   (cx + r * 0.6 * math.cos(a + 0.26), cy + r * 0.6 * math.sin(a + 0.26)),
                   (cx + r * 0.6 * math.cos(a - 0.26), cy + r * 0.6 * math.sin(a - 0.26))], fill=WHITE)
    d.ellipse((cx - r * 0.7, cy - r * 0.7, cx + r * 0.7, cy + r * 0.7), fill=WHITE)
    return image


def brazil():
    image, d = canvas()
    d.rectangle((0, 0, w(), h()), fill=(0, 156, 59))
    d.polygon([(w() * 0.08, h() / 2), (w() / 2, h() * 0.08), (w() * 0.92, h() / 2), (w() / 2, h() * 0.92)], fill=(255, 223, 0))
    r = h() * 0.23
    d.ellipse((w() / 2 - r, h() / 2 - r, w() / 2 + r, h() / 2 + r), fill=(0, 39, 118))
    return image


def portugal():
    image, d = canvas()
    d.rectangle((0, 0, w(), h()), fill=(255, 0, 0))
    d.rectangle((0, 0, w() * 0.4, h()), fill=(0, 102, 0))
    r = h() * 0.2
    d.ellipse((w() * 0.4 - r, h() / 2 - r, w() * 0.4 + r, h() / 2 + r), fill=(255, 255, 0))
    return image


def spain():
    image, d = canvas()
    d.rectangle((0, 0, w(), h()), fill=(170, 21, 27))
    d.rectangle((0, h() * 0.25, w(), h() * 0.75), fill=(241, 191, 0))
    return image


def mexico():
    image, d = stripes([(0, 104, 71), WHITE, (206, 17, 38)], vertical=True)
    r = h() * 0.12
    d.ellipse((w() / 2 - r, h() / 2 - r, w() / 2 + r, h() / 2 + r), fill=(140, 90, 40))
    return image


def arabic():
    image, d = canvas()
    d.rectangle((0, 0, w(), h()), fill=(0, 108, 53))
    d.rectangle((w() * 0.25, h() * 0.3, w() * 0.75, h() * 0.42), fill=WHITE)
    d.rectangle((w() * 0.25, h() * 0.62, w() * 0.72, h() * 0.66), fill=WHITE)
    return image


def turkey():
    image, d = canvas()
    d.rectangle((0, 0, w(), h()), fill=(227, 10, 23))
    r = h() * 0.25
    cx, cy = w() * 0.38, h() / 2
    d.ellipse((cx - r, cy - r, cx + r, cy + r), fill=WHITE)
    r2 = r * 0.8
    d.ellipse((cx - r2 + r * 0.25, cy - r2, cx + r2 + r * 0.25, cy + r2), fill=(227, 10, 23))
    star(d, w() * 0.6, h() / 2, h() * 0.1, WHITE, rotation=180)
    return image


def czech():
    image, d = stripes([WHITE, (215, 20, 26)])
    d.polygon([(0, 0), (w() * 0.5, h() / 2), (0, h())], fill=(17, 69, 126))
    return image


def greece():
    image, d = canvas()
    blue = (13, 94, 175)
    for i in range(9):
        d.rectangle((0, h() * i / 9, w(), h() * (i + 1) / 9), fill=blue if i % 2 == 0 else WHITE)
    d.rectangle((0, 0, h() * 5 / 9, h() * 5 / 9), fill=blue)
    t = h() / 9
    d.rectangle((h() * 5 / 18 - t / 2, 0, h() * 5 / 18 + t / 2, h() * 5 / 9), fill=WHITE)
    d.rectangle((0, h() * 5 / 18 - t / 2, h() * 5 / 9, h() * 5 / 18 + t / 2), fill=WHITE)
    return image


def thailand():
    image, _ = stripes([(165, 25, 49), WHITE, (45, 42, 74), (45, 42, 74), WHITE, (165, 25, 49)])
    return image


def vietnam():
    image, d = canvas()
    d.rectangle((0, 0, w(), h()), fill=(218, 37, 29))
    star(d, w() / 2, h() / 2, h() * 0.3, (255, 255, 0))
    return image


FLAGS = {
    "english": united_kingdom,
    "french": lambda: stripes([(0, 35, 149), WHITE, (237, 41, 57)], vertical=True)[0],
    "german": lambda: stripes([BLACK, (221, 0, 0), (255, 206, 0)])[0],
    "italian": lambda: stripes([(0, 146, 70), WHITE, (206, 43, 55)], vertical=True)[0],
    "spanish": spain,
    "latam": mexico,
    "polish": lambda: stripes([WHITE, (220, 20, 60)])[0],
    "russian": lambda: stripes([WHITE, (0, 57, 166), (213, 43, 30)])[0],
    "japanese": japan,
    "koreana": korea,
    "schinese": china,
    "tchinese": taiwan,
    "brazilian": brazil,
    "portuguese": portugal,
    "arabic": arabic,
    "dutch": lambda: stripes([(174, 28, 40), WHITE, (33, 70, 139)])[0],
    "czech": czech,
    "hungarian": lambda: stripes([(206, 41, 57), WHITE, (71, 112, 80)])[0],
    "turkish": turkey,
    "ukrainian": lambda: stripes([(0, 87, 183), (255, 215, 0)])[0],
    "swedish": lambda: nordic((0, 106, 167), (254, 204, 0)),
    "danish": lambda: nordic((200, 16, 46), WHITE),
    "finnish": lambda: nordic(WHITE, (0, 47, 108)),
    "norwegian": lambda: nordic((186, 12, 47), WHITE, (0, 32, 91)),
    "greek": greece,
    "romanian": lambda: stripes([(0, 43, 127), (252, 209, 22), (206, 17, 38)], vertical=True)[0],
    "bulgarian": lambda: stripes([WHITE, (0, 150, 110), (214, 38, 18)])[0],
    "thai": thailand,
    "vietnamese": vietnam,
    "indonesian": lambda: stripes([(206, 17, 38), WHITE])[0],
}


def main() -> None:
    OUT.mkdir(parents=True, exist_ok=True)
    for code, draw in FLAGS.items():
        image = draw().resize((W, H), Image.LANCZOS)
        image.save(OUT / f"{code}.png", optimize=True)
    print(f"{len(FLAGS)} flags in {OUT.relative_to(ROOT)}")


if __name__ == "__main__":
    main()
