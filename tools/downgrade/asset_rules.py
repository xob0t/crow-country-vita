"""Project rules for the exported assets and settings: each *_overlay function returns
{path in the project: new content} for pipeline.assemble, except name_overlay and
addressables_overlay, which also return the exported paths they move."""

import collections
import hashlib
import re
import struct
import sys
from pathlib import Path

from common import (
    BUILTIN_LEGACY_DIFFUSE,
    BUILTIN_PARTICLES_SURFACE,
    BUILTIN_STANDARD,
    NAME_LINE,
    NATIVE_ASSETS,
    SHADER_LINE,
    VECTOR,
    WORK,
    YAML_FILES,
    build_switches,
    expect,
    folder_meta,
    game_data,
    guid_for,
    listing,
    log,
    meta_guid,
    meta_of,
    require,
    scan,
)


def current_bytes(exported: Path, overlay: dict, relative: Path):
    """A file as the rules before left it: its overlay entry, else the export's file. An overlay entry is
    the file's bytes, or the Path of a file to copy (rules that change thousands of meshes or 1 GB of
    scenes write their results to work/ instead of holding them, and name_overlay moves files by path);
    every rule reads entries through this, or current_text, so it takes either."""
    content = overlay.get(relative)
    if content is None:
        return (exported / relative).read_bytes()
    return content.read_bytes() if isinstance(content, Path) else content


def current_text(exported: Path, overlay: dict, relative: Path):
    """current_bytes as text."""
    return current_bytes(exported, overlay, relative).decode('utf-8')


def script_file_id(namespace: str, name: str):
    """The fileID of a script class in a managed plugin: the first four bytes of
    MD4("s\\0\\0\\0" + namespace + name), little-endian."""
    from Crypto.Hash import MD4

    return struct.unpack('<i', MD4.new(('s\0\0\0' + namespace + name).encode()).digest()[:4])[0]


# CrowCompat's GUID in the project (pipeline.assembly_overlay writes its .meta with it).
COMPAT_GUID = guid_for('plugin', 'CrowCompat')

# Engine components the PS Vita's player lacks, which become a CrowCompat script of the same API.
# Loading a component of a class the player lacks leaves a hole in its GameObject's component list,
# and activating the GameObject crashes the player (PS Vita, The Drifter's port). The PS Vita's
# 2018.2 player has no VideoPlayer (the health monitor's five loops in the status screen).
# CrowCompat's plays the videos as frame atlases (video_rules.py, compat/Video.cs); the Windows
# players use it too, so they show what the PS Vita does.
COMPAT_COMPONENTS = {328: 'UnityEngine_Video_VideoPlayer'}
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
    classes = COMPAT_COMPONENTS
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
            has = header.search(current_bytes(exported, overlay, relative)) is not None
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
    expect(replaced, 4, f'{platform}: VideoPlayers replaced with CrowCompat scripts')
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
            data = current_bytes(exported, overlay, relative)
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
    expect(cut, 70, "references to scene objects cut (I2 Localization's targets)")
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
            has = b'm_AdditionalShaderChannelsFlag' in current_bytes(exported, overlay, relative)
        if has:
            text = current_text(exported, overlay, relative)
            new = CANVAS_CHANNELS.sub(add_texcoord2, text)
            if new != text:
                result[relative] = new.encode('utf-8')
    log(f'Canvases passing uv2 for UIPixelate: {changed} in {len(result)} files')
    expect(changed, 14, 'canvases passing uv2 for UIPixelate')
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
    expect(len(result), 63, 'files with Rewired PS Vita joystick maps')
    return result


# IL2CPP strips the class libraries down to what code references. PlayMaker finds its actions
# and the game's scripts by name, so the game's assemblies and plugins are kept whole. So are the engine
# types whose members PlayMaker's Set/Get Property actions reach by name, through reflection (CrowCompat's
# FastPaths makes delegates of the same accessors): every type an FSM's FsmProperty names, in the scenes
# and the assets, that one of the platform's engine modules defines (Light, ParticleSystem, AudioSource,
# Camera, RectTransform: 19 properties; uGUI and TextMeshPro are kept whole anyway). The linker strips a
# member only reflection reaches: the save rooms' fire lights threw "Set Method not found for 'range'"
# several times a frame once code_trim had removed the unused PlayMaker light actions that kept it.
FSM_PROPERTY_TYPE = re.compile(rb'^ +TargetTypeName: (\S+)\r?\n +PropertyName: ', re.M)
# By hand, engine types only CrowCompat's own reflection reaches: MeshCollider, whose sharedMesh getter
# only MeshRelease reads (the game only sets it), on by default on the PS Vita; per platform, the PS Vita's
# free memory for the memory probe (compat/Memory.cs).
LINK_PRESERVE = {'UnityEngine.PhysicsModule': ['UnityEngine.MeshCollider']}
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


def link_xml(exported: Path, engine: Path, assemblies: list, platform: str):
    """The linker's link.xml; engine: the platform's engine modules (pipeline.EditorPaths.target_engine)."""
    import dnfile

    found = scan(
        'fsm-property-types',
        exported / 'Assets',
        lambda relative: relative.suffix in YAML_FILES,
        lambda path: sorted({name.decode() for name in FSM_PROPERTY_TYPE.findall(path.read_bytes())}),
        version=FSM_PROPERTY_TYPE.pattern.decode(),
    )
    named, kept = set().union(*found.values()), {}
    for dll in sorted(engine.glob('UnityEngine.*.dll')):
        for row in dnfile.dnPE(str(dll)).net.mdtables.TypeDef or ():
            if f'{row.TypeNamespace}.{row.TypeName}' in named:
                kept.setdefault(dll.stem, set()).add(f'{row.TypeNamespace}.{row.TypeName}')
    for assembly, types in {**LINK_PRESERVE, **PLATFORM_LINK_PRESERVE.get(platform, {})}.items():
        kept.setdefault(assembly, set()).update(types)
    linked = PLATFORM_LINKED.get(platform, set())
    lines = ['<linker>']
    lines += [
        f'  <assembly fullname="{dll.stem}" preserve="all"/>' for dll in sorted(assemblies) if dll.stem not in linked
    ]
    for assembly, types in sorted(kept.items()):
        lines.append(f'  <assembly fullname="{assembly}">')
        lines += [f'    <type fullname="{t}" preserve="all"/>' for t in sorted(types)]
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
        graphics = int(require(re.search(r'^  m_ColorFormat: (\d+)$', text, re.M), path, 'm_ColorFormat')[1])
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
# Loops stream from STREAM_LOOP_SECONDS: a clip that only looping AudioSources play (every reference to it,
# in the scenes and the assets, is a looping source's; none is an FSM's or a script's), and a clip kept in
# memory stays loaded until the next area change (Ocean Kingdom starts with seagulls 1 and ocean surface 1,
# 2.9 MB; Haunted Hilltop plays fireplace, puddle baby talk and the crickets; Arcade its three music loops).
# A stream costs FMOD's buffers instead (tens of KB) and reads from the memory card while it plays. From 20 s
# underground creepy (27 scenes) and weird radio beep would stream too, many at once.
STREAM_LOOP_SECONDS = 25
CLIP_REFERENCE = re.compile(rb'fileID: 8300000, guid: ([0-9a-f]{32})')
LOOPING_SOURCE = re.compile(
    rb'^  m_audioClip: \{fileID: 8300000, guid: ([0-9a-f]{32}).*\n(?:  \w+: .*\n)*?  Loop: 1$', re.M
)


def clip_references(path: Path):
    """[the clip GUIDs a file references, those of its looping AudioSources], each once per reference."""
    data = path.read_bytes()
    return [[guid.decode() for guid in pattern.findall(data)] for pattern in (CLIP_REFERENCE, LOOPING_SOURCE)]


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


# compat/Audio.cs's SharedReverb: sources whose reverb filters are alike share one reverb, an SFX Reverb on the
# Master group of a mixer of its own (its 14 parameters exposed as p_<name>, set at run time from the filters;
# the mixer's output, the members' old group, too). One mixer per group in use, at most SHARED_REVERB_MIXERS at
# once (Pool has six groups, the player two). Every platform gets them; SharedReverb is on by default on the PS Vita.
SHARED_REVERB_MIXERS = 12
SHARED_REVERB_FOLDER = Path('Assets') / 'Resources' / 'CrowSharedReverb'
# The SFX Reverb's parameters, as the editor names them, and Unity's defaults (Room at -10000: no reverb until set).
SHARED_REVERB_PARAMETERS = [
    ('Dry Level', 0), ('Room', -10000), ('Room HF', 0), ('Decay Time', 1), ('Decay HF Ratio', 0.5), ('Reflections', -10000),
    ('Reflect Delay', 0.02), ('Reverb', 0), ('Reverb Delay', 0.04), ('Diffusion', 100), ('Density', 100),
    ('HF Reference', 5000), ('Room LF', 0), ('LF Reference', 250),
]


