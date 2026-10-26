"""Rebuild Thanksgiving, Christmas and New Year icon exports with Pillow."""

from pathlib import Path
from PIL import Image

root = Path(__file__).resolve().parent
sizes = (16, 20, 24, 32, 40, 48, 64, 96, 128, 256)
for season in ("thanksgiving", "christmas", "new-year"):
    folder = root / season
    with Image.open(folder / "master.png") as source:
        master = source.convert("RGBA")
        for size in sizes:
            master.resize((size, size), Image.Resampling.LANCZOS).save(folder / f"{season}_{size}.png")
        master.save(folder / f"{season}.ico", sizes=[(size, size) for size in sizes], bitmap_format="png")
