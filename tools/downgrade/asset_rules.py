"""Project rules for the exported assets and settings: each *_overlay function returns
{path in the project: new content} for pipeline.assemble, except name_overlay and
addressables_overlay, which also return the exported paths they move."""

import hashlib
import re
import struct
import sys
from pathlib import Path

from common import NATIVE_ASSETS, WORK, folder_meta, game_data, guid_for, listing, log, meta_guid, meta_of, scan


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
# CrowCompat's plays the videos as frame atlases (video_rules.py, compat/Video.cs); the Windows
# players use it too, so they show what the PS Vita does.
PLATFORM_COMPAT_COMPONENTS = {
    'vita': {328: 'UnityEngine_Video_VideoPlayer'},
    'windows': {328: 'UnityEngine_Video_VideoPlayer'},
}
# A VideoPlayer's fields that CrowCompat's keeps, under the engine's names. The clip references
# become video_rules' clip assets.
VIDEO_FIELDS = (
    'm_VideoClip',
    'm_TargetTexture',
    'm_RenderMode',
    'm_Looping',
    'm_PlayOnAwake',
    'm_PlaybackSpeed',
    'm_AudioOutputMode',
    'm_TargetAudioSources',
    'm_DirectAudioVolumes',
)
COMPONENT_HEADER = ('m_ObjectHideFlags', 'm_CorrespondingSourceObject', 'm_PrefabInternal', 'm_GameObject', 'm_Enabled')
COMPONENT_FIELD = re.compile(r'^  (\w+):(.*)\n((?:  - .*\n)*)', re.M)


def compat_component_overlay(exported: Path, overlay: dict, platform: str):
    classes = PLATFORM_COMPAT_COMPONENTS.get(platform, {})
    if not classes:
        return {}
    document = re.compile(
        r'^--- !u!(' + '|'.join(map(str, classes)) + r') &(-?\d+)([^\n]*)\n(?:(?!--- !u!).*\n?)*', re.M
    )
    header = re.compile(rb'^--- !u!(' + '|'.join(map(str, classes)).encode() + rb') ', re.M)
    # Which scenes and prefabs have such a component, read once per file version (scan): the scenes
    # are up to 35 MB each.
    has_component = scan(
        f'compat-components-{platform}',
        exported / 'Assets',
        lambda relative: relative.suffix in ('.unity', '.prefab'),
        lambda path: header.search(path.read_bytes()) is not None,
        version=header.pattern.decode(),
    )
    result, replaced = {}, 0
    for found, has in has_component.items():
        relative = Path('Assets') / found
        if relative in overlay:
            has = header.search(overlay[relative]) is not None
        if has:
            text = current_text(exported, overlay, relative)

            def replace(m):
                # A field's value: the rest of its line, and the items of a list on the lines below.
                fields = {
                    key: value + ('\n' + items.rstrip('\n') if items else '')
                    for key, value, items in COMPONENT_FIELD.findall(m[0])
                }
                lines = [f'--- !u!114 &{m[2]}{m[3]}', 'MonoBehaviour:']
                lines += [f'  {key}:{fields[key]}' for key in COMPONENT_HEADER if key in fields]
                script = script_file_id('CrowCompat', classes[int(m[1])])
                lines += [
                    '  m_EditorHideFlags: 0',
                    f'  m_Script: {{fileID: {script}, guid: {COMPAT_GUID}, type: 3}}',
                    '  m_Name: ',
                    '  m_EditorClassIdentifier: ',
                ]
                lines += [f'  {key}:{fields[key]}' for key in VIDEO_FIELDS if key in fields]
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
    scenes = {meta_guid(assets / meta) for meta in listing(assets) if meta.name.endswith('.unity.meta')}
    reference = re.compile(r'\{fileID: -?\d+, guid: (' + '|'.join(sorted(scenes)) + r'), type: 2\}')
    keys = [guid.encode() for guid in sorted(scenes)]
    # Which assets name a scene, read once per file version (scan).
    names_scene = scan(
        'scene-references',
        assets,
        lambda relative: relative.suffix in NATIVE_ASSETS and relative.parts[0] not in SCENE_REFERENCE_FOLDERS_SKIPPED,
        lambda path: any(key in path.read_bytes() for key in keys),
        version=' '.join(sorted(scenes)),
    )
    result, cut = {}, 0
    for found, named in names_scene.items():
        relative = Path('Assets') / found
        if relative in overlay:
            data = overlay[relative]
            named = any(key in data for key in keys)
        elif named:
            data = (exported / relative).read_bytes()
        if not named:
            continue
        text, count = reference.subn('{fileID: 0}', data.decode('utf-8'))
        if count:
            result[relative] = text.encode('utf-8')
            cut += count
    log(f'Cut {cut} references from {len(result)} assets to scene objects')
    return result


# A canvas gives its shaders the vertex streams its additional shader channels name. SFB's canvases
# pass uv1 (with normals and tangents, 25), where UIPixelate's grid is; on 2018.2 the grid's size goes
# in uv2 (compat/ApiShims.cs, VertexHelper.SetUV1), so each canvas that passes uv1 passes uv2 too.
CANVAS_TEXCOORD1, CANVAS_TEXCOORD2 = 1, 2
CANVAS_CHANNELS = re.compile(r'^(  m_AdditionalShaderChannelsFlag: )(\d+)$', re.M)