def shared_reverb_mixer(index: int):
    """The YAML of SharedReverb<index>.mixer, as the 2018.2 editor writes a mixer (AudioMixerController) whose
    Master group has an SFX Reverb before its Attenuation, with the reverb's parameters exposed."""
    name = f'SharedReverb{index}'

    def guid(part):
        return guid_for('shared reverb', f'{name}/{part}')

    params = [(label, default, guid(f'param {label}')) for label, default in SHARED_REVERB_PARAMETERS]
    head = ['  m_ObjectHideFlags: 0', '  m_CorrespondingSourceObject: {fileID: 0}', '  m_PrefabInternal: {fileID: 0}']
    effect_head = ['  m_ObjectHideFlags: 3', *head[1:]]
    lines = [
        '%YAML 1.1',
        '%TAG !u! tag:unity3d.com,2011:',
        '--- !u!241 &24100000',
        'AudioMixerController:',
        *head,
        f'  m_Name: {name}',
        '  m_OutputGroup: {fileID: 0}',
        '  m_MasterGroup: {fileID: 24300002}',
        '  m_Snapshots:',
        '  - {fileID: 24500008}',
        '  m_StartSnapshot: {fileID: 24500008}',
        '  m_SuspendThreshold: -80',
        '  m_EnableSuspend: 1',
        '  m_UpdateMode: 0',
        '  m_ExposedParameters:',
        *(f'  - guid: {g}\n    name: p_{label.replace(" ", "")}' for label, _, g in params),
        '  m_AudioMixerGroupViews: []',
        '  m_CurrentViewIndex: 0',
        '  m_TargetSnapshot: {fileID: 24500008}',
        '--- !u!243 &24300002',
        'AudioMixerGroupController:',
        *head,
        '  m_Name: Master',
        '  m_AudioMixer: {fileID: 24100000}',
        f'  m_GroupID: {guid("group")}',
        '  m_Children: []',
        f'  m_Volume: {guid("volume")}',
        f'  m_Pitch: {guid("pitch")}',
        '  m_Effects:',
        '  - {fileID: 24400004}',
        '  - {fileID: 24400006}',
        '  m_UserColorIndex: 0',
        '  m_Mute: 0',
        '  m_Solo: 0',
        '  m_BypassEffects: 0',
        '--- !u!244 &24400004',
        'AudioMixerEffectController:',
        *effect_head,
        '  m_Name: ',
        f'  m_EffectID: {guid("reverb")}',
        '  m_EffectName: SFX Reverb',
        f'  m_MixLevel: {guid("reverb mix")}',
        '  m_Parameters:',
        *(f'  - m_ParameterName: {label}\n    m_GUID: {g}' for label, _, g in params),
        '  m_SendTarget: {fileID: 0}',
        '  m_EnableWetMix: 0',
        '  m_Bypass: 0',
        '--- !u!244 &24400006',
        'AudioMixerEffectController:',
        *effect_head,
        '  m_Name: ',
        f'  m_EffectID: {guid("attenuation")}',
        '  m_EffectName: Attenuation',
        f'  m_MixLevel: {guid("attenuation mix")}',
        '  m_Parameters: []',
        '  m_SendTarget: {fileID: 0}',
        '  m_EnableWetMix: 0',
        '  m_Bypass: 0',
        '--- !u!245 &24500008',
        'AudioMixerSnapshotController:',
        *head,
        '  m_Name: Snapshot',
        '  m_AudioMixer: {fileID: 24100000}',
        f'  m_SnapshotID: {guid("snapshot")}',
        '  m_FloatValues:',
        *(f'    {g}: {default}' for _, default, g in params),
        '  m_TransitionOverrides: {}',
    ]
    return '\n'.join(lines) + '\n'


def shared_reverb_mixers():
    """{path: content} of SharedReverb's mixers and their folder in the project's Resources."""
    overlay = folder_meta(SHARED_REVERB_FOLDER)
    for index in range(SHARED_REVERB_MIXERS):
        path = SHARED_REVERB_FOLDER / f'SharedReverb{index}.mixer'
        overlay[path] = shared_reverb_mixer(index).encode()
        meta = [
            'fileFormatVersion: 2',
            f'guid: {guid_for("shared reverb", path.name)}',
            'NativeFormatImporter:',
            '  externalObjects: {}',
            '  mainObjectFileID: 24100000',
            '  userData: ',
            '  assetBundleName: ',
            '  assetBundleVariant: ',
        ]
        overlay[meta_of(path)] = ('\n'.join(meta) + '\n').encode()
    return overlay


def audio_overlay(exported: Path, platform: str):
    formats = PLATFORM_AUDIO.get(platform)
    if not formats:
        return shared_reverb_mixers()
    overlay, streamed, loops = shared_reverb_mixers(), [], []
    references, looping = collections.Counter(), collections.Counter()
    for every, looped in scan('clip-references', exported / 'Assets', lambda relative: relative.suffix in YAML_FILES,
                              clip_references, version=LOOPING_SOURCE.pattern.decode()).values():
        references.update(every)
        looping.update(looped)
    looped = {guid for guid, count in looping.items() if count == references[guid]}
    settings = re.compile(
        r'^(  defaultSettings:\n    loadType: )(\d)(\n(?:    .*\n)*?    compressionFormat: )(\d+)'
        r'(\n    quality: )([\d.]+)',
        re.M,
    )
    clips = 0
    for meta in (exported / 'Assets').rglob('*.meta'):
        if meta.suffixes[-2:-1] not in (['.ogg'], ['.wav'], ['.mp3']):
            continue
        text = original = meta.read_text(encoding='utf-8')
        if 'AudioImporter:' not in text:
            continue

        def convert(m, meta=meta, original=original):
            load_type, compression, quality = formats[int(m[2])]
            if load_type == 1:
                stem = meta.with_suffix('').stem
                seconds = audio_seconds(meta.with_suffix('')) or 0
                loop = seconds >= STREAM_LOOP_SECONDS and re.search(r'^guid: (\w+)', original, re.M)[1] in looped
                if loop or seconds >= STREAM_SECONDS:
                    load_type, compression, quality = formats[2]
                    (streamed if seconds >= STREAM_SECONDS else loops).append(stem)
            return m[1] + str(load_type) + m[3] + str(compression) + m[5] + (m[6] if quality is None else str(quality))

        text, count = settings.subn(convert, text, count=1)
        if not count:
            sys.exit(f'{meta}: an AudioImporter whose defaultSettings audio_overlay cannot read')
        text = re.sub(r'^  preloadAudioData: 1$', '  preloadAudioData: 0', text, flags=re.M)
        if text != original:
            overlay[meta.relative_to(exported)] = text.encode('utf-8')
            clips += 1
    log(
        f'{platform} audio import changed for {clips} clips; {len(streamed)} long clips stream, and '
        f'{len(loops)} loops of {STREAM_LOOP_SECONDS} s or more: {", ".join(sorted(loops))}'
    )
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
            chosen = ALPHA8_IMPORT if name in alpha_only else dxt_format(platform, fully_opaque(meta.with_suffix('')))
            text = with_platform_format(text, platform, chosen, size)
        overlay[meta.relative_to(exported)] = text.encode('utf-8')
    log(f'Uncompressed import for {len(overlay)} textures; {kept} stay compressed like the original')
    return overlay


# The PS Vita player keeps a texture whose sides are not powers of two uncompressed whatever its
# importer asks (RGBA32 or RGB24): in the first PS Vita build all 650 such textures were, 178 MB summed
# over the build's files (43 MB of them in the player prefab's preloaded assets, loaded all the time),
# and every power-of-two texture was DXT. On the PS Vita such a texture becomes a power of two, so it
# compresses to DXT5, or DXT1 when opaque:
# - a texture only Sprites use (AssetRipper exports sprites as assets, with rects in texels from the
#   bottom left) is padded at the top and right to the next power of two, its edge texels repeated so
#   filtering at the sprite's edge reads what it did. Only clamped textures: a tiled Image samples a
#   repeating sprite's whole texture.
# - any other smoothly filtered texture that no Sprite uses (materials, light cookies, the camera
#   effect's CRT mask, RawImages: all sample it by normalized coordinates) is scaled to the nearest power
#   of two by the importer (nPOTScale; a Default texture only, the other types ignore it);
# - one Sprites use as well, when square, is resized here to the power of two below its side, its sprites
#   scaled with it (sprite_texture_cap): 400x400 to 256, 0.61 MB of RGBA32 to 64 KB of DXT5.
# Kept: point-filtered textures (pixel art, the button icons; resampled they would blur), non-square
# ones that Sprites share (a sprite's pixels per unit is one number for both sides), those scripts'
# assets name (TextMeshPro's sprite assets keep glyph rects in texels), and Resources.
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


