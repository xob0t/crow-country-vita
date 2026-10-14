"""PS Vita LiveArea and splash artwork, composed from Crow Country's Steam store art.

    uv run tools/downgrade/livearea.py [--force]

The images come from Steam's public CDN (no Steam client, no game launch), downloaded once into
work/livearea/store/ and read from there afterwards. Writes to work/livearea/:

  icon0.png     128x128   bubble: the crow on the ticket booth (vertical key art)
  bg0.png       840x500   LiveArea background: the ticket booth scene (library hero art)
  gate.png      280x158   LiveArea start gate: the wide key art with its logo
  pic0.png      960x544   shown while the app starts: the wide key art with its logo
  splash.png    960x544   Unity's PS Vita splash image: pic0 in full colour

The LiveArea images and pic0 are 8-bit indexed: the installer rejects a truecolor pic0 (promote
error 0x8010113D), and icon0 must not have an alpha channel (The Drifter's port found both). The
outputs are remade when the cached sources or this script change. pipeline.py puts them into the
VPK (vita_app_files).
"""

import argparse
import hashlib
import urllib.request
from pathlib import Path

from common import WORK, log
from PIL import Image

OUT = WORK / 'livearea'
STORE = OUT / 'store'
FILES = ['icon0.png', 'bg0.png', 'gate.png', 'pic0.png', 'splash.png']
PALETTE_COLORS = 256  # 8-bit indexed

STEAM_APP_ID = '1996010'
CDN = f'https://cdn.akamai.steamstatic.com/steam/apps/{STEAM_APP_ID}/'
# Cached name: (URL, SHA-256 of the file the crops below were made for). A changed file is only
# reported: Valve can replace store art at the same URL.
SOURCES = {
    # Wide key art, logo across the top, 616x353.
    'capsule.jpg': (CDN + 'capsule_616x353.jpg', '7aa5b45abc4dda4565ef05a0095aa935fa14136bf5c732b9b07e3a18b89fd315'),
    # Vertical key art, 600x900: the logo above the ticket booth, the crow's face at its middle.
    'library.jpg': (CDN + 'library_600x900_2x.jpg', 'ac3b255c0470fb08b1191f7e8da06534a24f20a9d91778387a23ccb7c6165d6c'),
    # Library hero art, 1920x620: the ticket booth scene without the logo.
    'hero.jpg': (CDN + 'library_hero.jpg', '9669615f6cc728bf238b7a6b3257918a3a09b5b957306e2c402025ee2d2e1877'),
}
ICON_CROP = (225, 360, 385, 520)  # the booth crow's face in library.jpg


def fetch():
    """The cached store images, downloading the missing ones."""
    STORE.mkdir(parents=True, exist_ok=True)
    paths = {}
    for name, (url, digest) in SOURCES.items():
        path = STORE / name
        if not path.exists():
            log(f'Downloading {url}')
            request = urllib.request.Request(url, headers={'User-Agent': 'crow-country-vita-livearea'})
            try:
                with urllib.request.urlopen(request, timeout=60) as response:
                    data = response.read()
            except OSError as error:
                raise RuntimeError(f'cannot download {url} ({error}); the LiveArea art needs it once') from error
            path.with_suffix('.part').write_bytes(data)
            path.with_suffix('.part').replace(path)
        actual = hashlib.sha256(path.read_bytes()).hexdigest()
        if actual != digest:
            log(f'warning: {path.name} differs from the store image the crops were made for; check work/livearea/')
        paths[name] = path
    return paths


def cover(image: Image.Image, size, box=None):
    """image (or its box) scaled to fill size, centred, the overflow cut off."""
    if box:
        image = image.crop(box)
    width, height = size
    scale = max(width / image.width, height / image.height)
    scaled = image.resize((round(image.width * scale), round(image.height * scale)), Image.LANCZOS)
    left, top = (scaled.width - width) // 2, (scaled.height - height) // 2
    return scaled.crop((left, top, left + width, top + height))


def indexed(image: Image.Image):
    return image.convert('RGB').quantize(PALETTE_COLORS, method=Image.Quantize.MEDIANCUT, dither=Image.Dither.NONE)


def compose(sources):
    capsule = Image.open(sources['capsule.jpg']).convert('RGB')
    library = Image.open(sources['library.jpg']).convert('RGB')
    hero = Image.open(sources['hero.jpg']).convert('RGB')
    pic0 = cover(capsule, (960, 544))
    return {
        'icon0.png': indexed(cover(library, (128, 128), ICON_CROP)),
        'bg0.png': indexed(cover(hero, (840, 500))),
        'gate.png': indexed(cover(capsule, (280, 158))),
        'pic0.png': indexed(pic0),
        'splash.png': pic0,
    }


def stamp(sources):
    digest = hashlib.sha256(Path(__file__).read_bytes())
    for path in sources.values():
        digest.update(path.read_bytes())
    return digest.hexdigest()


def generate(force=False):
    """The artwork folder, made if missing or out of date."""
    sources = fetch()
    marker = OUT / 'stamp.txt'
    current = stamp(sources)
    if not force and marker.exists() and marker.read_text() == current and all((OUT / f).exists() for f in FILES):
        return OUT
    for name, image in compose(sources).items():
        image.save(OUT / name, optimize=True)
    marker.write_text(current)
    log(f'LiveArea art in {OUT}')
    return OUT


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument('--force', action='store_true')
    generate(parser.parse_args().force)
