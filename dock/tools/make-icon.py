"""Draws the Shiftbar mark (an indigo tile with two meter bars, the lower one shifted right) and writes every asset
built from it: dock/Shiftbar/dock.ico (7 sizes, 16 to 256), assets/shiftbar.png (macOS turns it into the .icns in
macos/build-macos.sh). The README hero and social preview are composed by make-readme-art.py.
Run: uv run --with pillow dock/tools/make-icon.py"""
import io
import struct
from pathlib import Path

from PIL import Image, ImageDraw, ImageFont

ROOT = Path(__file__).resolve().parents[2]

TILE = (79, 91, 213)        # brand indigo, light theme (#4F5BD5); white on it is 5.5:1
TILE_TOP = (107, 117, 240)  # a quiet top-to-bottom sheen at large sizes
BAR = (255, 255, 255)
BAR_DIM = (205, 210, 255)   # the upper (spent) bar reads a step quieter than the lower (fresh) one

# On a 16-unit grid, so at 16 px every edge is a whole pixel: two 3-unit bars, 2 units apart, the lower one shifted
# right by 3. The tray dot covers the lower right (7/16), so it lands on the lower bar's end like a meter's status light.
BARS = [((3, 4, 10, 7), BAR_DIM), ((6, 9, 13, 12), BAR)]
RADIUS = 3.5


def mark(size):
    """The tile at `size` px, drawn 8x and box-filtered down so edges on the grid stay sharp."""
    ss = 8
    big = size * ss
    unit = big / 16
    img = Image.new("RGBA", (big, big), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    if size >= 48:  # the sheen only where it is visible
        mask = Image.new("L", (big, big), 0)
        ImageDraw.Draw(mask).rounded_rectangle((0, 0, big - 1, big - 1), RADIUS * unit, fill=255)
        grad = Image.new("RGBA", (big, big))
        gd = ImageDraw.Draw(grad)
        for y in range(big):
            t = y / big
            gd.line([(0, y), (big, y)], fill=tuple(round(a + (b - a) * t) for a, b in zip(TILE_TOP, TILE)) + (255,))
        img.paste(grad, (0, 0), mask)
    else:
        d.rounded_rectangle((0, 0, big - 1, big - 1), RADIUS * unit, fill=TILE + (255,))
    for (x0, y0, x1, y1), color in BARS:
        # Snap to the target pixel grid first, so 24 and 48 stay crisp too.
        box = [round(v * size / 16) * ss for v in (x0, y0, x1, y1)]
        d.rounded_rectangle((box[0], box[1], box[2] - 1, box[3] - 1), (0 if size < 32 else 1.2 * unit), fill=color + (255,))
    return img.resize((size, size), Image.BOX)


def write_ico(path, sizes=(16, 24, 32, 48, 64, 128, 256)):
    pngs = []
    for n in sizes:
        buf = io.BytesIO()
        mark(n).save(buf, "PNG")
        pngs.append(buf.getvalue())
    out = bytearray(struct.pack("<HHH", 0, 1, len(sizes)))
    offset = 6 + 16 * len(sizes)
    for n, data in zip(sizes, pngs):
        out += struct.pack("<BBBBHHII", n % 256, n % 256, 0, 0, 1, 32, len(data), offset)
        offset += len(data)
    for data in pngs:
        out += data
    Path(path).write_bytes(bytes(out))


def font(names, size):
    for n in names:
        try:
            return ImageFont.truetype(n, size)
        except OSError:
            pass
    return ImageFont.load_default()


if __name__ == "__main__":
    write_ico(ROOT / "dock" / "Shiftbar" / "dock.ico")
    mark(1024).save(ROOT / "assets" / "shiftbar.png", optimize=True)  # build-macos.sh scales it to 16..1024 for the .icns
    print("wrote dock.ico, assets/shiftbar.png")
