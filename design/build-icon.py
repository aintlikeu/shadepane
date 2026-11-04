"""Builds the app icon files from design/app-icon.svg.

- src/Shadepane.App/Assets/Shadepane.ico — exe, windows and installer icon. Each size is rendered from the SVG
  separately (rsvg-convert), not downscaled from 256 px; up to 64 px as 32-bit BMP (most compatible), 256 px as PNG.
- installer/msix/Assets/*.png — MSIX package logos (Microsoft Store) for every scale and target size
  (resources.pri in the package picks the right file).

Usage: python3 design/build-icon.py   (needs rsvg-convert and Pillow)
"""
import io, struct, subprocess
from pathlib import Path
from PIL import Image

ROOT = Path(__file__).resolve().parent.parent
SVG = ROOT / "design/app-icon.svg"
ICO = ROOT / "src/Shadepane.App/Assets/Shadepane.ico"
MSIX_ASSETS = ROOT / "installer/msix/Assets"
ICO_SIZES = [16, 20, 24, 32, 40, 48, 64, 256]
SCALES = {100: 1.0, 125: 1.25, 150: 1.5, 200: 2.0, 400: 4.0}


def render(size: int) -> Image.Image:
    png = subprocess.run(["rsvg-convert", "-w", str(size), "-h", str(size), str(SVG)], check=True, capture_output=True).stdout
    return Image.open(io.BytesIO(png)).convert("RGBA")


def tile(width: int, height: int, icon_fraction: float) -> Image.Image:
    """Icon centred on a transparent canvas: tiles keep a margin around the icon, as Windows design guidance asks."""
    size = round(min(width, height) * icon_fraction)
    canvas = Image.new("RGBA", (width, height), (0, 0, 0, 0))
    icon = render(size)
    canvas.paste(icon, ((width - size) // 2, (height - size) // 2), icon)
    return canvas


def save_png(im: Image.Image, name: str) -> None:
    im.save(MSIX_ASSETS / name, "PNG", optimize=True)


def build_ico() -> None:
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

    entries = [(s, png_entry(render(s)) if s >= 256 else bmp_entry(render(s))) for s in ICO_SIZES]
    out = bytearray(struct.pack("<HHH", 0, 1, len(entries)))
    offset = 6 + 16 * len(entries)
    for size, data in entries:
        dim = 0 if size >= 256 else size
        out += struct.pack("<BBBBHHII", dim, dim, 0, 0, 1, 32, len(data), offset)
        offset += len(data)
    for _, data in entries:
        out += data
    ICO.parent.mkdir(parents=True, exist_ok=True)
    ICO.write_bytes(out)
    print(f"{ICO.relative_to(ROOT)}: {len(out)} bytes, sizes {ICO_SIZES}")


def build_msix_assets() -> None:
    MSIX_ASSETS.mkdir(parents=True, exist_ok=True)
    for old in MSIX_ASSETS.glob("*.png"):
        old.unlink()
    count = 0
    for scale, k in SCALES.items():
        # Значок приложения (меню «Пуск», список приложений): сам круг почти во всю ширину.
        save_png(tile(round(44 * k), round(44 * k), 0.92), f"Square44x44Logo.scale-{scale}.png")
        # Плитки: значок с полями.
        save_png(tile(round(150 * k), round(150 * k), 0.62), f"Square150x150Logo.scale-{scale}.png")
        save_png(tile(round(310 * k), round(150 * k), 0.62), f"Wide310x150Logo.scale-{scale}.png")
        # Логотип пакета (установщик приложений, Store).
        save_png(tile(round(50 * k), round(50 * k), 0.92), f"StoreLogo.scale-{scale}.png")
        count += 4
    # Панель задач, Проводник, заголовок окна берут точный размер; unplated — без цветной подложки (Windows 10).
    for size in (16, 20, 24, 32, 40, 48, 64, 256):
        icon = render(size)
        save_png(icon, f"Square44x44Logo.targetsize-{size}.png")
        save_png(icon, f"Square44x44Logo.targetsize-{size}_altform-unplated.png")
        count += 2
    print(f"{MSIX_ASSETS.relative_to(ROOT)}: {count} PNG files")


if __name__ == "__main__":
    build_ico()
    build_msix_assets()