def dxt_format(platform: str, opaque: bool):
    """The platform's DXT TextureImporterFormat for an image with or without alpha (PLATFORM_TEXTURE_FORMATS)."""
    _, with_alpha, without_alpha = PLATFORM_TEXTURE_FORMATS[platform]
    return without_alpha if opaque else with_alpha


def with_platform_format(text: str, platform: str, texture_format: int, size: int):
    """The importer text with the platform's override (TEXTURE_PLATFORM_ENTRY) added, or replacing its own."""
    target = PLATFORM_TEXTURE_FORMATS[platform][0]
    entry = TEXTURE_PLATFORM_ENTRY.format(target=target, size=size, format=texture_format)
    existing = re.compile(rf'^  - serializedVersion: \d+\n    buildTarget: {target}\n(?:    .*\n)*', re.M)
    if existing.search(text):
        return existing.sub(lambda m: entry, text, count=1)
    return text.replace('  platformSettings:\n', '  platformSettings:\n' + entry, 1)


def texture_reference_scan(assets: Path):
    """{asset: texture_references(asset)}, read once per file version (scan): the scenes are 1.2 GB."""
    return scan(
        'texture-references',
        assets,
        lambda relative: relative.suffix in TEXTURE_REFERRERS,
        texture_references,
        version=TEXTURE_REFERENCE.pattern.decode(),
    )


