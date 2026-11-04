"""Builds src/Shadepane.App/Assets/Shadepane.ico from design/app-icon.svg.

Each size is rendered from the SVG separately (rsvg-convert), not downscaled from 256 px.
Sizes up to 64 px are stored as 32-bit BMP (the most compatible form), 256 px as PNG.
Usage: python3 design/build-icon.py   (needs rsvg-convert and Pillow)
"""
import io, struct, subprocess, sys
from pathlib import Path
from PIL import Image

ROOT = Path(__file__).resolve().parent.parent
SVG = ROOT / "design/app-icon.svg"
OUT = ROOT / "src/Shadepane.App/Assets/Shadepane.ico"
SIZES = [16, 20, 24, 32, 40, 48, 64, 256]

def render(size: int) -> Image.Image:
    png = subprocess.run(["rsvg-convert", "-w", str(size), "-h", str(size), str(SVG)], check=True, capture_output=True).stdout
    return Image.open(io.BytesIO(png)).convert("RGBA")

def bmp_entry(im: Image.Image) -> bytes:
    w, h = im.size
    header = struct.pack("<IiiHHIIiiII", 40, w, h * 2, 1, 32, 0, 0, 0, 0, 0, 0)
    rows = []
    for y in range(h - 1, -1, -1):  # bottom-up
        rows.append(b"".join(struct.pack("BBBB", b, g, r, a) for r, g, b, a in (im.getpixel((x, y)) for x in range(w))))
    mask_row = ((w + 31) // 32) * 4
    return header + b"".join(rows) + b"\0" * (mask_row * h)  # AND mask unused: alpha channel decides

def png_entry(im: Image.Image) -> bytes:
    buf = io.BytesIO(); im.save(buf, "PNG", optimize=True); return buf.getvalue()

entries = [(s, png_entry(render(s)) if s >= 256 else bmp_entry(render(s))) for s in SIZES]
out = bytearray(struct.pack("<HHH", 0, 1, len(entries)))
offset = 6 + 16 * len(entries)
for size, data in entries:
    dim = 0 if size >= 256 else size
    out += struct.pack("<BBBBHHII", dim, dim, 0, 0, 1, 32, len(data), offset)
    offset += len(data)
for _, data in entries:
    out += data
OUT.parent.mkdir(parents=True, exist_ok=True)
OUT.write_bytes(out)
print(f"{OUT.relative_to(ROOT)}: {len(out)} bytes, sizes {SIZES}")
