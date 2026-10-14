"""Project rules for the exported assets and settings: each *_overlay function returns
{path in the project: new content} for pipeline.assemble, except name_overlay and
addressables_overlay, which also return the exported paths they move."""

import re
import struct
import sys
from pathlib import Path

from common import NATIVE_ASSETS, folder_meta, game_data, guid_for, log, meta_guid, meta_of


def current_text(exported: Path, overlay: dict, relative: Path):
    """A file's text as the rules before left it: its overlay entry, else the export's file."""
    if relative in overlay:
        return overlay[relative].decode('utf-8')
    return (exported / relative).read_text(encoding='utf-8')


def script_file_id(namespace: str, name: str):
    """The fileID of a script class in a managed plugin: the first four bytes of
    MD4("s\\0\\0\\0" + namespace + name), little-endian."""
    from Crypto.Hash import MD4

    return struct.unpack('<i', MD4.new(('s\0\0\0' + namespace + name).encode()).digest()[:4])[0]


# CrowCompat's GUID in the project (pipeline.assembly_overlay writes its .meta with it).
COMPAT_GUID = guid_for('plugin', 'CrowCompat')

# Engine components a platform's player lacks, which become a CrowCompat script of the same API.
# Loading a component of a class the player lacks leaves a hole in its GameObject's component list,
# and activating the GameObject crashes the player (PS Vita, The Drifter's port). The PS Vita's
# 2018.2 player has no VideoPlayer (the health monitor in the status screen, Pool, the ending).
PLATFORM_COMPAT_COMPONENTS = {'vita': {328: 'UnityEngine_Video_VideoPlayer'}}
# A VideoPlayer's fields that CrowCompat's keeps, under the engine's names. The clip is not kept:
# the PS Vita player has no VideoClip either.
VIDEO_FIELDS = ('m_TargetTexture', 'm_Looping', 'm_PlayOnAwake', 'm_PlaybackSpeed')
COMPONENT_HEADER = ('m_ObjectHideFlags', 'm_CorrespondingSourceObject', 'm_PrefabInternal', 'm_GameObject', 'm_Enabled')


def compat_component_overlay(exported: Path, overlay: dict, platform: str):
    classes = PLATFORM_COMPAT_COMPONENTS.get(platform, {})
    if not classes:
        return {}
    document = re.compile(
        r'^--- !u!(' + '|'.join(map(str, classes)) + r') &(-?\d+)([^\n]*)\n(?:(?!--- !u!).*\n?)*', re.M
    )
    result, replaced = {}, 0
    for suffix in ('*.unity', '*.prefab'):
        for path in (exported / 'Assets').rglob(suffix):
            relative = path.relative_to(exported)
            text = current_text(exported, overlay, relative)
            if not re.search(r'^--- !u!(' + '|'.join(map(str, classes)) + r') ', text, re.M):
                continue

            def replace(m):
                fields = dict(re.findall(r'^  (\w+): (.*)$', m[0], re.M))
                lines = [f'--- !u!114 &{m[2]}{m[3]}', 'MonoBehaviour:']
                lines += [f'  {key}: {fields[key]}' for key in COMPONENT_HEADER if key in fields]
                script = script_file_id('CrowCompat', classes[int(m[1])])
                lines += [
                    '  m_EditorHideFlags: 0',
                    f'  m_Script: {{fileID: {script}, guid: {COMPAT_GUID}, type: 3}}',
                    '  m_Name: ',
                    '  m_EditorClassIdentifier: ',
                ]
                lines += [f'  {key}: {fields[key]}' for key in VIDEO_FIELDS if key in fields]
                return '\n'.join(lines) + '\n'

            text, count = document.subn(replace, text)
            replaced += count
            result[relative] = text.encode('utf-8')
    log(f'Replaced {replaced} components the {platform} player lacks with CrowCompat scripts')
    return result


# Assets that reference objects in a scene. I2 Localization keeps each Localize component's target
# (LocalizeTarget_*, a ScriptableObject) in the scene with it, and the target references the text it
# localizes; AssetRipper exports the targets as assets of their own. Unity cannot load an asset that
# references a scene object: importing the first one logged "Do not use ReadObjectThreaded on scene
# objects!" without end (11.8 GB of log in 30 minutes). The references become null. I2 then finds
# the target again: LocalizeTarget.IsValid takes the Localize component's own component of the
# target's type, which is where the original reference pointed (70 targets, all on the same object).
SCENE_REFERENCE_FOLDERS_SKIPPED = {'Mesh'}  # meshes reference nothing


