"""Makes the program icon and the installer wizard images from app-icon.png.

Run from the repository root after changing app-icon.png (needs Python with Pillow):
    python assets/make_icons.py
The outputs are committed, so a normal build does not need Python.
"""
from PIL import Image

SOURCE = 'assets/app-icon.png'
ICON = 'src/BeepTone.ico'
BANNER = 'installer/WixUIBanner.bmp'   # top strip of each wizard page, 493 x 58
DIALOG = 'installer/WixUIDialog.bmp'   # Welcome and Finish pages, 493 x 312
SIZES = [16, 20, 24, 32, 40, 48, 64, 96, 128, 256]
PANEL = (255, 243, 230)                # warm cream behind the dog on the Welcome page

art = Image.open(SOURCE).convert('RGBA')
side = max(art.size)
square = Image.new('RGBA', (side, side), (0, 0, 0, 0))
square.alpha_composite(art, ((side - art.width) // 2, (side - art.height) // 2))

# Each size is resized from the full image on its own, so small sizes stay sharp.
images = [square.resize((s, s), Image.LANCZOS) for s in SIZES]
images[-1].save(ICON, format='ICO', sizes=[(s, s) for s in SIZES], append_images=images[:-1])

banner = Image.new('RGBA', (493, 58), (255, 255, 255, 255))
banner.alpha_composite(square.resize((52, 52), Image.LANCZOS), (493 - 52 - 14, 3))
banner.convert('RGB').save(BANNER, format='BMP')

dialog = Image.new('RGBA', (493, 312), (255, 255, 255, 255))
dialog.paste(Image.new('RGBA', (164, 312), PANEL + (255,)), (0, 0))
dialog.alpha_composite(square.resize((144, 144), Image.LANCZOS), (10, 150))
dialog.convert('RGB').save(DIALOG, format='BMP')

print('wrote', ICON, BANNER, DIALOG)