def canvas_channel_overlay(exported: Path, overlay: dict):
    has_canvas = scan(
        'canvas-channels',
        exported / 'Assets',
        lambda relative: relative.suffix in ('.unity', '.prefab'),
        lambda path: b'm_AdditionalShaderChannelsFlag' in path.read_bytes(),
    )
    result, changed = {}, 0

    def add_texcoord2(m):
        nonlocal changed
        flags = int(m[2])
        if not flags & CANVAS_TEXCOORD1 or flags & CANVAS_TEXCOORD2:
            return m[0]
        changed += 1
        return f'{m[1]}{flags | CANVAS_TEXCOORD2}'

    for found, has in has_canvas.items():
        relative = Path('Assets') / found
        if relative in overlay:
            has = b'm_AdditionalShaderChannelsFlag' in overlay[relative]
        if has:
            text = current_text(exported, overlay, relative)
            new = CANVAS_CHANNELS.sub(add_texcoord2, text)
            if new != text:
                result[relative] = new.encode('utf-8')
    log(f'Canvases passing uv2 for UIPixelate: {changed} in {len(result)} files')
    return result


# Rewired's PS Vita controller has one pair of shoulder buttons, which its Dual Analog Gamepad template
# file maps to Left and Right Shoulder 2. The game's joystick maps are template maps, and its default
# (Classic) layout has aiming (Combat Raise) and the menus' page buttons (L Button, R Button) on
# shoulder 1, so on the console nothing pressed them. Rewired prefers a controller's own map to the
# template's: on the PS Vita, each template map gets a copy for the PS Vita controller, with shoulder
# 1's actions on L and R, and of shoulder 2's only the combat ones (Modern's aim and shoot), which
# replace shoulder 1's (Modern's reload). Shoulder 2's other actions are R2 Button, a second run
# button (Circle runs), and L2 Button, which nothing reads.
REWIRED_TEMPLATE = '83b427e4-086f-47f3-bb06-be266abd1ca5'  # Dual Analog Gamepad
REWIRED_VITA = 'd2aef070-7caa-42ff-b2dc-daac7e4a62b4'  # Sony Playstation Vita
# Template element id -> the PS Vita controller's (DualAnalogGamepad.asset, SonyPlaystationVita.asset).
REWIRED_VITA_ELEMENTS = {
    **{i: i for i in range(6)},  # sticks, Cross, Circle
    7: 6,  # Action Top Row 1: Square
    8: 7,  # Action Top Row 2: Triangle
    10: 8,  # Left Shoulder 1: L
    11: 8,  # Left Shoulder 2: L
    12: 9,  # Right Shoulder 1: R
    13: 9,  # Right Shoulder 2: R
    14: 10,  # Center 1: Select
    15: 11,  # Center 2: Start
    **{i: i - 7 for i in range(19, 23)},  # the d-pad
}
REWIRED_SHOULDERS = {10: 11, 12: 13}  # shoulder 1 -> shoulder 2
REWIRED_PLATFORMS = {'vita'}
REWIRED_SECTION = re.compile(r'^    joystickMaps:\n(.*?)(?=^    keyboardMaps:\n)', re.M | re.S)
REWIRED_MAP = re.compile(r'^    - id: (\d+)\n.*?(?=^    - id: |\Z)', re.M | re.S)
REWIRED_ELEMENT_MAP = re.compile(r'^      - _actionCategoryId: .*?(?=^      - _actionCategoryId: |\Z)', re.M | re.S)
REWIRED_ACTION = re.compile(r'^    - _id: (-?\d+)\n      _name: (.*)$', re.M)


def rewired_vita_maps(text: str):
    """The scene's text with a PS Vita copy of each template joystick map, or None if it has none."""
    section = REWIRED_SECTION.search(text)
    if not section or f'hardwareGuidString: {REWIRED_TEMPLATE}' not in section[1]:
        return None
    combat = {int(m[1]) for m in REWIRED_ACTION.finditer(text[: section.start()]) if m[2].startswith('Combat')}
    maps = [m[0] for m in REWIRED_MAP.finditer(section[1])]
    next_id = max(int(m.split('\n', 1)[0].split(': ')[1]) for m in maps) + 1
    added = []
    for block in maps:
        if f'hardwareGuidString: {REWIRED_TEMPLATE}\n' not in block:
            continue
        head, _, body = block.partition('      actionElementMaps:\n')
        entries = [
            (e, int(re.search(r'_actionId: (-?\d+)', e)[1]), int(re.search(r'_elementIdentifierId: (\d+)', e)[1]))
            for e in REWIRED_ELEMENT_MAP.findall(body)
        ]
        combat_on = {element for _, action, element in entries if action in combat}
        kept = []
        for entry, action, element in entries:
            if action < 0 or element not in REWIRED_VITA_ELEMENTS:
                continue
            if element in REWIRED_SHOULDERS.values() and action not in combat:
                continue
            if action in combat and REWIRED_SHOULDERS.get(element) in combat_on:
                continue
            kept.append(
                re.sub(r'_elementIdentifierId: \d+', f'_elementIdentifierId: {REWIRED_VITA_ELEMENTS[element]}', entry)
            )
        head = re.sub(r'^    - id: \d+', f'    - id: {next_id}', head, count=1)
        head = head.replace(f'hardwareGuidString: {REWIRED_TEMPLATE}', f'hardwareGuidString: {REWIRED_VITA}')
        added.append(head + '      actionElementMaps:\n' + ''.join(kept))
        next_id += 1
    return text[: section.end()] + ''.join(added) + text[section.end() :]