def scene_reference_overlay(exported: Path, overlay: dict):
    assets = exported / 'Assets'
    scenes = {meta_guid(meta) for meta in assets.rglob('*.unity.meta')}
    reference = re.compile(r'\{fileID: -?\d+, guid: (' + '|'.join(sorted(scenes)) + r'), type: 2\}')
    keys = [guid.encode() for guid in scenes]
    result, cut = {}, 0
    for path in assets.rglob('*'):
        relative = path.relative_to(exported)
        if path.suffix not in NATIVE_ASSETS or relative.parts[1] in SCENE_REFERENCE_FOLDERS_SKIPPED:
            continue
        data = overlay.get(relative) or path.read_bytes()
        if not any(key in data for key in keys):
            continue
        text, count = reference.subn('{fileID: 0}', data.decode('utf-8'))
        if count:
            result[relative] = text.encode('utf-8')
            cut += count
    log(f'Cut {cut} references from {len(result)} assets to scene objects')
    return result


# IL2CPP strips the class libraries down to what code references. PlayMaker finds its actions
# and the game's scripts by name, so the game's assemblies and plugins are kept whole.
LINK_PRESERVE = {}


def link_xml(assemblies: list, platform: str):
    lines = ['<linker>']
    lines += [f'  <assembly fullname="{dll.stem}" preserve="all"/>' for dll in sorted(assemblies)]
    for assembly, types in LINK_PRESERVE.items():
        lines.append(f'  <assembly fullname="{assembly}">')
        lines += [f'    <type fullname="{t}" preserve="all"/>' for t in types]
        lines.append('  </assembly>')
    return '\n'.join([*lines, '</linker>', ''])


# GraphicsFormat (2019.3+) -> (RenderTextureFormat, sRGB) for the 2018 render texture importer.
RENDER_TEXTURE_FORMATS = {
    4: (0, 1),  # R8G8B8A8_SRGB -> ARGB32
    8: (0, 0),  # R8G8B8A8_UNorm -> ARGB32
    48: (2, 0),  # R16G16B16A16_SFloat -> ARGBHalf
    52: (11, 0),  # R32G32B32A32_SFloat -> ARGBFloat
}


def render_texture_overlay(exported: Path):
    overlay = {}
    for path in (exported / 'Assets').rglob('*.renderTexture'):
        text = path.read_text(encoding='utf-8')
        graphics = int(re.search(r'^  m_ColorFormat: (\d+)$', text, re.M).group(1))
        if graphics not in RENDER_TEXTURE_FORMATS:
            sys.exit(f'{path.name}: no RenderTextureFormat rule for GraphicsFormat {graphics}')
        legacy, srgb = RENDER_TEXTURE_FORMATS[graphics]
        text = re.sub(r'^  m_ColorFormat: \d+$', f'  m_ColorFormat: {legacy}', text, flags=re.M)
        text = re.sub(r'^  m_SRGB: \d+$', f'  m_SRGB: {srgb}', text, flags=re.M)
        overlay[path.relative_to(exported)] = text.encode('utf-8')
    log(f'Converted the color format of {len(overlay)} render textures')
    return overlay


# Per platform: the source's AudioClip load type -> (load type, compression format, quality or None
# to keep it). On the PS Vita: decompress-on-load and compressed-in-memory clips become compressed in
# memory as HEVAG (3, its ADPCM), streamed ones stream HEVAG; long clips stream (STREAM_SECONDS).
PLATFORM_AUDIO = {'vita': {0: (1, 5, None), 1: (1, 3, 0.5), 2: (2, 3, 0.7)}}
# A clip kept in memory is read whole, on the main thread, when it first plays; long loops stream.
STREAM_SECONDS = 45


