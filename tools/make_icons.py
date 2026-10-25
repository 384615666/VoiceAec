"""Build multi-size Windows .ico files from the concept artwork.

Run from the project root:  python tools/make_icons.py
The generated icons are committed; the PSD-level source stays in assets/.
"""

from __future__ import annotations

import io
import os
import struct
import sys

from PIL import Image, ImageDraw, ImageFilter

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SOURCE = os.path.join(ROOT, "assets", "icon-source.png")
APP_ICO = os.path.join(ROOT, "assets", "VoiceAec.ico")
TRAY_ICO = os.path.join(ROOT, "assets", "VoiceAecTray.ico")
PREVIEW_DIR = os.environ.get("ICON_PREVIEW_DIR", os.path.join(ROOT, "artifacts", "icon-preview"))


def artwork_bounds(img: Image.Image) -> tuple[int, int, int, int]:
    alpha = img.getchannel("A")
    return alpha.getbbox() or (0, 0, img.width, img.height)


def square_crop(img: Image.Image, center: tuple[float, float], side: int) -> Image.Image:
    cx, cy = center
    left = int(round(cx - side / 2))
    top = int(round(cy - side / 2))
    return img.crop((left, top, left + side, top + side))


def rounded_mask(size: int, radius: float) -> Image.Image:
    scale = 4
    big = Image.new("L", (size * scale, size * scale), 0)
    draw = ImageDraw.Draw(big)
    draw.rounded_rectangle(
        (0, 0, size * scale - 1, size * scale - 1),
        radius=radius * scale,
        fill=255,
    )
    return big.resize((size, size), Image.LANCZOS)


def prepare(img: Image.Image, size: int, boost: bool) -> Image.Image:
    small = img.resize((size, size), Image.LANCZOS)
    if boost and size <= 48:
        small = small.filter(ImageFilter.UnsharpMask(radius=max(0.6, size / 18), percent=110, threshold=1))
        # Keep the mint/bow accents from washing out once the icon is tiny.
        small = Image.eval(small, lambda v: v)
    return small


def dib_bytes(img: Image.Image) -> bytes:
    """32bpp bottom-up DIB with an empty AND mask, as stored inside ICO."""
    img = img.convert("RGBA")
    w, h = img.size
    pixels = img.load()
    rows = []
    for y in range(h - 1, -1, -1):
        row = bytearray()
        for x in range(w):
            r, g, b, a = pixels[x, y]
            row += bytes((b, g, r, a))
        rows.append(bytes(row))
    header = struct.pack(
        "<IiiHHIIiiII", 40, w, h * 2, 1, 32, 0, len(rows) * len(rows[0]), 0, 0, 0, 0
    )
    mask_row_bytes = ((w + 31) // 32) * 4
    mask = bytes(mask_row_bytes * h)
    return header + b"".join(rows) + mask


def png_bytes(img: Image.Image) -> bytes:
    buffer = io.BytesIO()
    img.save(buffer, format="PNG", optimize=True)
    return buffer.getvalue()


def write_ico(path: str, frames: list[Image.Image], png_from: int = 128) -> None:
    entries = []
    payloads = []
    offset = 6 + 16 * len(frames)
    for frame in frames:
        blob = png_bytes(frame) if frame.width >= png_from else dib_bytes(frame)
        w = 0 if frame.width >= 256 else frame.width
        h = 0 if frame.height >= 256 else frame.height
        entries.append(struct.pack("<BBBBHHII", w, h, 0, 0, 1, 32, len(blob), offset))
        payloads.append(blob)
        offset += len(blob)
    with open(path, "wb") as handle:
        handle.write(struct.pack("<HHH", 0, 1, len(frames)))
        handle.write(b"".join(entries))
        handle.write(b"".join(payloads))
    print(f"wrote {path} ({os.path.getsize(path) // 1024} KiB, {len(frames)} frames)")


def main() -> int:
    source = Image.open(SOURCE).convert("RGBA")
    left, top, right, bottom = artwork_bounds(source)
    side = max(right - left, bottom - top)
    cx, cy = (left + right) / 2, (top + bottom) / 2

    # Full artwork: the rounded-square body already carries its own alpha.
    app_art = square_crop(source, (cx, cy), side)

    # Tray art: zoom into the microphone so it still reads at 16 px. Corners are
    # re-rounded because the crop cuts the original squircle away.
    tray_zoom = 0.84  # 84% of the artwork square -> mic fills more of the frame
    tray_art = square_crop(source, (cx, cy - side * 0.01), int(round(side * tray_zoom)))
    tray_art = tray_art.resize((1024, 1024), Image.LANCZOS)
    tray_art.putalpha(rounded_mask(1024, 1024 * 0.235))
    small_art = zoom_art(tray_art, 1.35)  # ~1.6x overall: cat face fills 16 px

    # Small shell sizes need a tighter crop; 32 px and up carry the full
    # artwork so the stand, base and breathing room survive.
    app_plan = {16: small_art, 20: small_art, 24: small_art}
    tray_plan = {16: small_art, 20: small_art, 24: small_art, 32: tray_art, 40: tray_art}
    app_sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256]
    tray_sizes = [16, 20, 24, 32, 40, 48, 64]
    write_ico(APP_ICO, [prepare(app_plan.get(s, app_art), s, True) for s in app_sizes])
    write_ico(TRAY_ICO, [prepare(tray_plan.get(s, app_art), s, True) for s in tray_sizes])

    # Contact sheet for the visual review pass.
    os.makedirs(PREVIEW_DIR, exist_ok=True)
    build_preview(app_art, tray_art, small_art, PREVIEW_DIR)
    return 0