def rewired_vita_overlay(exported: Path, overlay: dict, platform: str):
    if platform not in REWIRED_PLATFORMS:
        return {}
    has_maps = scan(
        'rewired-template-maps',
        exported / 'Assets',
        lambda relative: relative.suffix in ('.unity', '.prefab'),
        lambda path: f'hardwareGuidString: {REWIRED_TEMPLATE}'.encode() in path.read_bytes(),
    )
    result = {}
    for found, has in has_maps.items():
        relative = Path('Assets') / found
        if has:
            new = rewired_vita_maps(current_text(exported, overlay, relative))
            if new is not None:
                result[relative] = new.encode('utf-8')
    log(f'Rewired: PS Vita joystick maps (shoulder 1 on L and R) in {len(result)} files')
    return result


# IL2CPP strips the class libraries down to what code references. PlayMaker finds its actions
# and the game's scripts by name, so the game's assemblies and plugins are kept whole. Engine types
# whose members only PlayMaker's Set/Get Property actions reach (by name, through reflection), which
# the linker would strip: ParticleSystem's emissionRate, startLifetime and loop (47 actions). And
# MeshCollider, whose sharedMesh getter only CrowCompat's MeshRelease reads (by reflection; the
# game only sets it), on by default on the PS Vita.
LINK_PRESERVE = {
    'UnityEngine.ParticleSystemModule': ['UnityEngine.ParticleSystem'],
    'UnityEngine.PhysicsModule': ['UnityEngine.MeshCollider'],
}
# Per platform, engine types only reflection reaches: the PS Vita's free memory for the memory probe
# (compat/Memory.cs).
PLATFORM_LINK_PRESERVE = {'vita': {'UnityEngine.PSVitaModule': ['UnityEngine.PSVita.Diagnostics']}}
# Per platform, plugin assemblies left to the linker instead of kept whole: no scripts, no startup
# hooks, nothing in them found by name there. The editor's linker keeps the script assemblies whole
# anyway (they are its roots). On the PS Vita: Rewired's Windows input (Rewired_Core looks its
# initializer up by name only on Windows; DetectPlatform says PSVita there), the stubbed Steam and GOG
# wrappers, PlayMaker's expression parser and ProBuilder's helpers. Measured on the build's
# UnityLinker and IL2CPP steps: 550 -> 487 MB of C++, global-metadata.dat 14.6 -> 12.6 MB.
PLATFORM_LINKED = {
    'vita': {
        'Rewired_Windows',
        'Facepunch.Steamworks.Win64',
        'GalaxyCSharp',
        'ConditionalExpression',
        'MeshExtension',
        'Unity.ProBuilder.KdTree',
        'Unity.ProBuilder.Poly2Tri',
    }
}


