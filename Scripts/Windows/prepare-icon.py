#!/usr/bin/env python3
"""Resize the imagegen master into Windows package assets; preserve its alpha.

Requires Pillow. This only resamples/encodes the approved raster artwork.
Run before stage-from-wsl.py when preparing a new icon.
"""
from pathlib import Path
from PIL import Image, ImageOps

root = Path(__file__).resolve().parents[2]
source = root / "Resources/WindowsIcon/SyncBarIcon.png"
assets = root / "Windows/CodexSyncBar.Windows/Assets"
master = Image.open(source).convert("RGBA")
assert master.getchannel("A").getextrema()[0] == 0, "The master must retain transparent corners."

def save_png(name, size, mark_size=None):
    canvas = Image.new("RGBA", size, (0, 0, 0, 0))
    mark = ImageOps.contain(master, mark_size or size, Image.Resampling.LANCZOS)
    canvas.alpha_composite(mark, ((size[0] - mark.width) // 2, (size[1] - mark.height) // 2))
    canvas.save(assets / name, optimize=True)

for name, size in {
    "SyncBarIcon.png": (64, 64),
    "StoreLogo.png": (50, 50),
    "Square44x44Logo.scale-200.png": (88, 88),
    "Square44x44Logo.targetsize-24_altform-unplated.png": (24, 24),
    "Square44x44Logo.targetsize-48_altform-lightunplated.png": (48, 48),
    "Square150x150Logo.scale-200.png": (300, 300),
    "LockScreenLogo.scale-200.png": (48, 48),
}.items():
    save_png(name, size)
save_png("Wide310x150Logo.scale-200.png", (620, 300), (240, 240))
save_png("SplashScreen.scale-200.png", (1240, 600), (320, 320))
master.save(assets / "AppIcon.ico", format="ICO",
            sizes=[(s, s) for s in (16, 20, 24, 32, 40, 48, 64, 128, 256)])
print(f"Windows icon assets generated from {source.name}; original {master.width}x{master.height}, alpha preserved.")