def audio_seconds(path: Path):
    """Length of an exported Ogg Vorbis or WAV clip in seconds, or None."""
    data = path.read_bytes()
    if path.suffix == '.ogg':
        header = data.find(b'\x01vorbis')
        last = data.rfind(b'OggS')
        if header < 0 or last < 0:
            return None
        rate = int.from_bytes(data[header + 12 : header + 16], 'little')
        granule = int.from_bytes(data[last + 6 : last + 14], 'little')
        return granule / rate if rate else None
    if path.suffix == '.wav':
        fmt, chunk = data.find(b'fmt '), data.find(b'data')
        if fmt < 0 or chunk < 0:
            return None
        byte_rate = int.from_bytes(data[fmt + 16 : fmt + 20], 'little')
        return int.from_bytes(data[chunk + 4 : chunk + 8], 'little') / byte_rate if byte_rate else None
    return None


def audio_overlay(exported: Path, platform: str):
    formats = PLATFORM_AUDIO.get(platform)
    if not formats:
        return {}
    overlay, streamed = {}, []
    settings = re.compile(
        r'^(  defaultSettings:\n    loadType: )(\d)(\n(?:    .*\n)*?    compressionFormat: )(\d+)'
        r'(\n    quality: )([\d.]+)',
        re.M,
    )
    for meta in (exported / 'Assets').rglob('*.meta'):
        if meta.suffixes[-2:-1] not in (['.ogg'], ['.wav'], ['.mp3']):
            continue
        text = original = meta.read_text(encoding='utf-8')

        def convert(m, meta=meta):
            load_type, compression, quality = formats[int(m[2])]
            if load_type == 1:
                seconds = audio_seconds(meta.with_suffix(''))
                if seconds is not None and seconds >= STREAM_SECONDS:
                    load_type, compression, quality = formats[2]
                    streamed.append(meta.with_suffix('').stem)
            return m[1] + str(load_type) + m[3] + str(compression) + m[5] + (m[6] if quality is None else str(quality))

        text = settings.sub(convert, text, count=1)
        text = re.sub(r'^  preloadAudioData: 1$', '  preloadAudioData: 0', text, flags=re.M)
        if text != original:
            overlay[meta.relative_to(exported)] = text.encode('utf-8')
    log(f'{platform} audio import changed for {len(overlay)} clips; {len(streamed)} long clips stream')
    return overlay


# TextureFormat values that are block-compressed.
COMPRESSED_TEXTURE_FORMATS = {10, 12, 24, 25, 26, 27, 28, 29, 34, 45, 46, 47, 48, 49, 50}
# Per platform: (build target, TextureImporterFormat with alpha, without). The PS Vita has 512 MB
# shared by the CPU and GPU; its GPU samples DXT natively.
PLATFORM_TEXTURE_FORMATS = {'vita': ('PSP2', 12, 10)}
TEXTURE_PLATFORM_ENTRY = """  - serializedVersion: 2
    buildTarget: {target}
    maxTextureSize: {size}
    resizeAlgorithm: 0
    textureFormat: {format}
    textureCompression: 1
    compressionQuality: 100
    crunchedCompression: 0
    allowsAlphaSplitting: 0
    overridden: 1
    androidETC2FallbackOverride: 0
"""


def fully_opaque(image: Path):
    from PIL import Image

    with Image.open(image) as picture:
        if 'A' not in picture.getbands() and 'transparency' not in picture.info:
            return True
        return picture.convert('RGBA').getchannel('A').getextrema()[0] == 255


def max_texture_size(largest):
    """The smallest power of two from 2048 up that holds a texture's largest side, so the import
    never downscales it."""
    return max(2048, 1 << (largest - 1).bit_length())


