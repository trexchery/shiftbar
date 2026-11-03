"""Composes the README hero (assets/readme/hero.png) and the 1280x640 social preview (assets/social-preview.png) from
the mark and two real renders: assets/readme/flyout.png and toast.png.

Those two come from the tray's own render tests (synthetic accounts, no real emails). Regenerate them with
    $env:PG_TEST_SCALE=2; $env:DOCK_RENDER_SESSIONS_OUT="$PWD\assets\readme\flyout.png"
    $env:DOCK_RENDER_TOAST_ACTION_OUT="$PWD\assets\readme\toast.png"
    dotnet test dock/Shiftbar.Tests/Shiftbar.Tests.csproj -c Release --filter FullyQualifiedName~RenderTests.SessionsPaceKeyboardAndToastAction
Run: uv run --with pillow dock/tools/make-readme-art.py"""
import importlib.util
from pathlib import Path

from PIL import Image, ImageDraw, ImageFilter

ROOT = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location("make_icon", Path(__file__).with_name("make-icon.py"))
icon = importlib.util.module_from_spec(spec)
spec.loader.exec_module(icon)

INDIGO = (139, 147, 255)
TEXT = (234, 236, 248)
MUTED = (156, 160, 190)


def backdrop(w, h):
    img = Image.new("RGB", (w, h))
    d = ImageDraw.Draw(img)
    for y in range(h):  # a quiet vertical glow, low and dark
        t = y / h
        d.line([(0, y), (w, y)], fill=(int(16 + 8 * t), int(17 + 10 * t), int(30 + 22 * t)))
    return img


def shot(name, height=None, width=None):
    im = Image.open(ROOT / "assets" / "readme" / name).convert("RGBA")
    s = (height / im.height) if height else (width / im.width)
    return im.resize((round(im.width * s), round(im.height * s)), Image.LANCZOS)


def place(img, im, xy, radius=10):
    """Pastes `im` with rounded corners, a hairline border and a soft shadow."""
    x, y = xy
    mask = Image.new("L", im.size, 0)
    ImageDraw.Draw(mask).rounded_rectangle((0, 0, im.width - 1, im.height - 1), radius, fill=255)
    shadow = Image.new("RGBA", (im.width + 120, im.height + 120), (0, 0, 0, 0))
    ImageDraw.Draw(shadow).rounded_rectangle((60, 70, 60 + im.width, 70 + im.height), radius, fill=(0, 0, 0, 150))
    shadow = shadow.filter(ImageFilter.GaussianBlur(24))
    img.paste(shadow, (x - 60, y - 60), shadow)
    img.paste(im, (x, y), mask)
    ImageDraw.Draw(img).rounded_rectangle((x, y, x + im.width - 1, y + im.height - 1), radius, outline=(70, 74, 110), width=1)


def hero(path):
    w, h = 1600, 760
    img = backdrop(w, h)
    d = ImageDraw.Draw(img)
    tile = icon.mark(132)
    img.paste(tile, (100, 150), tile)
    title = icon.font(["C:/Windows/Fonts/segoeuib.ttf", "DejaVuSans-Bold.ttf"], 108)
    sub = icon.font(["C:/Windows/Fonts/segoeui.ttf", "DejaVuSans.ttf"], 42)
    small = icon.font(["C:/Windows/Fonts/segoeui.ttf", "DejaVuSans.ttf"], 30)
    d.text((262, 142), "Shiftbar", font=title, fill=INDIGO)
    d.text((104, 330), "Every Claude account, one tray.", font=sub, fill=TEXT)
    for i, line in enumerate(["Switch the default login without closing a session.",
                              "Run accounts side by side in their own terminals.",
                              "See every limit; carry on when one is hit."]):
        d.text((104, 420 + i * 48), line, font=small, fill=MUTED)
    flyout = shot("flyout.png", height=676)
    place(img, flyout, (w - flyout.width - 130, 42))
    toast = shot("toast.png", width=470)
    place(img, toast, (w - flyout.width - 130 - 250, h - toast.height - 60), radius=8)
    img.save(path, optimize=True)


def social(path):
    w, h = 1280, 640
    img = backdrop(w, h)
    d = ImageDraw.Draw(img)
    tile = icon.mark(112)
    img.paste(tile, (80, 90), tile)
    title = icon.font(["C:/Windows/Fonts/segoeuib.ttf", "DejaVuSans-Bold.ttf"], 92)
    sub = icon.font(["C:/Windows/Fonts/segoeui.ttf", "DejaVuSans.ttf"], 36)
    small = icon.font(["C:/Windows/Fonts/segoeui.ttf", "DejaVuSans.ttf"], 26)
    d.text((216, 80), "Shiftbar", font=title, fill=INDIGO)
    d.text((84, 262), "Every Claude account,", font=sub, fill=TEXT)
    d.text((84, 308), "one tray.", font=sub, fill=TEXT)
    d.text((84, 400), "Switching, side-by-side sessions,", font=small, fill=MUTED)
    d.text((84, 436), "unified usage and limit handling", font=small, fill=MUTED)
    d.text((84, 472), "for Claude Code. Windows and macOS.", font=small, fill=MUTED)
    flyout = shot("flyout.png", height=560)
    place(img, flyout, (w - flyout.width - 90, 40))
    toast = shot("toast.png", width=400)
    place(img, toast, (w - flyout.width - 90 - 190, h - toast.height - 60), radius=8)
    img.save(path, optimize=True)


if __name__ == "__main__":
    hero(ROOT / "assets" / "readme" / "hero.png")
    social(ROOT / "assets" / "social-preview.png")
    print("wrote assets/readme/hero.png, assets/social-preview.png")