def npot_texture_overlay(exported: Path, overlay: dict, platform: str):
    if platform not in PLATFORM_TEXTURE_FORMATS:
        return {}
    assets = exported / 'Assets'
    references = texture_reference_scan(assets)
    sprites, others = {}, {}
    for found, (lone_sprite, guids) in references.items():
        for guid in guids:
            if lone_sprite:
                sprites.setdefault(guid, []).append(Path('Assets') / found)
            else:
                others.setdefault(guid, set()).add(found.suffix)
    result, scaled, padded, resized, kept_bytes, saved = {}, 0, 0, 0, 0, 0
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
        guid = require(re.search(r'^guid: (\w+)', text, re.M), relative, 'guid')[1]
        field = dict(reversed(re.findall(r'^ {2,4}(\w+): (-?\d+)$', text, re.M))).get  # first of each
        users = others.get(guid, set())
        size = max(int(field('maxTextureSize')), power_of_two(max(width, height)))
        smooth = field('filterMode') != '0' and '.asset' not in users
        if guid in sprites and not users and field('wrapU') == field('wrapV') == '1':
            result[relative.with_suffix('')] = pad_to_power_of_two(image)
            padded += 1
        elif smooth and guid not in sprites and field('textureType') == '0':
            text = re.sub(r'^(\s*)nPOTScale: \d+$', r'\g<1>nPOTScale: 1', text, flags=re.M)
            scaled += 1
        elif smooth and width == height:
            size = power_of_two(width) // 2
            result.update(sprite_texture_cap(exported, overlay, relative.with_suffix(''), sprites.get(guid, []), size))
            resized += 1
        else:
            kept_bytes += width * height * 4
            continue
        saved += width * height * 4
        text = with_platform_format(text, platform, dxt_format(platform, fully_opaque(image)), size)
        text = re.sub(r'^(\s*)maxTextureSize: \d+$', rf'\g<1>maxTextureSize: {size}', text, count=1, flags=re.M)
        result[relative] = text.encode('utf-8')
    log(
        f'{platform}: {scaled} textures scaled, {padded} padded and {resized} resized to powers of two for DXT '
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
# Per platform: the largest textures of Haunted Hilltop's and Ocean Kingdom's loads, halved or more (the
# user accepts a softer look there for the memory). Area textures sit in main memory on the PS Vita, and
# those two areas' loads bottomed at 4-7 MB free (Ocean Kingdom -> Haunted Hilltop). Most are shared with other
# areas, which get the same cut. The 400x400 ones are DXT5 at 512 after npot_texture_overlay (padded or
# scaled up); at 256 they take a quarter (256 -> 64 KB). Copies a texture has in the export are capped
# alike, so texture_dedup_overlay still finds them identical. Haunted Hilltop about -3.6 MB,
# Ocean Kingdom about -4.9 MB. Without a PS Vita entry (textures the game ships compressed), the default
# platform's size is capped. Empty: as before.
PLATFORM_TEXTURE_AREA_CAPS = {
    'vita': {
        'blood pool 1': 512,  # 1024 DXT5, 41 areas
        'water surface': 512,  # 1024 DXT5
        'ocean light - Copy': 512,  # 1024 DXT1 with mipmaps, Ocean Kingdom and its submarine
        'concrete 2': 512,  # 1024 DXT1
        'concrete 2_0': 512,
        'rock wall 1': 512,  # 1024 DXT1, Ocean Kingdom
        'grime 2': 512,  # 512x1024 DXT1 with mipmaps
        'blur circle_0': 256,  # 400x400 sprites, padded to 512
        'ring4': 256,  # 512 DXT5 particles
        'lightning particle grid 1': 256,
        'trash symbol': 256,
        'drip': 256,
    }
}


def texture_cap_overlay(exported: Path, overlay: dict, platform: str):
    area = PLATFORM_TEXTURE_AREA_CAPS.get(platform, {})
    caps = {**PLATFORM_TEXTURE_SIZE_CAPS.get(platform, {}), **area}
    if not caps:
        return {}
    target = PLATFORM_TEXTURE_FORMATS[platform][0]
    entry = re.compile(rf'(buildTarget: {target}\n    maxTextureSize: )\d+')
    default = re.compile(r'(^  maxTextureSize: |buildTarget: DefaultTexturePlatform\n    maxTextureSize: )\d+', re.M)
    sprites = {}
    for found, (lone_sprite, guids) in texture_reference_scan(exported / 'Assets').items():
        for guid in guids if lone_sprite else ():
            sprites.setdefault(guid, []).append(Path('Assets') / found)
    result, resized = {}, 0
    for stem, size in caps.items():
        relative = Path('Assets') / 'Texture2D' / f'{stem}.png.meta'
        if not (exported / relative).exists():
            sys.exit(f'No {relative} in the export for its size cap')
        text, count = entry.subn(rf'\g<1>{size}', current_text(exported, overlay, relative))
        if not count and stem in area:
            text, count = default.subn(rf'\g<1>{size}', text)
        if not count:
            sys.exit(f'{relative} has no {target} import settings to cap')
        result[relative] = text.encode('utf-8')
        guid = require(re.search(r'^guid: (\w+)', text, re.M), relative, 'guid')[1]
        if guid in sprites:
            capped = sprite_texture_cap(exported, overlay, relative.with_suffix(''), sprites[guid], size)
            result.update(capped)
            resized += bool(capped)
    log(
        f"{platform}: {len(caps)} textures capped in size (CJK font atlases; {len(area)} of the big areas'), "
        f'{resized} of them resized here with their sprites'
    )
    return result


# The importer's maxTextureSize shrinks a texture but not the sprites drawn from it: their rects stay in the
# full size's texels, and uGUI and SpriteRenderers divide them by the texture's size. A sprite on a 400x400
# texture capped at 256 (or padded to 512 by npot_texture_overlay, then capped) reached past the texture and
# drew its picture shrunk into the lower left of its rect: the map's save and root markers on the PS Vita
# (29 Oct). So a capped texture that sprites draw from is resized here (bicubic, the alpha premultiplied),
# and its sprites' sizes in texels (rects, offsets, borders, the pivot's place in the texture) and their
# pixels per unit are scaled alike: the same size in units and on the canvas, the same pivot.
SPRITE_TEXELS = re.compile(r'^( +(?:x|y|width|height|m_PixelsToUnits): )(\S+)$', re.M)
SPRITE_TEXEL_MAPS = re.compile(r'^( +(?:m_Offset|m_Border|textureRectOffset|uvTransform): )\{(.*)\}$', re.M)
MAP_VALUE = re.compile(r'(\w+: )([^,]+)')


def sprite_texture_cap(exported: Path, overlay: dict, image: Path, sprite_assets: list, size: int):
    """{the image resized to size on its longer side, its sprites scaled to match} (see SPRITE_TEXELS)."""
    import io

    from PIL import Image

    with Image.open(io.BytesIO(current_bytes(exported, overlay, image))) as picture:
        picture.load()
    factor = size / max(picture.size)
    if factor >= 1:
        return {}
    new_size = (round(picture.width * factor), round(picture.height * factor))
    if picture.mode in ('RGBA', 'LA', 'P') or 'transparency' in picture.info:
        picture = picture.convert('RGBA').convert('RGBa').resize(new_size, Image.BICUBIC).convert('RGBA')
    else:
        picture = picture.convert('RGB').resize(new_size, Image.BICUBIC)
    buffer = io.BytesIO()
    picture.save(buffer, 'PNG')
    result = {image: buffer.getvalue()}

    def scaled(m):
        return m[1] + f'{float(m[2]) * factor:.7g}'

    for sprite in sprite_assets:
        text = SPRITE_TEXELS.sub(scaled, current_text(exported, overlay, sprite))
        text = SPRITE_TEXEL_MAPS.sub(lambda m: m[1] + '{' + MAP_VALUE.sub(scaled, m[2]) + '}', text)
        result[sprite] = text.encode('utf-8')
    return result


# A readable mesh keeps a CPU copy of its vertices and indices beside the GPU's. Nearly all of the
# game's meshes are readable (so are the original's): 82,000 of the first PS Vita build's 86,000, 358
# of its 480 MB of mesh data, up to 25 MB a scene (Pool). What needs them readable is
# MeshCombineStudio, which combines what it finds under its search parents at run time (its
# MeshCombiner components with combineInRuntime, nearly all searching by a MeshCombine* tag), and mesh
# colliders; no game script or PlayMaker action reads a mesh. On the PS Vita, a mesh that only scenes'
# MeshFilters use, outside every run-time MeshCombiner's search parents (or without its tag, when it
# searches by tag), is not readable: 42,000 meshes, 179 MB of the build's readable data. Should a
# combiner reach one anyway, it skips it with an error and leaves its renderer as it was. The flags sit
# in a mesh file's first READABLE_FLAGS_BYTES, before its index and vertex data (all 85,799 exported
# meshes): only that much is searched.
MESH_COMBINER_SCRIPT = script_file_id('MeshCombineStudio', 'MeshCombiner')
MESH_REFERENCE = re.compile(rb'fileID: 4300000, guid: ([0-9a-f]{32})')
SCENE_DOCUMENT = re.compile(r'^--- !u!(\d+) &(-?\d+)[^\n]*\n((?:(?!--- !u!).*\n?)*)', re.M)
READABLE_FLAGS = (b'  m_IsReadable: 1\n', b'  m_KeepVertices: 1\n', b'  m_KeepIndices: 1\n')
READABLE_FLAGS_BYTES = 4096
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


def scene_mesh_scan(assets: Path):
    """{scene: scene_mesh_users(scene)}, read once per file version."""
    return scan(
        'mesh-users',
        assets,
        lambda relative: relative.suffix == '.unity',
        scene_mesh_users,
        version=f'{MESH_COMBINER_SCRIPT} {SCENE_DOCUMENT.pattern}',
    )


def mesh_references_elsewhere(assets: Path):
    """{prefab or asset: the mesh GUIDs it names}, read once per file version."""
    return scan(
        'mesh-references',
        assets,
        lambda relative: relative.suffix in TEXTURE_REFERRERS - {'.unity'} and relative.parts[0] != 'Mesh',
        lambda path: sorted({g.decode() for g in MESH_REFERENCE.findall(path.read_bytes())}),
        version=MESH_REFERENCE.pattern.decode(),
    )


def mesh_guids(assets: Path):
    """{Mesh/<name>.asset.meta: the mesh's GUID}."""
    return scan(
        'mesh-guids',
        assets,
        lambda relative: relative.parts[0] == 'Mesh' and relative.name.endswith('.asset.meta'),
        meta_guid,
    )


def mesh_readable_overlay(exported: Path, overlay: dict, platform: str, duplicates: dict | None = None):
    """duplicates: mesh_duplicates' {copy's GUID: the GUID the scenes name instead}."""
    if platform != 'vita':
        return {}
    assets = exported / 'Assets'
    scenes = scene_mesh_scan(assets)
    elsewhere = mesh_references_elsewhere(assets)
    keep, candidates = set(), set()
    for combined, others, named in scenes.values():
        keep.update(combined, named)
        candidates.update(others)
    for guids in elsewhere.values():
        keep.update(guids)
    if duplicates:
        # A group's one mesh stays readable if any of its copies had to.
        keep = {duplicates.get(guid, guid) for guid in keep}
        candidates = {duplicates.get(guid, guid) for guid in candidates}
    candidates -= keep
    guids = mesh_guids(assets)
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
            head, rest = data[:READABLE_FLAGS_BYTES], data[READABLE_FLAGS_BYTES:]
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


# Meshes whose vertex data the game streams from a .resS file (m_StreamData): the 1,460 meshes that
# MeshCombineStudio combined in the editor and SFB saved into the scenes (walls, fences, pipes, cars, a
# level's vertex-coloured parts in one mesh: Roadside's Ford Fiesta, the red car's body, the fence rails
# and pipes), 103 MB in all, 0.3-6.6 MB an area. AssetRipper 2.0.0 exports their index buffers but
# not their vertex data (m_DataSize 0, _typelessdata empty), so the port built them with every vertex at
# the origin: drawn, culled as visible, and invisible on every platform. Each such exported mesh gets the
# vertex data of the game's mesh with its name, vertex count, index buffer and bounds, read from the .resS
# file; the vertex layout (m_Channels) is the export's, which it copied from the game. The key has the
# bounds too: a combiner names each cell's mesh after its material, so cells of the same parts (two vents,
# a pipe, a cabinet) have the same name, count and index buffer at other places (110 meshes in 50 such
# groups; without the bounds, 55 drew with another's vertices, up to 17 m from their place). Only
# Underground SE's and NW's `elevator 5` cells (80 of 1,680 vertices 0.44 m apart) also have the same bounds:
# a key with sources whose vertices differ takes the one in the level file of the scene that uses the mesh.
STREAMED_MESHES = WORK / 'streamed-meshes.json'
STREAMED_MESH_CACHE = WORK / 'streamed-meshes'
NO_VERTEX_DATA = re.compile(rb'^    m_DataSize: 0\n    _typelessdata: *\n', re.M)
MESH_KEY_VERSION = 'bounds'
LOCAL_AABB = re.compile(
    rb'^  m_LocalAABB:\n'
    rb'    m_Center: \{x: (\S+), y: (\S+), z: (\S+)\}\n'
    rb'    m_Extent: \{x: (\S+), y: (\S+), z: (\S+)\}\n',
    re.M,
)


def mesh_key(name: str, vertex_count: int, index_buffer: bytes, bounds):
    """bounds: m_LocalAABB's centre and extent (x, y, z each), as single floats, so that the export's
    text and the game's values give the same key."""
    floats = ','.join(repr(struct.unpack('<f', struct.pack('<f', float(value)))[0]) for value in bounds)
    return f'{name}|{vertex_count}|{hashlib.sha1(index_buffer).hexdigest()}|{floats}'


def original_streamed_meshes(game: Path):
    """{mesh_key: [[.resS file name, offset, size], ...]} for the game's meshes with streamed vertex data. Reading
    the data files takes UnityPy minutes, so the result is kept in work/, keyed by their sizes and times."""
    import json

    data = game_data(game)
    files = sorted(p for p in data.iterdir() if re.fullmatch(r'level\d+|.*\.assets', p.name))
    key = hashlib.sha256(
        json.dumps(
            [MESH_KEY_VERSION, 'sources', *([p.name, p.stat().st_size, p.stat().st_mtime_ns] for p in files)]
        ).encode()
    ).hexdigest()
    try:
        cached = json.loads(STREAMED_MESHES.read_text(encoding='utf-8'))
        if cached.get('key') == key:
            return cached['meshes']
    except (OSError, ValueError):
        pass
    import UnityPy

    meshes = {}
    for path in files:
        for obj in UnityPy.load(str(path)).objects:
            if obj.type.name != 'Mesh':
                continue
            tree = obj.read_typetree()
            stream = tree.get('m_StreamData') or {}
            if not stream.get('size'):
                continue
            aabb = tree['m_LocalAABB']
            bounds = [aabb[part][axis] for part in ('m_Center', 'm_Extent') for axis in 'xyz']
            count = tree['m_VertexData']['m_VertexCount']
            found = mesh_key(tree['m_Name'], count, bytes(tree['m_IndexBuffer']), bounds)
            meshes.setdefault(found, []).append([Path(stream['path']).name, stream['offset'], stream['size']])
    STREAMED_MESHES.write_text(json.dumps({'key': key, 'meshes': meshes}), encoding='utf-8')
    return meshes


def exported_mesh_key(path: Path):
    """mesh_key of an exported mesh without vertex data, else None."""
    data = path.read_bytes()
    if not NO_VERTEX_DATA.search(data):
        return None
    name = re.search(rb'^  m_Name: (.*)\n', data, re.M)
    count = re.search(rb'^    m_VertexCount: (\d+)\n', data, re.M)
    indices = re.search(rb'^  m_IndexBuffer: ([0-9a-f]*)\n', data, re.M)
    bounds = LOCAL_AABB.search(data)
    if not (name and count and indices and bounds) or count[1] == b'0':
        return None
    return mesh_key(name[1].decode('utf-8'), int(count[1]), bytes.fromhex(indices[1].decode()), bounds.groups())


def read_source(data: Path, source):
    name, offset, length = source
    with (data / name).open('rb') as handle:
        handle.seek(offset)
        return handle.read(length)


def scene_source(exported: Path, relative: Path, sources):
    """Of a key's sources whose vertices differ, the one in the level file (level<N>.resS,
    sharedassets<N>.assets.resS) of the one scene that uses the mesh, N its build index."""
    assets = exported / 'Assets'
    guid = mesh_guids(assets).get(relative.with_name(relative.name + '.meta'))
    scenes = [s for s, users in scene_mesh_scan(assets).items() if any(guid in found for found in users)]
    settings = (exported / 'ProjectSettings' / 'EditorBuildSettings.asset').read_text(encoding='utf-8')
    order = re.findall(r'^  - enabled: 1\n    path: Assets/(.*)$', settings, re.M)
    levels = {order.index(s.as_posix()) for s in scenes if s.as_posix() in order}
    level = re.compile(r'(?:level|sharedassets)(\d+)(?:\.assets)?\.resS')
    picked = [s for s in sources if level.fullmatch(s[0]) and int(level.fullmatch(s[0])[1]) in levels]
    if len(scenes) != 1 or len(picked) != 1:
        sys.exit(
            f'Meshes: {relative.as_posix()} matches {len(sources)} game meshes with other vertices '
            f'({", ".join(s[0] for s in sources)}), and its scenes '
            f'({", ".join(s.as_posix() for s in scenes) or "none"}) single out none of them'
        )
    log(f'Meshes: {relative.as_posix()} takes its vertices from {picked[0][0]}, the level of {scenes[0].stem}')
    return picked[0]


def streamed_mesh_overlay(exported: Path, game: Path):
    """The exported meshes without vertex data, with the game's (files in STREAMED_MESH_CACHE)."""
    assets = exported / 'Assets'
    empty = {
        relative: found
        for relative, found in scan(
            'mesh-without-vertices',
            assets,
            lambda relative: relative.parts[0] == 'Mesh' and relative.suffix == '.asset',
            exported_mesh_key,
            version=NO_VERTEX_DATA.pattern.decode() + MESH_KEY_VERSION,
        ).items()
        if found
    }
    if not empty:
        return {}
    originals = original_streamed_meshes(game)
    data = game_data(game)
    files = listing(assets)
    result, missing = {}, []
    for relative, found in sorted(empty.items()):
        sources = originals.get(found)
        if sources is None:
            missing.append(relative.as_posix())
            continue
        source = sources[0]
        if len({read_source(data, s) for s in sources}) > 1:
            source = scene_source(exported, relative, sources)
        cached = STREAMED_MESH_CACHE / relative
        size, modified = files[relative]
        stamp = cached.with_name(cached.name + '.source')
        if not (cached.exists() and stamp.exists() and stamp.read_text() == f'{size} {modified} {source}'):
            vertices = read_source(data, source)
            text = (exported / 'Assets' / relative).read_bytes()
            lines = f'    m_DataSize: {len(vertices)}\n    _typelessdata: {vertices.hex()}\n'.encode()
            text = NO_VERTEX_DATA.sub(lambda _, lines=lines: lines, text, count=1)
            cached.parent.mkdir(parents=True, exist_ok=True)
            cached.write_bytes(text)
            stamp.write_text(f'{size} {modified} {source}')
        result[Path('Assets') / relative] = cached
    log(
        f'Meshes: vertex data from the game for {len(result)} meshes the export left without'
        + (f'; none found for {len(missing)} ({", ".join(missing[:5])})' if missing else '')
    )
    return result


# Meshes with the same geometry. ProBuilder gives every object a mesh of its own, so the game has many
# copies of each shape (a step, a tile, a pillar): Pool's 4,517 meshes are 617 shapes, and the copies
# were 30 of its 40 MB of mesh memory on the PS Vita (readable ones counted twice; Ocean Kingdom 32 of
# 38, Underground NE 32 of 40, Haunted Hilltop 22 of 29, Station Square 16 of 21), besides the cost of
# 3,900 more Mesh objects. The scenes' references to a copy name one mesh of its group instead (the
# lowest GUID), and the copies are no longer built; the group's mesh is readable if any copy had to be
# (mesh_readable_overlay). Only meshes that scenes alone name: a mesh a prefab or another asset names
# (the player prefab, loaded all the time) is left alone, so no mesh is shared by the resident player
# and an area, whose meshes MeshRelease makes unreadable once combined. No game script or PlayMaker
# action reads or writes a mesh; MeshCombineStudio, MeshColliders and particle systems take shared ones.
MESH_DEDUP_CACHE = WORK / 'deduplicated-scenes'
READABLE_LINES = re.compile(rb'^  m_(?:IsReadable|KeepVertices|KeepIndices): \d+\n', re.M)
MESH_NAME_LINE = re.compile(rb'^  m_Name: .*\n', re.M)


def mesh_geometry(path: Path):
    return geometry_hash(path.read_bytes())


def geometry_hash(data: bytes):
    """A hash of a mesh asset's text after its name, its read/write flags left out."""
    name = MESH_NAME_LINE.search(data)
    return hashlib.sha1(READABLE_LINES.sub(b'', data[name.end() :] if name else data)).hexdigest()


def mesh_duplicates(exported: Path, overlay: dict):
    """{a copy's GUID: the GUID of its group's mesh}, for meshes only scenes name, as the rules before left
    them (streamed_mesh_overlay gave some vertex data)."""
    assets = exported / 'Assets'
    geometry = scan(
        'mesh-geometry',
        assets,
        lambda relative: relative.parts[0] == 'Mesh' and relative.suffix == '.asset',
        mesh_geometry,
        version=READABLE_LINES.pattern.decode() + MESH_NAME_LINE.pattern.decode(),
    )
    for relative in geometry:
        if Path('Assets') / relative in overlay:
            geometry[relative] = geometry_hash(current_bytes(exported, overlay, Path('Assets') / relative))
    named_elsewhere = {guid for guids in mesh_references_elsewhere(assets).values() for guid in guids}
    groups = {}
    for meta, guid in mesh_guids(assets).items():
        shape = geometry.get(meta.with_suffix(''))
        if shape and guid not in named_elsewhere:
            groups.setdefault(shape, []).append(guid)
    duplicates = {}
    for members in groups.values():
        kept = min(members)
        duplicates.update((guid, kept) for guid in members if guid != kept)
    log(f'Meshes: {len(duplicates)} copies of {len(set(duplicates.values()))} shapes replaced by one mesh each')
    return duplicates


# Textures with the same image and import settings. The player prefab came from an Addressables bundle
# with copies of its own of textures the areas use too ("old paper wallpaper grunge 2" and "..._0"), so
# an area loaded a second copy of what the resident player holds: 1.7-5.4 MB a big area, 6 MB on the
# title. References to a copy (from scenes, materials, sprites, other assets) name one texture of its
# group instead: the one the player prefab reaches, else the lowest GUID. Only copies whose image (after
# npot_texture_overlay) and import settings (after the texture rules) are the same byte for byte, and
# not readable ones: TextMeshPro's font atlases are, for dynamic glyphs written at run time, and two font
# assets sharing one atlas could write over each other.
TEXTURE_USE = re.compile(rb'fileID: (?:2800000|21300000), guid: ([0-9a-f]{32})')
ANY_GUID = re.compile(rb'guid: ([0-9a-f]{32})')
META_IDENTITY_LINES = re.compile(r'^(?:guid|timeCreated): .*\n', re.M)


def texture_users(assets: Path):
    """{YAML file: the texture GUIDs it names (as a texture or its sprite)}, read once per file version."""
    return scan(
        'texture-users',
        assets,
        lambda relative: relative.suffix in YAML_FILES and relative.parts[0] != 'Mesh',
        lambda path: sorted({g.decode() for g in TEXTURE_USE.findall(path.read_bytes())}),
        version=TEXTURE_USE.pattern.decode(),
    )


def resident_guids(assets: Path):
    """The GUIDs the player prefab (ADDRESSABLE_ASSETS, loaded all the time) reaches through native assets."""
    references = scan(
        'guid-references',
        assets,
        lambda relative: relative.suffix in NATIVE_ASSETS and relative.parts[0] != 'Mesh',
        lambda path: sorted({g.decode() for g in ANY_GUID.findall(path.read_bytes())}),
        version=ANY_GUID.pattern.decode(),
    )
    paths = scan(
        'asset-guids',
        assets,
        lambda relative: relative.suffix == '.meta' and relative.parts[0] != 'Mesh',
        meta_guid,
    )
    by_guid = {guid: meta.with_suffix('') for meta, guid in paths.items() if guid}
    seen, todo = set(), [root.relative_to('Assets') for root in ADDRESSABLE_ASSETS.values()]
    while todo:
        for guid in references.get(todo.pop(), []):
            if guid not in seen:
                seen.add(guid)
                if by_guid.get(guid) in references:
                    todo.append(by_guid[guid])
    return seen


def texture_dedup_overlay(exported: Path, overlay: dict):
    """Returns ({a copy's GUID: the GUID references name instead}, overlay entries for the native
    assets that named a copy). Scenes are rewritten by scene_dedup_overlay."""
    assets = exported / 'Assets'
    content = scan(
        'texture-content',
        assets,
        lambda relative: relative.parts[0] == 'Texture2D' and relative.suffix == '.png',
        lambda path: hashlib.sha1(path.read_bytes()).hexdigest(),
    )
    groups = {}
    for found, image in content.items():
        relative = Path('Assets') / found
        if relative in overlay:  # npot_texture_overlay's padded images
            image = hashlib.sha1(current_bytes(exported, overlay, relative)).hexdigest()
        meta = current_text(exported, overlay, meta_of(relative))
        if 'TextureImporter:' not in meta or re.search(r'^  isReadable: 1$', meta, re.M):
            continue
        guid = require(re.search(r'^guid: (\w+)', meta, re.M), meta_of(relative), 'guid')[1]
        settings = hashlib.sha1(META_IDENTITY_LINES.sub('', meta).encode()).hexdigest()
        groups.setdefault((image, settings), []).append(guid)
    resident = resident_guids(assets)
    duplicates = {}
    for members in groups.values():
        kept = min((guid not in resident, guid) for guid in members)[1]
        duplicates.update((guid, kept) for guid in members if guid != kept)
    result, references = {}, 0
    for found, guids in texture_users(assets).items():
        if found.suffix == '.unity' or not any(guid in duplicates for guid in guids):
            continue
        relative = Path('Assets') / found
        new, count = replace_references(current_bytes(exported, overlay, relative), duplicates)
        if count:
            result[relative] = new
            references += count
    kept_resident = sum(1 for guid in set(duplicates.values()) if guid in resident)
    log(
        f'Textures: {len(duplicates)} copies replaced by one texture each ({kept_resident} of them the player '
        f"prefab's), {references} references in {len(result)} assets"
    )
    return duplicates, result


DEDUPLICATED_REFERENCE = re.compile(rb'fileID: (4300000|2800000|21300000), guid: ([0-9a-f]{32})')


def replace_references(data: bytes, duplicates: dict):
    """data with its mesh and texture references to copies naming their group's asset, and the count."""
    count = 0

    def kept(m):
        nonlocal count
        guid = duplicates.get(m[2].decode())
        if guid is None:
            return m[0]
        count += 1
        return b'fileID: ' + m[1] + b', guid: ' + guid.encode()

    return DEDUPLICATED_REFERENCE.sub(kept, data), count


def scene_dedup_overlay(exported: Path, overlay: dict, duplicates: dict, platform: str):
    """The scenes with their references to mesh and texture copies changed to the group's asset
    (mesh_duplicates, texture_dedup_overlay). The rewritten scenes (1 GB) go into MESH_DEDUP_CACHE/<platform>,
    current while their stamp (the source's identity and the replacements) matches, and the overlay
    names those files."""
    if not duplicates:
        return {}
    assets = exported / 'Assets'
    files = listing(assets)
    textures = texture_users(assets)
    result, references = {}, 0
    for found, users in scene_mesh_scan(assets).items():
        named = {guid for guids in users for guid in guids} | set(textures.get(found, []))
        replaced = sorted(guid for guid in named if guid in duplicates)
        if not replaced:
            continue
        relative = Path('Assets') / found
        source = overlay.get(relative)  # a file's identity, without reading it when it is one
        if isinstance(source, Path):
            stat = source.stat()
            identity = f'{source} {stat.st_size} {stat.st_mtime_ns}'
        elif source is not None:
            identity = hashlib.sha1(source).hexdigest()
        else:
            identity = f'{files[found][0]} {files[found][1]}'
        stamp = hashlib.sha1(f'{identity} {[(g, duplicates[g]) for g in replaced]}'.encode()).hexdigest()
        cached = MESH_DEDUP_CACHE / platform / relative
        stamp_file = cached.with_name(cached.name + '.stamp')
        if cached.exists() and stamp_file.exists() and stamp_file.read_text() == stamp:
            result[relative] = cached
            continue
        new, count = replace_references(current_bytes(exported, overlay, relative), duplicates)
        references += count
        cached.parent.mkdir(parents=True, exist_ok=True)
        cached.write_bytes(new)
        stamp_file.write_text(stamp)
        result[relative] = cached
    log(
        f'Copies of meshes and textures replaced in {len(result)} scenes '
        f'({references} references rewritten this run, the rest as cached)'
    )
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
        if target in moved:  # two listed assets of one name: which one moved would follow the listing's order
            sys.exit(f'I2 Localization: {relative} and another asset are both named {name!r}; move one by hand')
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
    expect(len(dropped), 41, 'I2 Localization CJK font assets and materials loaded on demand')
    return moved, gone


# ProBuilder's ProBuilderMesh components (839 in the player prefab, 10 MB of managed heap) can't be
# left out of the project: in the export, 842 of the prefab's MeshFilters have no mesh, because
# ProBuilder builds those meshes at run time from its own copy. The PS Vita build builds them with
# ProBuilder's own code as it writes the player's scene and drops the components there
# (unity/ProBuilderMeshes.cs).

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
    exported file's Path, not its bytes.

    A name that can't be a file name ("Legacy Shaders/Diffuse": 45 materials, a mesh, TextMeshPro/Sprite)
    becomes the file's name in the asset, as the editor renames such an object when it loads it. The editor
    then also marked it changed and wrote it back at the build's first SaveAssets (DowngradeBuild's
    SaveMaraScene), and the next project stage restored it: so the build named it after its file when the
    editor had imported it in the same session, and kept the slashed name otherwise. The merged meshes
    are named after their materials, so builds differed by their editor's history (27 Oct, DETERMINISM)."""
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
            if path.suffix in NATIVE_ASSETS:
                relative = path.relative_to(exported)
                text = current_text(exported, overlay, relative)
                name_line = '  m_Name: ' + path.stem
                text, count = re.subn(r'^  m_Name: .*$', lambda _: name_line, text, count=1, flags=re.M)  # noqa: B023
                if count:
                    moved[relative] = text.encode('utf-8')
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
        + (f'; {skipped} names cannot be file names, and now match their files' if skipped else '')
    )
    return moved, excluded