def texture_overlay(exported: Path, game: Path, platform: str):
    """Import textures the way the game shipped them: AssetRipper writes high-quality compression
    into every texture importer, so Unity would block-compress textures the game stores
    uncompressed (its pixel art, UI). Uncompressed unless the original was compressed, with a max
    size that never downscales; the PS Vita compresses those to DXT, by the image's own alpha."""
    import UnityPy

    data = game_data(game)
    compressed, largest = set(), {}
    for path in sorted(data.glob('*.assets')):
        for obj in UnityPy.load(str(path)).objects:
            if obj.type.name != 'Texture2D':
                continue
            tree = obj.read_typetree()
            if tree['m_TextureFormat'] in COMPRESSED_TEXTURE_FORMATS:
                compressed.add(tree['m_Name'])
            largest[tree['m_Name']] = max(largest.get(tree['m_Name'], 0), tree['m_Width'], tree['m_Height'])
    overlay, kept = {}, 0
    for meta in (exported / 'Assets').rglob('*.png.meta'):
        text = meta.read_text(encoding='utf-8')
        if 'TextureImporter:' not in text:
            continue
        stem = meta.name[: -len('.png.meta')]
        name = stem if stem in largest else re.sub(r'_\d+$', '', stem)
        if name in compressed:
            kept += 1
            continue
        size = max_texture_size(largest.get(name, 0))
        text = re.sub(r'^(\s*)textureCompression: \d+$', r'\g<1>textureCompression: 0', text, flags=re.M)
        text = re.sub(r'^(\s*)maxTextureSize: \d+$', rf'\g<1>maxTextureSize: {size}', text, flags=re.M)
        if platform in PLATFORM_TEXTURE_FORMATS:
            target, with_alpha, without_alpha = PLATFORM_TEXTURE_FORMATS[platform]
            text = text.replace(
                '  platformSettings:\n',
                '  platformSettings:\n'
                + TEXTURE_PLATFORM_ENTRY.format(
                    target=target, size=size, format=without_alpha if fully_opaque(meta.with_suffix('')) else with_alpha
                ),
                1,
            )
        overlay[meta.relative_to(exported)] = text.encode('utf-8')
    log(f'Uncompressed import for {len(overlay)} textures; {kept} stay compressed like the original')
    return overlay


IMPORTED_BINARY = {'.png', '.ogg', '.wav', '.mp3', '.ttf', '.otf', '.mp4', '.webm', '.txt', '.bytes', '.json'}
# A native asset's m_Name is in its first lines; reading this much of each file finds it.
NAME_SEARCH_BYTES = 4000


def name_overlay(exported: Path, game: Path, overlay: dict):
    """Restore original object names that AssetRipper had to change for unique file names.

    Unity names an asset's main object after its file, so AssetRipper's "Name_1.anim" becomes an
    object called "Name_1" and the game's lookups by name fail. Move each such asset to
    "<stem>/<original name><ext>" with its .meta, so the GUID is kept. Resources paths are left
    alone; they are runtime load paths. Returns (moved overlay entries, excluded original paths);
    overlay entries already generated for a moved path are moved with it."""
    import UnityPy

    data = game_data(game)
    original = set()
    # A build keeps these assets in resources.assets and sharedassetsN.assets, never in a level.
    for path in sorted(data.glob('*.assets')):
        for obj in UnityPy.load(str(path)).objects:
            if obj.type.name in ('Texture2D', 'AudioClip', 'Font', 'VideoClip', 'TextAsset'):
                original.add(obj.peek_name())
    moved, excluded, skipped = {}, set(), 0
    assets = exported / 'Assets'
    for path in assets.rglob('*'):
        if path.suffix not in NATIVE_ASSETS | IMPORTED_BINARY or 'Resources' in path.relative_to(assets).parts:
            continue
        name = None
        if path.suffix in NATIVE_ASSETS:
            with path.open(encoding='utf-8', errors='replace') as handle:
                match = re.search(r'^  m_Name: (.*)$', handle.read(NAME_SEARCH_BYTES), re.M)
            if match and match.group(1) != path.stem:
                name = match.group(1)
        elif path.stem not in original:
            match = re.match(r'^(.*)_\d+$', path.stem)
            if match and match.group(1) in original:
                name = match.group(1)
        if name is None:
            continue
        if not name or re.search(r'[<>:"/\\|?*]', name) or name.rstrip('. ') != name:
            skipped += 1
            continue
        relative = path.relative_to(exported)
        meta = meta_of(relative)
        target = relative.parent / path.stem / (name + path.suffix)
        moved[target] = overlay.pop(relative, None) or path.read_bytes()
        moved[meta_of(target)] = overlay.pop(meta, None) or (exported / meta).read_bytes()
        moved.update(folder_meta(target.parent))
        excluded.update({relative, meta})
    log(
        f'Restored original names for {len(excluded) // 2} assets'
        + (f'; {skipped} names cannot be file names' if skipped else '')
    )
    return moved, excluded


# Assets the game loads through Addressables by key (its catalog, StreamingAssets/aa): AssetRipper
# exports the bundle's assets at the top of Assets. CrowCompat's Addressables loads a key from
# Resources/Addressables/<key>.
ADDRESSABLE_ASSETS = {'CharacterCameraPrefab': Path('Assets') / 'CharacterCameraPrefab.prefab'}
ADDRESSABLES_FOLDER = Path('Assets') / 'Resources' / 'Addressables'