def build_preview(app_art: Image.Image, tray_art: Image.Image, small_art: Image.Image, out_dir: str) -> None:
    candidates = [
        ("A full artwork", app_art),
        ("B tray crop (32px)", tray_art),
        ("C small crop (16-24px)", small_art),
    ]
    sizes = [16, 24, 32, 48]
    zoom = 8
    pad = 26
    cell_w = 48 * zoom + pad * 2
    sheet = Image.new("RGBA", (cell_w * len(sizes), 216 * len(candidates) + 20), (246, 246, 246, 255))
    draw = ImageDraw.Draw(sheet)
    for row, (label, art) in enumerate(candidates):
        y = 20 + row * 216
        draw.text((pad, y - 6), label, fill=(40, 40, 40, 255))
        for col, size in enumerate(sizes):
            x = col * cell_w + pad
            tile = prepare(art, size, True)
            sheet.alpha_composite(tile, (x, y + 18))
            dark = Image.new("RGBA", tile.size, (32, 32, 32, 255))
            sheet.alpha_composite(dark, (x + 70, y + 18))
            sheet.alpha_composite(tile, (x + 70, y + 18))
            big = tile.resize((size * zoom, size * zoom), Image.NEAREST)
            sheet.alpha_composite(big, (x, y + 52))
            draw.text((x, y + 18 + size + 30), f"{size}px", fill=(90, 90, 90, 255))
    sheet.convert("RGB").save(os.path.join(out_dir, "candidates.png"))

    # Simulated taskbar strip at real pixel size plus a magnified view.
    lane = 150
    bar = Image.new("RGBA", (lane * len(candidates) + 30, 130), (24, 24, 24, 255))
    bar_draw = ImageDraw.Draw(bar)
    bar_draw.text((14, 8), "simulated tray (real 16 px) + 64 px magnified", fill=(190, 190, 190, 255))
    for i, (_, art) in enumerate(candidates):
        tile = prepare(art, 16, True)
        x = 20 + i * lane
        bar_draw.rounded_rectangle((x - 8, 30, x + 40, 78), radius=8, fill=(43, 43, 43, 255))
        bar.alpha_composite(tile, (x, 38))
        bar.alpha_composite(tile.resize((64, 64), Image.NEAREST), (x + 58, 30))
        bar_draw.text((x, 92), "ABC"[i], fill=(220, 220, 220, 255))
    bar.convert("RGB").save(os.path.join(out_dir, "taskbar.png"))
    print(f"wrote previews to {out_dir}")


def zoom_art(art: Image.Image, factor: float) -> Image.Image:
    side = int(1024 / factor)
    cropped = square_crop(art, (512, 512), side).resize((1024, 1024), Image.LANCZOS)
    cropped.putalpha(rounded_mask(1024, 1024 * min(0.30, 0.235 * factor)))
    return cropped


if __name__ == "__main__":
    sys.exit(main())