# Assets the game loads through Addressables by key (its catalog, StreamingAssets/aa): AssetRipper
# exports the bundle's assets at the top of Assets. The game's catalog has one, the player prefab, which
# the build script saves in a scene of its own, CrowMara (DowngradeBuild.SaveMaraScene), from the list in
# ADDRESSABLES_LIST, in ProjectSettings: outside Assets, so the editor doesn't import it. CrowCompat's
# Addressables (compat/Addressables.cs) loads other keys from Resources/Addressables. resident_guids
# starts from these assets.
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
    log(f'Listed {len(ADDRESSABLE_ASSETS)} Addressables assets for the build script; left out {catalog}')
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


# The export writes the game's productGUID as Unity 2022 does, a mapping of its four 32-bit words, which 2018.2
# doesn't read: the editor then made a new random one at every build, into the project's settings and the
# player's PlayerSettings, so no two builds were the same. Written in 2018's form, 32 hex digits, each word's
# from its lowest four bits up.
PRODUCT_GUID = re.compile(r'^  productGUID:\n((?:    data\[\d\]: \d+\n){4})', re.M)


def product_guid(text: str):
    match = PRODUCT_GUID.search(text)
    if not match:
        sys.exit('ProjectSettings has no productGUID in the form the export writes (four data[] words)')
    words = [int(word) for word in re.findall(r': (\d+)', match[1])]
    digits = ''.join(f'{(word >> (4 * i)) & 15:x}' for word in words for i in range(8))
    return text[: match.start()] + f'  productGUID: {digits}\n' + text[match.end() :]