def addressables_overlay(exported: Path, overlay: dict):
    """Moves the assets in ADDRESSABLE_ASSETS (with their .meta, so references keep their GUID).
    Returns (moved overlay entries, excluded original paths)."""
    moved, excluded = folder_meta(ADDRESSABLES_FOLDER), set()
    for key, relative in ADDRESSABLE_ASSETS.items():
        if not (exported / relative).exists():
            sys.exit(f'No {relative} in the export for the Addressables key {key}')
        target = ADDRESSABLES_FOLDER / (key + relative.suffix)
        moved[target] = overlay.pop(relative, None) or (exported / relative).read_bytes()
        moved[meta_of(target)] = overlay.pop(meta_of(relative), None) or (exported / meta_of(relative)).read_bytes()
        excluded.update({relative, meta_of(relative)})
    # The catalog and the Windows bundles (70 MB), which nothing reads now.
    catalog = Path('Assets') / 'StreamingAssets' / 'aa'
    if (exported / catalog).exists():
        excluded.update(p.relative_to(exported) for p in (exported / catalog).rglob('*'))
        excluded.update({catalog, meta_of(catalog)})
    log(f'Moved {len(ADDRESSABLE_ASSETS)} Addressables assets to {ADDRESSABLES_FOLDER.as_posix()}; left out {catalog}')
    return moved, excluded


# Player settings whose 2018 defaults differ from the source version's behaviour.
SETTING_RULES = {
    # Unity 2019.3 removed the launch-time resolution dialog; 2018 shows it unless disabled.
    'displayResolutionDialog': '0',
    # The game ships without a player log (boot.config nolog); the port's test tools read it.
    'usePlayerLog': '1',
}
# Per platform. The PS Vita player starts in power mode A, with the GPU at 111 MHz; mode C (2)
# raises the GPU clock and gives up the camera and full screen brightness (The Drifter's port).
PLATFORM_SETTING_RULES = {'vita': {'psp2PowerMode': '2'}}


def settings_overlay(exported: Path, platform: str):
    relative = Path('ProjectSettings') / 'ProjectSettings.asset'
    text = (exported / relative).read_text(encoding='utf-8')
    for key, value in {**SETTING_RULES, **PLATFORM_SETTING_RULES.get(platform, {})}.items():
        text, count = re.subn(rf'^(  {key}:).*$', rf'\g<1> {value}', text, flags=re.M)
        if count == 0:
            # Keys the source version no longer has; the file holds one PlayerSettings mapping.
            text = text.rstrip('\n') + f'\n  {key}: {value}\n'
        elif count > 1:
            sys.exit(f'ProjectSettings has {count} entries for {key}')
    return {relative: text.encode('utf-8')}


NATIVE_PLUGIN_META = """fileFormatVersion: 2
guid: {guid}
PluginImporter:
  serializedVersion: 2
  iconMap: {{}}
  executionOrder: {{}}
  isPreloaded: 0
  isOverridable: 0
  platformData:
  - first:
      Any:
    second:
      enabled: 0
      settings: {{}}
  - first:
      Editor: Editor
    second:
      enabled: 1
      settings:
        CPU: x86_64
        OS: Windows
  - first:
      Standalone: Win64
    second:
      enabled: 1
      settings:
        CPU: AnyCPU
  userData:
  assetBundleName:
  assetBundleVariant:
"""


def native_plugin_overlay(game: Path):
    """AssetRipper exports managed code only; carry the game's 64-bit native plugins over (Steam,
    GOG Galaxy, Rewired's DirectInput, AVPro's capture) for Windows players."""
    source = game_data(game) / 'Plugins' / 'x86_64'
    folder = Path('Assets') / 'Plugins' / 'x86_64'
    overlay = folder_meta(folder)
    for dll in sorted(source.glob('*.dll')):
        overlay[folder / dll.name] = dll.read_bytes()
        overlay[meta_of(folder / dll.name)] = NATIVE_PLUGIN_META.format(guid=guid_for('native', dll.name)).encode()
    log('Native plugins: ' + ', '.join(p.name for p in overlay if p.suffix == '.dll'))
    return overlay
