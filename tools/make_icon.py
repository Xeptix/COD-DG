"""Draws COD Downgrader's icon: a rewind arrow around a download arrow, on a dark rounded square.

    python tools/make_icon.py

Writes src/CODDowngrader/Assets/icon.ico (every size Windows asks for) and icon.png (256 px, for the window and Linux).
"""
import math
from pathlib import Path

from PIL import Image, ImageDraw

ROOT = Path(__file__).resolve().parent.parent
ASSETS = ROOT / "src" / "CODDowngrader" / "Assets"
SIZE = 1024
BACKGROUND = (29, 39, 51, 255)
AMBER = (242, 169, 59, 255)
WHITE = (245, 245, 247, 255)


def draw() -> Image.Image:
    image = Image.new("RGBA", (SIZE, SIZE), (0, 0, 0, 0))
    d = ImageDraw.Draw(image)
    d.rounded_rectangle((40, 40, SIZE - 40, SIZE - 40), radius=210, fill=BACKGROUND)

    # The rewind ring: most of a circle, open at the lower left, with its head at that end pointing anticlockwise.
    centre, radius, width = SIZE / 2, 318, 86
    box = (centre - radius, centre - radius, centre + radius, centre + radius)
    start, end = 200, 480
    d.arc(box, start=start, end=end, fill=AMBER, width=width)
    angle = math.radians(start)
    tip = (centre + radius * math.cos(angle), centre + radius * math.sin(angle))
    # Going anticlockwise on screen, where y grows downwards, the way on is (sin, -cos) of the angle.
    way = (math.sin(angle), -math.cos(angle))
    across = (math.cos(angle), math.sin(angle))
    length, half = 170, 125
    apex = (tip[0] + length * way[0], tip[1] + length * way[1])
    d.polygon([
        apex,
        (tip[0] + half * across[0], tip[1] + half * across[1]),
        (tip[0] - half * across[0], tip[1] - half * across[1]),
    ], fill=AMBER)

    # The download arrow in the middle.
    shaft = 62
    d.rounded_rectangle((centre - shaft, centre - 190, centre + shaft, centre + 40), radius=26, fill=WHITE)
    d.polygon([(centre - 150, centre + 10), (centre + 150, centre + 10), (centre, centre + 175)], fill=WHITE)
    return image


def main() -> None:
    ASSETS.mkdir(parents=True, exist_ok=True)
    image = draw()
    image.resize((256, 256), Image.LANCZOS).save(ASSETS / "icon.png")
    image.save(ASSETS / "icon.ico", sizes=[(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)])
    print(f"Wrote {ASSETS / 'icon.ico'} and {ASSETS / 'icon.png'}")


if __name__ == "__main__":
    main()