def link_xml(assemblies: list, platform: str):
    linked = PLATFORM_LINKED.get(platform, set())
    lines = ['<linker>']
    lines += [
        f'  <assembly fullname="{dll.stem}" preserve="all"/>' for dll in sorted(assemblies) if dll.stem not in linked
    ]
    for assembly, types in {**LINK_PRESERVE, **PLATFORM_LINK_PRESERVE.get(platform, {})}.items():
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
# memory, streamed ones stream; long clips stream (STREAM_SECONDS). The format numbers are Unity's
# AudioCompressionFormat (3 MP3, 5 HEVAG), but the 2018.2 PS Vita module encodes only HEVAG (its
# FSBTool has vagconv2 alone; no ATRAC9 or MP3 encoder), and the build has every clip as HEVAG
# (FSB5 mode 9, 961 clips) whichever is asked. HEVAG decodes cheaply in FMOD's mixer; what costs it
# is the game's reverb filters (compat/Audio.cs's FilterGate).
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
ALPHA8, ALPHA8_IMPORT = 1, 1  # TextureFormat.Alpha8, TextureImporterFormat.Alpha8
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
    size that never downscales; the PS Vita compresses those to DXT, by the image's own alpha, except
    textures the game stores as Alpha8 (TextMeshPro's SDF font atlases), which stay Alpha8 there: the
    size of DXT5, without its 4x4 blocks on the glyphs' distance edges."""
    import UnityPy

    data = game_data(game)
    compressed, alpha_only, largest = set(), set(), {}
    for path in sorted(data.glob('*.assets')):
        for obj in UnityPy.load(str(path)).objects:
            if obj.type.name != 'Texture2D':
                continue
            tree = obj.read_typetree()
            if tree['m_TextureFormat'] in COMPRESSED_TEXTURE_FORMATS:
                compressed.add(tree['m_Name'])
            if tree['m_TextureFormat'] == ALPHA8:
                alpha_only.add(tree['m_Name'])
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
            if name in alpha_only:
                chosen = ALPHA8_IMPORT
            else:
                chosen = without_alpha if fully_opaque(meta.with_suffix('')) else with_alpha
            text = text.replace(
                '  platformSettings:\n',
                '  platformSettings:\n' + TEXTURE_PLATFORM_ENTRY.format(target=target, size=size, format=chosen),
                1,
            )
        overlay[meta.relative_to(exported)] = text.encode('utf-8')
    log(f'Uncompressed import for {len(overlay)} textures; {kept} stay compressed like the original')
    return overlay


# The PS Vita player keeps a texture whose sides are not powers of two uncompressed whatever its
# importer asks (RGBA32 or RGB24): in the first PS Vita build all 650 such textures were, 178 MB summed
# over the build's files (43 MB of them in the player prefab's preloaded assets, loaded all the time),
# and every power-of-two texture was DXT. On the PS Vita such a texture becomes a power of two, so it
# compresses to DXT5, or DXT1 when opaque:
# - a Default texture only materials use is scaled to the nearest power of two by the importer
#   (nPOTScale), since materials sample it by normalized coordinates; point-filtered ones are kept;
# - a texture only Sprites use (AssetRipper exports sprites as assets, with rects in texels from the
#   bottom left) is padded at the top and right to the next power of two, its edge texels repeated so
#   filtering at the sprite's edge reads what it did. Only clamped textures: a tiled Image samples a
#   repeating sprite's whole texture.
# Textures used otherwise (scenes, prefabs, scripts' assets such as TextMeshPro's, Resources) are kept.
TEXTURE_REFERENCE = re.compile(rb'fileID: 2800000, guid: ([0-9a-f]{32})')
TEXTURE_REFERRERS = NATIVE_ASSETS | {'.unity', '.cubemap', '.flare', '.spriteatlas'}
SPRITE_DOCUMENT = b'--- !u!213 '


def texture_references(path: Path):
    """[whether the file is a lone Sprite, the texture GUIDs it names]."""
    data = path.read_bytes()
    lone_sprite = data.count(b'--- !u!') == 1 and SPRITE_DOCUMENT in data
    return [lone_sprite, sorted({g.decode() for g in TEXTURE_REFERENCE.findall(data)})]


def power_of_two(n):
    return 1 << (n - 1).bit_length()


def pad_to_power_of_two(image: Path):
    """The image padded at the top and right to power-of-two sides, the edge texels repeated: what
    Unity sees as the bottom-left corner keeps its texel coordinates."""
    import io

    from PIL import Image

    with Image.open(image) as picture:
        picture = picture.convert('RGBA' if picture.mode not in ('RGB', 'RGBA') else picture.mode)
    width, height = picture.size
    wide, high = power_of_two(width), power_of_two(height)
    padded = Image.new(picture.mode, (wide, high))
    padded.paste(picture, (0, high - height))
    if wide > width:
        column = picture.crop((width - 1, 0, width, height)).resize((wide - width, height), Image.NEAREST)
        padded.paste(column, (width, high - height))
    if high > height:
        row = padded.crop((0, high - height, wide, high - height + 1)).resize((wide, high - height), Image.NEAREST)
        padded.paste(row, (0, 0))
    buffer = io.BytesIO()
    padded.save(buffer, 'PNG')
    return buffer.getvalue()


def with_platform_format(text: str, platform: str, opaque: bool, size: int):
    """The importer text with the platform's DXT override (PLATFORM_TEXTURE_FORMATS)."""
    target, with_alpha, without_alpha = PLATFORM_TEXTURE_FORMATS[platform]
    entry = TEXTURE_PLATFORM_ENTRY.format(target=target, size=size, format=without_alpha if opaque else with_alpha)
    existing = re.compile(rf'^  - serializedVersion: \d+\n    buildTarget: {target}\n(?:    .*\n)*', re.M)
    if existing.search(text):
        return existing.sub(lambda m: entry, text, count=1)
    return text.replace('  platformSettings:\n', '  platformSettings:\n' + entry, 1)


def npot_texture_overlay(exported: Path, overlay: dict, platform: str):
    if platform not in PLATFORM_TEXTURE_FORMATS:
        return {}
    assets = exported / 'Assets'
    # Which textures each asset names, read once per file version (scan): the scenes are 1.2 GB.
    references = scan(
        'texture-references',
        assets,
        lambda relative: relative.suffix in TEXTURE_REFERRERS,
        texture_references,
        version=TEXTURE_REFERENCE.pattern.decode(),
    )
    sprites, others = {}, {}
    for found, (lone_sprite, guids) in references.items():
        for guid in guids:
            (sprites if lone_sprite else others).setdefault(guid, set()).add(found.suffix)
    result, scaled, padded, kept_bytes, saved = {}, 0, 0, 0, 0
    for found, stat in listing(assets).items():
        if stat is None or not found.name.endswith('.png.meta') or 'Resources' in found.parts:
            continue
        relative = Path('Assets') / found
        text = current_text(exported, overlay, relative)
        if 'TextureImporter:' not in text:
            continue
        image = exported / relative.with_suffix('')
        with image.open('rb') as file:
            header = file.read(24)
        width, height = struct.unpack('>II', header[16:24])
        if power_of_two(width) == width and power_of_two(height) == height:
            continue
        if re.search(rf'buildTarget: PSP2\n(?:    .*\n)*?    textureFormat: {ALPHA8_IMPORT}\n', text):
            continue  # Alpha8 is kept at any size (texture_overlay)
        guid = re.search(r'^guid: (\w+)', text, re.M)[1]
        field = dict(reversed(re.findall(r'^ {2,4}(\w+): (-?\d+)$', text, re.M))).get  # first of each
        users = others.get(guid, set())
        size = max(int(field('maxTextureSize')), power_of_two(max(width, height)))
        if guid in sprites and not users and field('wrapU') == field('wrapV') == '1':
            result[relative.with_suffix('')] = pad_to_power_of_two(image)
            padded += 1
        elif guid not in sprites and users == {'.mat'} and field('textureType') == '0' and field('filterMode') != '0':
            text = re.sub(r'^(\s*)nPOTScale: \d+$', r'\g<1>nPOTScale: 1', text, flags=re.M)
            scaled += 1
        else:
            kept_bytes += width * height * 4
            continue
        saved += width * height * 4
        text = with_platform_format(text, platform, fully_opaque(image), size)
        text = re.sub(r'^(\s*)maxTextureSize: \d+$', rf'\g<1>maxTextureSize: {size}', text, count=1, flags=re.M)
        result[relative] = text.encode('utf-8')
    log(
        f'{platform}: {scaled} textures scaled and {padded} padded to powers of two for DXT '
        f'({saved / 1e6:.0f} MB as RGBA32); {kept_bytes / 1e6:.0f} MB of others stay uncompressed'
    )
    return result


# Per platform: exported textures (by file stem) imported at most this size. The player prefab keeps
# its own copies of the four CJK fonts ("_0", see i2_font_overlay) only to write the language names in
# the options menu, so their 2048x2048 atlases (16.8 MB, preloaded: loaded all the time) were the
# largest textures always in memory on the PS Vita. At 1024 they take 4.2 MB; an SDF atlas scaled down
# keeps its distances (the material's texture size stays, so TextMeshPro's UVs still match), and
# glyphs draw softer.
PLATFORM_TEXTURE_SIZE_CAPS = {
    'vita': {
        'KH-Dot-Dougenzaka-16 SDF Atlas_0': 1024,
        'YoonA px Pixelbatang SDF Atlas_0': 1024,
        'UnifontZhCn Atlas_0': 1024,
        'UnifontZhTw Atlas_0': 1024,
    }
}


def texture_cap_overlay(exported: Path, overlay: dict, platform: str):
    caps = PLATFORM_TEXTURE_SIZE_CAPS.get(platform, {})
    if not caps:
        return {}
    target = PLATFORM_TEXTURE_FORMATS[platform][0]
    entry = re.compile(rf'(buildTarget: {target}\n    maxTextureSize: )\d+')
    result = {}
    for stem, size in caps.items():
        relative = Path('Assets') / 'Texture2D' / f'{stem}.png.meta'
        if not (exported / relative).exists():
            sys.exit(f'No {relative} in the export for its size cap')
        text, count = entry.subn(rf'\g<1>{size}', current_text(exported, overlay, relative))
        if not count:
            sys.exit(f'{relative} has no {target} import settings to cap')
        result[relative] = text.encode('utf-8')
    log(f"{platform}: {len(result)} textures capped in size (the player prefab's CJK font atlases)")
    return result


# A readable mesh keeps a CPU copy of its vertices and indices beside the GPU's. Nearly all of the
# game's meshes are readable (so are the original's): 82,000 of the first PS Vita build's 86,000, 358
# of its 480 MB of mesh data, up to 25 MB a scene (Pool). What needs them readable is
# MeshCombineStudio, which combines what it finds under its search parents at run time (its
# MeshCombiner components with combineInRuntime, nearly all searching by a MeshCombine* tag), and mesh
# colliders; no game script or PlayMaker action reads a mesh. On the PS Vita, a mesh that only scenes'
# MeshFilters use, outside every run-time MeshCombiner's search parents (or without its tag, when it
# searches by tag), is not readable: 42,000 meshes, 179 MB of the build's readable data. Should a
# combiner reach one anyway, it skips it with an error and leaves its renderer as it was.
MESH_COMBINER_SCRIPT = script_file_id('MeshCombineStudio', 'MeshCombiner')
MESH_REFERENCE = re.compile(rb'fileID: 4300000, guid: ([0-9a-f]{32})')
SCENE_DOCUMENT = re.compile(r'^--- !u!(\d+) &(-?\d+)[^\n]*\n((?:(?!--- !u!).*\n?)*)', re.M)
READABLE_FLAGS = (b'  m_IsReadable: 1\n', b'  m_KeepVertices: 1\n', b'  m_KeepIndices: 1\n')
MESH_CACHE = WORK / 'unreadable-meshes'


def scene_mesh_users(path: Path):
    """[GUIDs of meshes in reach of a run-time MeshCombiner, of other MeshFilters' meshes, of meshes
    named anywhere else in the scene]."""
    text = path.read_text(encoding='utf-8')
    father, transform_of, tag, filters, combiners, named = {}, {}, {}, {}, [], set()
    for kind, file_id, body in SCENE_DOCUMENT.findall(text):
        if kind in ('4', '224'):  # Transform, RectTransform
            parent = re.search(r'^  m_Father: \{fileID: (-?\d+)\}', body, re.M)
            father[file_id] = parent[1] if parent else '0'
            transform_of[re.search(r'^  m_GameObject: \{fileID: (-?\d+)\}', body, re.M)[1]] = file_id
        elif kind == '1':  # GameObject
            found = re.search(r'^  m_TagString: (.*)$', body, re.M)
            tag[file_id] = found[1].strip() if found else 'Untagged'
        elif kind == '33':  # MeshFilter
            mesh = MESH_REFERENCE.search(body.encode())
            if mesh:
                filters[re.search(r'^  m_GameObject: \{fileID: (-?\d+)\}', body, re.M)[1]] = mesh[1].decode()
        else:
            named.update(g.decode() for g in MESH_REFERENCE.findall(body.encode()))
            if kind == '114' and f'fileID: {MESH_COMBINER_SCRIPT},' in body and 'combineInRuntime: 1' in body:
                found = re.search(r'^    parentGOs:\n((?:    - .*\n)*)', body, re.M)
                parents = re.findall(r'fileID: (-?\d+)', found[1]) if found else []
                single = re.search(r'^    parent: \{fileID: (-?\d+)\}', body, re.M)
                if not parents and single and single[1] != '0':
                    parents = [single[1]]
                by_tag = re.search(r'^    useTag: 1$', body, re.M) is not None
                wanted = re.search(r'^    tag: (.*)$', body, re.M)
                combiners.append(
                    ({transform_of.get(p) for p in parents}, wanted[1].strip() if by_tag and wanted else None)
                )
    combined, others = set(), set()
    for game_object, mesh in filters.items():
        reach, transform, depth = set(), transform_of.get(game_object), 0
        while transform and transform != '0' and depth < 500:
            reach.add(transform)
            transform, depth = father.get(transform), depth + 1
        if any(parents & reach and wanted in (None, tag.get(game_object)) for parents, wanted in combiners):
            combined.add(mesh)
        else:
            others.add(mesh)
    return [sorted(combined), sorted(others - combined), sorted(named)]


def mesh_readable_overlay(exported: Path, overlay: dict, platform: str):
    if platform != 'vita':
        return {}
    assets = exported / 'Assets'
    scenes = scan(
        'mesh-users',
        assets,
        lambda relative: relative.suffix == '.unity',
        scene_mesh_users,
        version=f'{MESH_COMBINER_SCRIPT} {SCENE_DOCUMENT.pattern}',
    )
    elsewhere = scan(
        'mesh-references',
        assets,
        lambda relative: relative.suffix in TEXTURE_REFERRERS - {'.unity'} and relative.parts[0] != 'Mesh',
        lambda path: sorted({g.decode() for g in MESH_REFERENCE.findall(path.read_bytes())}),
        version=MESH_REFERENCE.pattern.decode(),
    )
    keep, candidates = set(), set()
    for combined, others, named in scenes.values():
        keep.update(combined, named)
        candidates.update(others)
    for guids in elsewhere.values():
        keep.update(guids)
    candidates -= keep
    guids = scan(
        'mesh-guids',
        assets,
        lambda relative: relative.parts[0] == 'Mesh' and relative.name.endswith('.asset.meta'),
        meta_guid,
    )
    # The changed meshes go into MESH_CACHE and the overlay names those files, so 42,000 meshes are
    # neither held in memory nor read again: a cached copy written after the exported file, of its
    # size (the flags keep the length), is current.
    files = listing(assets)
    result, changed = {}, 0
    for meta, guid in guids.items():
        if guid not in candidates:
            continue
        relative = Path('Assets') / meta.with_suffix('')
        if relative in overlay:
            continue
        cached = MESH_CACHE / relative
        size, modified = files[meta.with_suffix('')]
        try:
            kept = cached.stat()
        except FileNotFoundError:
            kept = None
        if kept is None or kept.st_size != size or kept.st_mtime_ns < modified:
            data = (exported / relative).read_bytes()
            head, rest = data[:4096], data[4096:]
            for flag in READABLE_FLAGS:
                head = head.replace(flag, flag[:-2] + b'0\n', 1)
            if head + rest == data:
                continue
            cached.parent.mkdir(parents=True, exist_ok=True)
            cached.write_bytes(head + rest)
        result[relative] = cached
        changed += 1
    log(f'{platform}: {changed} of {len(guids)} meshes not readable; {len(keep)} stay readable for MeshCombineStudio')
    return result


# I2 Localization's language source (Resources/I2Languages.asset, loaded at startup) lists the assets
# its terms name, so all of them stay loaded in every language: with them the CJK fonts' TextMeshPro
# font assets and their materials, whose four 2048x2048 atlases take 16 MB (4 MB each as Alpha8 or
# DXT5). I2 looks a term's asset up in that list by name, and else loads it with Resources.Load(name)
# (I2.Loc.ResourceManager.GetAsset). These fonts and materials move to Assets/Resources under their
# object names and out of the list, so only a CJK language loads them in the game's areas. (The Title
# and Arcade scenes name some of them in their texts, so they load there; the player prefab keeps its
# own "_0" copies of the four fonts, for the language names in the options menu, loaded all the time.)
I2_SOURCE = Path('Assets') / 'Resources' / 'I2Languages.asset'
ON_DEMAND_FONTS = ('KH-Dot-Dougenzaka-16 SDF', 'YoonA px Pixelbatang SDF', 'UnifontZhCn', 'UnifontZhTw')
ON_DEMAND_FOLDERS = {'MonoBehaviour': '.asset', 'Material': '.mat'}


def i2_font_overlay(exported: Path, overlay: dict, excluded: set):
    """Returns (overlay entries, excluded original paths)."""
    text = current_text(exported, overlay, I2_SOURCE)
    block = re.search(r'^    Assets:\n((?:    - .*\n)*)', text, re.M)
    listed = set(re.findall(r'guid: (\w+)', block[1])) if block else set()
    moved, gone, dropped = {}, set(), set()
    for found, stat in listing(exported / 'Assets').items():
        relative = Path('Assets') / found
        if (
            stat is None
            or len(found.parts) != 2
            or ON_DEMAND_FOLDERS.get(found.parts[0]) != found.suffix
            or not found.stem.startswith(ON_DEMAND_FONTS)
            or relative in excluded
        ):
            continue
        guid = meta_guid(exported / meta_of(relative))
        name = main_object_name(exported / relative)
        if guid not in listed or not name or re.search(r'[<>:"/\\|?*]', name):
            continue
        target = Path('Assets') / 'Resources' / (name + found.suffix)
        moved[target] = overlay.pop(relative, None) or exported / relative
        moved[meta_of(target)] = overlay.pop(meta_of(relative), None) or exported / meta_of(relative)
        gone.update({relative, meta_of(relative)})
        dropped.add(guid)
    if dropped:
        kept = ''.join(
            line for line in block[1].splitlines(keepends=True) if re.search(r'guid: (\w+)', line)[1] not in dropped
        )
        moved[I2_SOURCE] = (text[: block.start(1)] + kept + text[block.end(1) :]).encode('utf-8')
    log(f'I2 Localization: {len(dropped)} CJK font assets and materials load from Resources on demand')
    return moved, gone


# ProBuilder's ProBuilderMesh keeps its own copy of the mesh it built (positions, faces, UVs, shared
# vertices). The player prefab, which is loaded all the time, has 839 of them (the scenes none): 10 MB
# of the Windows player's managed heap at the title, the second largest holder after PlayMaker. They
# can't be left out: in the export, 842 of the prefab's MeshFilters have no mesh, because ProBuilder
# builds those meshes at run time from its own copy. Left out on the PS Vita, Mara's body and the
# prefab's other ProBuilder parts drew nothing (only her pistol, a plain mesh, showed). To save that
# memory, the meshes would have to be built into the project instead (e.g. by the build script, with
# ProBuilder's API) before the components are dropped.
PROBUILDER_MESH_SCRIPT = script_file_id('UnityEngine.ProBuilder', 'ProBuilderMesh')
PLATFORM_STRIPPED_SCRIPTS: dict[str, set[int]] = {}


def stripped_component_overlay(exported: Path, overlay: dict, platform: str):
    scripts = PLATFORM_STRIPPED_SCRIPTS.get(platform)
    if not scripts:
        return {}
    keys = [f'm_Script: {{fileID: {script},'.encode() for script in sorted(scripts)]
    has = scan(
        f'stripped-components-{platform}',
        exported / 'Assets',
        lambda relative: relative.suffix in ('.prefab', '.unity'),
        lambda path: any(key in path.read_bytes() for key in keys),
        version=' '.join(map(str, sorted(scripts))),
    )
    document = re.compile(r'^--- !u!114 &(-?\d+)[^\n]*\n(?:(?!--- !u!).*\n?)*', re.M)
    result, removed = {}, 0
    for found, named in has.items():
        relative = Path('Assets') / found
        if relative in overlay:
            named = any(key in overlay[relative] for key in keys)
        if not named:
            continue
        text = current_text(exported, overlay, relative)
        ids = set()

        def drop(m, ids=ids):
            script = re.search(r'^  m_Script: \{fileID: (-?\d+),', m[0], re.M)
            if script and int(script[1]) in scripts:
                ids.add(m[1])
                return ''
            return m[0]

        text = document.sub(drop, text)
        text = re.sub(
            r'^  - component: \{fileID: (-?\d+)\}\n', lambda m, ids=ids: '' if m[1] in ids else m[0], text, flags=re.M
        )
        removed += len(ids)
        result[relative] = text.encode('utf-8')
    log(f'{platform}: {removed} ProBuilderMesh components left out of {len(result)} prefabs and scenes')
    return result


IMPORTED_BINARY = {'.png', '.ogg', '.wav', '.mp3', '.ttf', '.otf', '.mp4', '.webm', '.txt', '.bytes', '.json'}
# A native asset's m_Name is in its first lines; reading this much of each file finds it.
NAME_SEARCH_BYTES = 4000


def original_asset_names(game: Path):
    """The names of the game's textures, audio clips, fonts, videos and text assets. Reading them
    takes UnityPy minutes, so they are kept in work/, keyed by the data files' sizes and times."""
    import json

    data = game_data(game)
    # A build keeps these assets in resources.assets and sharedassetsN.assets, never in a level.
    files = sorted(data.glob('*.assets'))
    key = hashlib.sha256(
        json.dumps([[p.name, p.stat().st_size, p.stat().st_mtime_ns] for p in files]).encode()
    ).hexdigest()
    cache = WORK / 'original-asset-names.json'
    if cache.exists():
        cached = json.loads(cache.read_text(encoding='utf-8'))
        if cached.get('key') == key:
            return set(cached['names'])
    import UnityPy

    names = set()
    for path in files:
        for obj in UnityPy.load(str(path)).objects:
            if obj.type.name in ('Texture2D', 'AudioClip', 'Font', 'VideoClip', 'TextAsset'):
                names.add(obj.peek_name())
    cache.write_text(json.dumps({'key': key, 'names': sorted(names)}), encoding='utf-8')
    return names


def main_object_name(path: Path):
    """The m_Name in a native asset's first lines (its main object's), or None."""
    with path.open(encoding='utf-8', errors='replace') as handle:
        match = re.search(r'^  m_Name: (.*)$', handle.read(NAME_SEARCH_BYTES), re.M)
    return match.group(1) if match else None


def name_overlay(exported: Path, game: Path, overlay: dict):
    """Restore original object names that AssetRipper had to change for unique file names.

    Unity names an asset's main object after its file, so AssetRipper's "Name_1.anim" becomes an
    object called "Name_1" and the game's lookups by name fail. Move each such asset to
    "<stem>/<original name><ext>" with its .meta, so the GUID is kept. Resources paths are left
    alone; they are runtime load paths. Returns (moved overlay entries, excluded original paths);
    overlay entries already generated for a moved path are moved with it; other moved entries are the
    exported file's Path, not its bytes."""
    original = original_asset_names(game)
    moved, excluded, skipped = {}, set(), 0
    assets = exported / 'Assets'
    # The native assets' m_Name, read once per file version (scan): a full read took minutes.
    object_names = scan(
        'object-names',
        assets,
        lambda relative: relative.suffix in NATIVE_ASSETS and 'Resources' not in relative.parts,
        main_object_name,
    )
    for found, stat in listing(assets).items():
        if stat is None or found.suffix not in NATIVE_ASSETS | IMPORTED_BINARY or 'Resources' in found.parts:
            continue
        path = assets / found
        name = None
        if path.suffix in NATIVE_ASSETS:
            if object_names[found] is not None and object_names[found] != path.stem:
                name = object_names[found]
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
        # A file the rules before didn't change moves as its exported path: sync_tree copies it only
        # when the project's copy differs (reading 7,600 assets each run took minutes).
        moved[target] = overlay.pop(relative, None) or path
        moved[meta_of(target)] = overlay.pop(meta, None) or exported / meta
        moved.update(folder_meta(target.parent))
        excluded.update({relative, meta})
    log(
        f'Restored original names for {len(excluded) // 2} assets'
        + (f'; {skipped} names cannot be file names' if skipped else '')
    )
    return moved, excluded


# Assets the game loads through Addressables by key (its catalog, StreamingAssets/aa): AssetRipper
# exports the bundle's assets at the top of Assets. They stay there; the build script puts them in a
# CrowCompat AddressablesTable among the player's preloaded assets (DowngradeBuild), from the list in
# ADDRESSABLES_LIST, in ProjectSettings: outside Assets, so the editor doesn't import it.
ADDRESSABLE_ASSETS = {'CharacterCameraPrefab': Path('Assets') / 'CharacterCameraPrefab.prefab'}
ADDRESSABLES_LIST = Path('ProjectSettings') / 'CrowAddressables.txt'


def addressables_overlay(exported: Path, overlay: dict):
    """The list of Addressables assets for the build script, and the Addressables catalog left out.
    Returns (overlay entries, excluded original paths)."""
    lines = []
    for key, relative in ADDRESSABLE_ASSETS.items():
        if not (exported / relative).exists():
            sys.exit(f'No {relative} in the export for the Addressables key {key}')
        lines.append(f'{key}\t{relative.as_posix()}\n')
    added, excluded = {ADDRESSABLES_LIST: ''.join(lines).encode('utf-8')}, set()
    # The catalog and the Windows bundles (70 MB), which nothing reads now.
    catalog = Path('Assets') / 'StreamingAssets' / 'aa'
    if (exported / catalog).exists():
        excluded.update(p.relative_to(exported) for p in (exported / catalog).rglob('*'))
        excluded.update({catalog, meta_of(catalog)})
    log(f'Listed {len(ADDRESSABLE_ASSETS)} Addressables assets for the preloaded table; left out {catalog}')
    return added, excluded


# Player settings whose 2018 defaults differ from the source version's behaviour.
SETTING_RULES = {
    # Unity 2019.3 removed the launch-time resolution dialog; 2018 shows it unless disabled.
    'displayResolutionDialog': '0',
    # The game ships without a player log (boot.config nolog); the port's test tools read it.
    'usePlayerLog': '1',
}
# Per platform. The PS Vita player starts in power mode A, with the GPU at 111 MHz; mode C (2)
# raises the GPU clock and gives up the camera and full screen brightness (The Drifter's port).
PLATFORM_SETTING_RULES = {
    'vita': {'psp2PowerMode': '2'},
    # Unity keeps PlayerPrefs (the game's display, sound and control settings) under the company and
    # product name, in the registry on Windows: test players get their own, apart from the game's.
    'windows': {'productName': 'Crow Country 2018 port'},
}

# The game's one quality level, per platform. On the PS Vita a High (2) shadow map crashed the
# player in GfxDeviceGXM::Clear on the first frame with a shadow (Driving Intro); Medium and Low
# draw the same shadows, soft and hard, and Medium is the larger.
PLATFORM_QUALITY_RULES = {
    'vita': {'shadowResolution': '1'},
}

# Physics settings. Unity 2022 says when physics steps with m_SimulationMode (0: in FixedUpdate,
# the game's), where 2018 has m_AutoSimulation, which the export wrote as 0: no rigidbody moved in
# the port, so Mara turned but never walked (her walk is PlayMaker AddForce on her rigidbody), and
# nothing fell or flew.
PHYSICS_RULES = {'m_AutoSimulation': '1'}


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
    overlay = {relative: text.encode('utf-8')}
    quality_rules = PLATFORM_QUALITY_RULES.get(platform, {})
    if quality_rules:
        relative = Path('ProjectSettings') / 'QualitySettings.asset'
        text = (exported / relative).read_text(encoding='utf-8')
        for key, value in quality_rules.items():
            # The levels' entries, indented under m_QualitySettings; the game has one level.
            text, count = re.subn(rf'^(    {key}:).*$', rf'\g<1> {value}', text, flags=re.M)
            if count != 1:
                sys.exit(f'QualitySettings has {count} entries for {key}, expected one quality level')
        overlay[relative] = text.encode('utf-8')
    relative = Path('ProjectSettings') / 'DynamicsManager.asset'
    text = (exported / relative).read_text(encoding='utf-8')
    for key, value in PHYSICS_RULES.items():
        text, count = re.subn(rf'^(  {key}:).*$', rf'\g<1> {value}', text, flags=re.M)
        if count != 1:
            sys.exit(f'DynamicsManager has {count} entries for {key}')
    overlay[relative] = text.encode('utf-8')
    return overlay


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
