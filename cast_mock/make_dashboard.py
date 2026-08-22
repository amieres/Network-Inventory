"""Render mock dashboard frames: a dark HA-style panel with a live ticking clock.

Frames are written as PNGs to a temp dir; ffmpeg turns them into an HLS stream.
Kept deliberately dependency-light: Pillow only.
"""

import math
import os
import sys
from datetime import datetime, timedelta

from PIL import Image, ImageDraw, ImageFont

W, H     = 1280, 720
BG       = (16, 20, 26)
CARD     = (30, 37, 47)
ACCENT   = (3, 169, 244)
TEXT     = (236, 239, 241)
MUTED    = (130, 145, 160)
GREEN    = (76, 200, 120)
AMBER    = (255, 179, 0)

FONT_DIR = "C:/Windows/Fonts"

def font(name, size):
    return ImageFont.truetype(os.path.join(FONT_DIR, name), size)

F_CLOCK  = font("consola.ttf", 132)
F_TITLE  = font("arialbd.ttf",  40)
F_CARD   = font("arialbd.ttf",  27)
F_VALUE  = font("arialbd.ttf",  52)
F_SMALL  = font("arial.ttf",    20)

# label, value, unit, colour
CARDS = [ ("Living room",  "22.4", "\u00b0C", ACCENT)
        , ("Battery SOC",  "87",   "%",       GREEN )
        , ("Solar now",    "3.1",  "kW",      AMBER )
        , ("Garage",       "Closed", "",      MUTED )
        ]

def rounded(draw, box, radius, fill):
    draw.rounded_rectangle(box, radius=radius, fill=fill)

def render(t, when):
    """t = seconds since start (float); when = datetime to display."""
    img  = Image.new("RGB", (W, H), BG)
    d    = ImageDraw.Draw(img)

    # header
    d.text((60, 44), "Mock Dashboard", font=F_TITLE, fill=TEXT)
    d.text((60, 96), "local cast facsimile \u2014 default media receiver",
           font=F_SMALL, fill=MUTED)
    d.line((60, 132, W - 60, 132), fill=(50, 60, 72), width=2)

    # clock
    clock = when.strftime("%H:%M:%S")
    bbox  = d.textbbox((0, 0), clock, font=F_CLOCK)
    cw    = bbox[2] - bbox[0]
    d.text(((W - cw) // 2, 168), clock, font=F_CLOCK, fill=TEXT)
    date  = when.strftime("%A, %d %B %Y")
    bbox  = d.textbbox((0, 0), date, font=F_CARD)
    dw    = bbox[2] - bbox[0]
    d.text(((W - dw) // 2, 318), date, font=F_CARD, fill=MUTED)

    # cards
    cw_, ch_, gap = 268, 168, 24
    total = len(CARDS) * cw_ + (len(CARDS) - 1) * gap
    x0    = (W - total) // 2
    y0    = 396
    for i, (label, value, unit, colour) in enumerate(CARDS):
        x = x0 + i * (cw_ + gap)
        rounded(d, (x, y0, x + cw_, y0 + ch_), 16, CARD)
        d.text((x + 22, y0 + 20), label, font=F_CARD, fill=MUTED)
        d.text((x + 22, y0 + 62), value, font=F_VALUE, fill=colour)
        if unit:
            vb = d.textbbox((0, 0), value, font=F_VALUE)
            d.text((x + 26 + (vb[2] - vb[0]), y0 + 88), unit,
                   font=F_CARD, fill=colour)
        # activity bar so every card visibly moves
        phase = (math.sin(t * 1.6 + i * 1.1) + 1) / 2
        bw    = int((cw_ - 44) * (0.25 + 0.75 * phase))
        d.rounded_rectangle((x + 22, y0 + ch_ - 26, x + 22 + bw, y0 + ch_ - 18),
                            radius=4, fill=colour)

    # sweeping progress bar
    by = H - 74
    d.rounded_rectangle((60, by, W - 60, by + 12), radius=6, fill=CARD)
    span  = W - 120
    pos   = (math.sin(t * 0.9) + 1) / 2
    knob  = int(span * pos)
    d.rounded_rectangle((60, by, 60 + knob, by + 12), radius=6, fill=ACCENT)

    # moving dot
    cx = 60 + knob
    d.ellipse((cx - 11, by - 5, cx + 11, by + 17), fill=TEXT)

    d.text((60, H - 40), f"t+{int(t):04d}s", font=F_SMALL, fill=MUTED)
    return img


def main():
    outdir = sys.argv[1]
    fps    = int(sys.argv[2]) if len(sys.argv) > 2 else 5
    secs   = int(sys.argv[3]) if len(sys.argv) > 3 else 300
    os.makedirs(outdir, exist_ok=True)
    start  = datetime.now()
    total  = fps * secs
    for n in range(total):
        t     = n / fps
        when  = start + timedelta(seconds=t)
        img   = render(t, when)
        img.save(os.path.join(outdir, f"f{n:06d}.png"))
        if n % (fps * 10) == 0:
            print(f"  rendered {n}/{total}", flush=True)
    print("done", total)


if __name__ == "__main__":
    main()