def settings_overlay(exported: Path, platform: str):
    relative = Path('ProjectSettings') / 'ProjectSettings.asset'
    text = (exported / relative).read_text(encoding='utf-8')
    text = product_guid(text)
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


# CrowVita/Lit (vita_shaders/CrowLit.shader, compat/VitaLit.cs): Standard with every light in its
# base pass, which CrowCompat swaps in at run time. It goes in Resources (Shader.Find finds it), with
# a material for each set of its keywords the game's Standard materials use, so that the build keeps
# those variants (it drops the shader_feature variants no material uses).
# The PS Vita project also has the materials saved with it (CROW_LIT_BAKE=on, the default; off keeps
# the game's shaders and leaves the swap to run time): every Standard, SFBStandard and Legacy
# Shaders/Diffuse material (VitaLit's set) gets CrowVita/Lit as its shader, its render queue as it
# was (m_CustomRenderQueue is kept; -1 is Geometry for all four shaders) and _CrowLitFrom (1, 2, 3)
# naming its old shader; a Legacy Diffuse one gets VitaLit.LegacyDiffuse's changes (its colour over
# Standard's dielectric diffuse factor, the old colour in _CrowLitColor, smoothness and metallic 0, no
# highlights or reflections). Run-time swapping took 150-240 ms in each area load's last frame and
# 30-60 ms three times after it on the PS Vita.
VITA_SHADERS = Path(__file__).resolve().parent / 'vita_shaders'
VITA_LIT_FOLDER = Path('Assets') / 'Resources' / 'CrowVita'
VITA_LIT_KEYWORDS = {
    '_ALPHATEST_ON',
    '_ALPHABLEND_ON',
    '_ALPHAPREMULTIPLY_ON',
    '_EMISSION',
    '_METALLICGLOSSMAP',
    '_DETAIL_MULX2',
    '_SPECULARHIGHLIGHTS_OFF',
    '_GLOSSYREFLECTIONS_OFF',
}
VITA_LIT_MATERIAL = """%YAML 1.1
%TAG !u! tag:unity3d.com,2011:
--- !u!21 &2100000
Material:
  serializedVersion: 6
  m_ObjectHideFlags: 0
  m_CorrespondingSourceObject: {{fileID: 0}}
  m_PrefabInternal: {{fileID: 0}}
  m_Name: {name}
  m_Shader: {{fileID: 4800000, guid: {guid}, type: 3}}
  m_ShaderKeywords: {keywords}
  m_LightmapFlags: 4
  m_EnableInstancingVariants: 0
  m_DoubleSidedGI: 0
  m_CustomRenderQueue: -1
  stringTagMap: {{}}
  disabledShaderPasses: []
  m_SavedProperties:
    serializedVersion: 3
    m_TexEnvs: []
    m_Floats: []
    m_Colors: []
"""


