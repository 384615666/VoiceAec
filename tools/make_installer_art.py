"""Build the Inno Setup wizard artwork from the app icon.

Outputs a full-bleed side panel (cream → mint → sky) and a small header mark,
at 100/125/150/200% scaling, into tools/installer/.
"""

from __future__ import annotations

import os

from PIL import Image, ImageDraw, ImageFont, ImageFilter

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SOURCE = os.path.join(ROOT, "assets", "icon-source.png")
OUT = os.path.join(ROOT, "tools", "installer")
FONT = r"C:\Windows\Fonts\msyh.ttc"
FONT_BOLD = r"C:\Windows\Fonts\msyhbd.ttc"

SIZES = [(1.0, "100"), (1.25, "125"), (1.5, "150"), (2.0, "200")]


def artwork() -> Image.Image:
    img = Image.open(SOURCE).convert("RGBA")
    alpha = img.getchannel("A")
    left, top, right, bottom = alpha.getbbox()
    side = max(right - left, bottom - top)
    cx, cy = (left + right) / 2, (top + bottom) / 2
    box = (int(cx - side / 2), int(cy - side / 2), int(cx + side / 2), int(cy + side / 2))
    return img.crop(box)


def gradient(size: tuple[int, int]) -> Image.Image:
    w, h = size
    base = Image.new("RGB", size, (255, 249, 242))
    draw = ImageDraw.Draw(base)
    stops = [(0.0, (255, 251, 246)), (0.38, (236, 250, 244)), (0.72, (226, 244, 255)), (1.0, (240, 245, 255))]
    for y in range(h):
        t = y / max(1, h - 1)
        for i in range(len(stops) - 1):
            t0, c0 = stops[i]
            t1, c1 = stops[i + 1]
            if t0 <= t <= t1:
                k = (t - t0) / (t1 - t0)
                color = tuple(int(c0[j] + (c1[j] - c0[j]) * k) for j in range(3))
                draw.line([(0, y), (w, y)], fill=color)
                break

    # soft pastel bubbles for depth
    layer = Image.new("RGBA", size, (0, 0, 0, 0))
    ld = ImageDraw.Draw(layer)
    scale = w / 164.0
    for cx, cy, r, color in [
        (28, 54, 46, (252, 217, 236, 90)),
        (142, 116, 58, (214, 247, 232, 120)),
        (34, 250, 62, (217, 239, 251, 110)),
        (132, 286, 40, (240, 232, 255, 90)),
    ]:
        ld.ellipse((cx * scale - r * scale, cy * scale - r * scale,
                    cx * scale + r * scale, cy * scale + r * scale), fill=color)
    layer = layer.filter(ImageFilter.GaussianBlur(12 * scale))
    base = Image.alpha_composite(base.convert("RGBA"), layer)
    return base


def side_panel(scale: float) -> Image.Image:
    w, h = int(164 * scale), int(314 * scale)
    panel = gradient((w, h))
    draw = ImageDraw.Draw(panel)
    art = artwork()

    icon_size = int(96 * scale)
    icon = art.resize((icon_size, icon_size), Image.LANCZOS)
    # soft shadow under the icon
    shadow = Image.new("RGBA", (icon_size + 24, icon_size + 24), (0, 0, 0, 0))
    sd = ImageDraw.Draw(shadow)
    sd.rounded_rectangle((12, 12, icon_size + 12, icon_size + 12), radius=int(22 * scale),
                         fill=(46, 196, 137, 60))
    shadow = shadow.filter(ImageFilter.GaussianBlur(9 * scale))
    icon_x = (w - icon_size) // 2
    icon_y = int(72 * scale)
    panel.alpha_composite(shadow, (icon_x - 12, icon_y - 8))
    panel.alpha_composite(icon, (icon_x, icon_y))

    try:
        title_font = ImageFont.truetype(FONT_BOLD, int(17 * scale))
        sub_font = ImageFont.truetype(FONT, int(8.6 * scale))
    except OSError:
        title_font = ImageFont.load_default()
        sub_font = ImageFont.load_default()

    title = "VoiceAec"
    subtitle = "本地实时音频控制台"
    tw = draw.textlength(title, font=title_font)
    sw = draw.textlength(subtitle, font=sub_font)
    title_y = icon_y + icon_size + int(18 * scale)
    draw.text(((w - tw) / 2, title_y), title, font=title_font, fill=(70, 66, 87, 255))
    draw.text(((w - sw) / 2, title_y + int(24 * scale)), subtitle, font=sub_font, fill=(116, 101, 133, 255))

    # thin accent rule
    rule_w = int(36 * scale)
    rule_y = title_y + int(46 * scale)
    draw.rounded_rectangle(((w - rule_w) / 2, rule_y, (w + rule_w) / 2, rule_y + max(2, int(2 * scale))),
                           radius=max(1, int(scale)), fill=(46, 196, 137, 200))
    return panel.convert("RGB")


def small_mark(scale: float) -> Image.Image:
    size = int(55 * scale)
    canvas = Image.new("RGB", (size, size), (255, 255, 255))
    art = artwork().resize((int(size * 0.86), int(size * 0.86)), Image.LANCZOS)
    offset = (size - art.width) // 2
    canvas.paste(art, (offset, offset), art)
    return canvas


def main() -> None:
    os.makedirs(OUT, exist_ok=True)
    for scale, tag in SIZES:
        side_panel(scale).save(os.path.join(OUT, f"wizard-side-{tag}.bmp"))
        small_mark(scale).save(os.path.join(OUT, f"wizard-small-{tag}.bmp"))
    # previews for review
    preview = side_panel(1.0).resize((328, 628), Image.LANCZOS)
    preview.save(os.path.join(OUT, "wizard-side-preview.png"))
    small_mark(2.0).save(os.path.join(OUT, "wizard-small-preview.png"))
    print("wizard artwork written to", OUT)


if __name__ == "__main__":
    main()
