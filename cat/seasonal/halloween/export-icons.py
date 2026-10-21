"""Rebuild the Halloween icon exports from the approved transparent master (Pillow)."""

from pathlib import Path
from PIL import Image

root = Path(__file__).resolve().parent
sizes = (16, 20, 24, 32, 40, 48, 64, 96, 128, 256)
with Image.open(root / "master.png") as source:
    master = source.convert("RGBA")
    for size in sizes:
        master.resize((size, size), Image.Resampling.LANCZOS).save(root / f"halloween_{size}.png")
    master.save(root / "halloween.ico", sizes=[(size, size) for size in sizes], bitmap_format="png")

target = root.parents[2] / "LogReader" / "LogReader.App" / "Assets" / "Seasonal" / "halloween.ico"
target.parent.mkdir(parents=True, exist_ok=True)
target.write_bytes((root / "halloween.ico").read_bytes())