VITA_LIT_FROM = {BUILTIN_STANDARD: 1, BUILTIN_LEGACY_DIFFUSE: 3}  # and SFBStandard 2
DIELECTRIC_DIFFUSE = 1 - 0.220916301  # Standard's diffuse factor for dielectrics, gamma space

# Depth layers (vita_shaders/CrowLit.shader's CrowDepthLayerZ): CrowVita/Lit materials whose depth the PS
# Vita build moves by a layer, 5.85 mm at 20 m: -1 away from the camera (walls and floors that things lie
# on), +1 toward it (decals). The PS Vita's depth test can't separate surfaces laid less than about a
# millimetre over others at the game's camera distances where Windows does, and resolves faces drawn twice
# with the same vertices differently in blocks of the screen. The list comes from a survey of every scene's
# near-coplanar overlapping and coincident faces (work/scratch/zfight2): per material the layer that settles
# the most of the pairs under 1 mm (by area and colour contrast) while turning the fewest within a layer's
# reach the wrong way; a material is all or nothing, so a few pairs remain. Pairs of Fade or Transparent
# materials (_ZWrite 0) are left out: they never depth-test each other. Matched by material name (each must
# match a material saved with CrowVita/Lit); `-crow-lit-debug 1` switches the layers off at run time.
DEPTH_LAYERS = {
    -1: [
        'break room floor', 'cell floor 1', 'concrete floor 2', 'door metal 3', 'handgun ammo card 1',
        'lab floor 1', 'leaf floor', 'liminal walls', 'main wall', 'mega byte magazine 2', 'metal trash can 2',
        'parking concrete 1', 'road sign 1', 'shotgun case 2', 'shower 2', 'side wall 1', 'soda cup 2',
        'soil', 'stone 1', 'toy shop carpet', 'white painted wall',
    ],
    1: [
        'concrete floor 12', 'concrete gradient', 'concrete gradient 1', 'grass 4', 'handgun ammo box',
        'soil circle', 'text', 'wallpaper gap 1', 'wallpaper gap 2', 'wallpaper gap 3', 'wallpaper gap 4',
        'wallpaper gap 5', 'wallpaper gap 6', 'wallpaper gap 7', 'wallpaper gap 8', 'window blood',
    ],
}


# The layer a CrowVita/Lit material is drawn on (vita_shaders/CrowLit.shader's _CrowLayerLo/Hi), so that a key light
# whose culling mask leaves the layer out doesn't light it, as the game's light doesn't (each Arcade cabinet's point light
# 8 cm in front of its screen lights the cabinet and leaves out the screen's layer, 28; as a key light it washed the screen
# out to white). From the enabled renderers of every scene and prefab, those switched off at the load as well; a material
# drawn on several layers keeps Default's (every key lights it). compat/VitaLit.cs gives each key's mask.
# CrowVita/Particles Lit tests the masks too: the guns' lights leave out Particles, the layer of the Crypt's dust.
LAYER_DOCUMENT = re.compile(rb'^--- !u!(\d+) &(-?\d+)[^\n]*\n', re.M)
GAME_OBJECT_LAYER = re.compile(rb'^  m_Layer: (\d+)$', re.M)
COMPONENT_OWNER = re.compile(rb'^  m_GameObject: \{fileID: (-?\d+)\}', re.M)
COMPONENT_ENABLED = re.compile(rb'^  m_Enabled: (\d)', re.M)
RENDERER_MATERIALS = re.compile(rb'^  m_Materials:\n((?:  - \{[^\n]*\}\n)*)', re.M)
MATERIAL_GUID = re.compile(rb'guid: ([0-9a-f]{32})')


def material_layers(exported: Path, overlay: dict):
    """Material GUID -> the layers of the enabled renderers that draw it."""
    layers = {}
    files = sorted((exported / 'Assets').rglob('*.unity')) + sorted((exported / 'Assets').rglob('*.prefab'))
    for path in files:
        data = current_bytes(exported, overlay, path.relative_to(exported))
        heads = list(LAYER_DOCUMENT.finditer(data))
        owners, renderers = {}, []
        for index, head in enumerate(heads):
            kind = head.group(1)
            # GameObject, MeshRenderer, SkinnedMeshRenderer, ParticleSystemRenderer, LineRenderer, TrailRenderer
            if kind not in (b'1', b'23', b'137', b'199', b'120', b'96'):
                continue
            body = data[head.end() : heads[index + 1].start() if index + 1 < len(heads) else len(data)]
            if kind == b'1':
                layer = GAME_OBJECT_LAYER.search(body)
                if layer:
                    owners[head.group(2)] = int(layer.group(1))
                continue
            enabled, owner = COMPONENT_ENABLED.search(body), COMPONENT_OWNER.search(body)
            materials = RENDERER_MATERIALS.search(body)
            if enabled and enabled.group(1) == b'1' and owner and materials:
                renderers.append((owner.group(1), MATERIAL_GUID.findall(materials.group(1))))
        for owner, guids in renderers:
            layer = owners.get(owner)
            if layer is not None:
                for guid in guids:
                    layers.setdefault(guid.decode(), set()).add(layer)
    return layers


def layer_floats(drawn: set):
    """_CrowLayerLo/Hi for a material drawn on one layer other than Default (material_layers); none otherwise."""
    if len(drawn) != 1 or 0 in drawn:
        return {}
    layer = next(iter(drawn))
    return {
        '_CrowLayerLo': repr(2.0**-layer) if layer < 16 else '0',
        '_CrowLayerHi': repr(2.0 ** (16 - layer)) if layer >= 16 else '0',
    }


def vita_lit_bake(exported: Path, overlay: dict, lit_guid: str, drawn_on: dict):
    """The PS Vita project's Standard, SFBStandard and Legacy Diffuse materials saved with CrowVita/Lit,
    with their DEPTH_LAYERS and the layer they are drawn on (material_layers)."""
    layers = {name: layer for layer, names in DEPTH_LAYERS.items() for name in names}
    on_layer = 0
    layered = {}
    shaders = dict(VITA_LIT_FROM)
    sfb = exported / 'Assets' / 'Shader' / 'SFBStandard.shader.meta'
    if sfb.exists():
        shaders[f'{{fileID: 4800000, guid: {meta_guid(sfb)}, type: 3}}'] = 2
    result, counts = {}, [0, 0, 0, 0]
    for path in (exported / 'Assets').rglob('*.mat'):
        relative = path.relative_to(exported)
        text = current_text(exported, overlay, relative)
        found = SHADER_LINE.search(text)
        kind = shaders.get(found.group(1)) if found else None
        if kind is None:
            continue
        text = text[: found.start(1)] + f'{{fileID: 4800000, guid: {lit_guid}, type: 3}}' + text[found.end(1) :]
        floats = {'_CrowLitFrom': '1' if kind == 1 else '2' if kind == 2 else '3'}
        colors = {}
        if kind == 3:
            # Its colour, which the shader's default (white) would replace without a word.
            color = require(re.search(rf'^      _Color: {VECTOR}$', text, re.M), relative, 'readable _Color')
            r, g, b, a = (float(v) for v in color.groups())
            colors['_CrowLitColor'] = f'{{r: {r!r}, g: {g!r}, b: {b!r}, a: {a!r}}}'
            d = DIELECTRIC_DIFFUSE
            colors['_Color'] = f'{{r: {r / d!r}, g: {g / d!r}, b: {b / d!r}, a: {a!r}}}'
            floats.update(_Glossiness='0', _Metallic='0')
            keywords = require(re.search(r'^  m_ShaderKeywords:(.*)$', text, re.M), relative, 'm_ShaderKeywords')
            words = set(keywords.group(1).split()) | {'_GLOSSYREFLECTIONS_OFF', '_SPECULARHIGHLIGHTS_OFF'}
            text = text[: keywords.start(1)] + ' ' + ' '.join(sorted(words)) + text[keywords.end(1) :]
        name = NAME_LINE.search(text)
        if name and name.group(1) in layers:
            floats['_CrowDepthLayer'] = str(layers[name.group(1)])
            layered[name.group(1)] = layered.get(name.group(1), 0) + 1
        layer = layer_floats(drawn_on.get(meta_guid(exported / meta_of(relative)), set()))
        floats.update(layer)
        on_layer += bool(layer)
        text = set_properties(text, 'm_Floats', floats)
        text = set_properties(text, 'm_Colors', colors)
        result[relative] = text.encode('utf-8')
        counts[kind] += 1
    missing = sorted(set(layers) - set(layered))
    if missing:
        sys.exit(f'DEPTH_LAYERS names no CrowVita/Lit material: {missing}')
    log(
        f'CrowVita/Lit saved into {sum(counts)} materials: {counts[1]} Standard, {counts[2]} SFBStandard, '
        f'{counts[3]} Legacy Diffuse; depth layers in {sum(layered.values())} of them, {on_layer} on one layer other than Default'
    )
    return result


def set_properties(text: str, block: str, values: dict):
    """Sets m_Floats/m_Colors entries (the mapping form of serializedVersion 3), adding the missing."""
    if not values:
        return text
    empty = re.search(rf'^    {block}: (\{{\}}|\[\])$', text, re.M)
    if empty:
        lines = ''.join(f'\n      {k}: {v}' for k, v in values.items())
        return text[: empty.start()] + f'    {block}:' + lines + text[empty.end() :]
    start = re.search(rf'^    {block}:\n', text, re.M)
    if not start:
        raise RuntimeError(f'no {block} in a material')
    end = start.end()
    while True:
        line = re.match(r'      \S.*\n?', text[end:])
        if not line:
            break
        end += line.end()
    body = text[start.end() : end]
    for key, value in values.items():
        entry = re.search(rf'^      {re.escape(key)}: .*$', body, re.M)
        if entry:
            body = body[: entry.start()] + f'      {key}: {value}' + body[entry.end() :]
        else:
            body = body + ('' if body.endswith('\n') or not body else '\n') + f'      {key}: {value}\n'
    if not text[end:] and body.endswith('\n') and not text[start.end() : end].endswith('\n'):
        body = body[:-1]
    return text[: start.end()] + body + text[end:]


# CrowVita/Particles Lit (vita_shaders/CrowParticles.shader) for Particles/Standard Surface's materials: the editor
# compiles no PS Vita programs of that surface shader, so they (the Crypt's water rings, bubbles, splashes, leaves
# and blobs, the crows' feathers, the enemies' death puffs, Scamper's prints) drew with its fallback, VertexLit:
# opaque quads. The PS Vita project saves them with this one, with the layer they are drawn on (material_layers).
def vita_particle_bake(exported: Path, overlay: dict, particles_guid: str, drawn_on: dict):
    result = {}
    on_layer = 0
    for path in (exported / 'Assets').rglob('*.mat'):
        relative = path.relative_to(exported)
        text = current_text(exported, overlay, relative)
        found = SHADER_LINE.search(text)
        if not found or found.group(1) != BUILTIN_PARTICLES_SURFACE:
            continue
        shader = f'{{fileID: 4800000, guid: {particles_guid}, type: 3}}'
        text = text[: found.start(1)] + shader + text[found.end(1) :]
        layer = layer_floats(drawn_on.get(meta_guid(exported / meta_of(relative)), set()))
        on_layer += bool(layer)
        result[relative] = set_properties(text, 'm_Floats', layer).encode('utf-8')
    expect(len(result), 9, 'Particles/Standard Surface materials saved with CrowVita/Particles Lit')
    log(f'CrowVita/Particles Lit saved into {len(result)} materials, {on_layer} on one layer other than Default')
    return result


def vita_lit_overlay(exported: Path, overlay: dict, platform: str):
    from common import stable_meta

    sets = set()
    for path in (exported / 'Assets').rglob('*.mat'):
        text = current_text(exported, overlay, path.relative_to(exported))
        found = SHADER_LINE.search(text)
        if not found or found.group(1) != BUILTIN_STANDARD:
            continue
        found = re.search(r'^  m_ShaderKeywords:(.*)$', text, re.M)
        sets.add(' '.join(sorted(k for k in (found.group(1).split() if found else []) if k in VITA_LIT_KEYWORDS)))
    shader = VITA_SHADERS / 'CrowLit.shader'
    # Assets/Resources is the export's (Addressables' assets move there).
    result = folder_meta(VITA_LIT_FOLDER)
    result[VITA_LIT_FOLDER / shader.name] = shader.read_bytes()
    name = 'CrowVita/CrowLit.shader'
    result[meta_of(VITA_LIT_FOLDER / shader.name)] = stable_meta('asset', name, 'ShaderImporter').encode()
    guid = guid_for('asset', name)
    # CrowVita/Post, the camera effect's filters in three passes (compat/CamEffect.cs; Shader.Find finds it).
    post = VITA_SHADERS / 'CrowPost.shader'
    result[VITA_LIT_FOLDER / post.name] = post.read_bytes()
    post_meta = stable_meta('asset', 'CrowVita/CrowPost.shader', 'ShaderImporter')
    result[meta_of(VITA_LIT_FOLDER / post.name)] = post_meta.encode()
    drawn_on = material_layers(exported, overlay) if platform == 'vita' else {}
    if platform == 'vita':
        particles = VITA_SHADERS / 'CrowParticles.shader'
        result[VITA_LIT_FOLDER / particles.name] = particles.read_bytes()
        particles_meta = stable_meta('asset', 'CrowVita/CrowParticles.shader', 'ShaderImporter')
        result[meta_of(VITA_LIT_FOLDER / particles.name)] = particles_meta.encode()
        particles_guid = guid_for('asset', 'CrowVita/CrowParticles.shader')
        result.update(vita_particle_bake(exported, overlay, particles_guid, drawn_on))
    for index, keywords in enumerate(sorted(sets)):
        name = f'CrowLitVariant{index}'
        relative = VITA_LIT_FOLDER / 'Variants' / f'{name}.mat'
        result[relative] = VITA_LIT_MATERIAL.format(name=name, guid=guid, keywords=keywords).encode()
        result[meta_of(relative)] = (
            f'fileFormatVersion: 2\nguid: {guid_for("asset", f"CrowVita/{name}.mat")}\n'
            'NativeFormatImporter:\n  externalObjects: {}\n  mainObjectFileID: 2100000\n  userData:\n'
        ).encode()
    result.update(folder_meta(VITA_LIT_FOLDER / 'Variants'))
    log(f'CrowVita/Lit: {len(sets)} keyword sets of the Standard materials')
    if platform == 'vita' and build_switches(platform, ('project',))['CROW_LIT_BAKE'] == 'on':
        result.update(vita_lit_bake(exported, overlay, guid_for('asset', 'CrowVita/CrowLit.shader'), drawn_on))
    return result
