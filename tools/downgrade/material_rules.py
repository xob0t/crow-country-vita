"""Material shader keywords. Unity 2022 keeps a material's keywords in m_ValidKeywords and
m_InvalidKeywords, and AssetRipper's 2018 export leaves m_ShaderKeywords empty, so every material
would render with its shader's no-keyword variant: Standard without its alpha modes, detail maps or
emission (3,379 of the game's materials are Standard). material_keyword_overlay writes the original
keywords back.

An exported .mat doesn't say which object it came from, so each is matched to the game's materials
by name and property values (floats, colours, which texture slots are set, with their tiling). A
material with no single match gets the keywords Unity's Standard inspector would set from its
properties (standard_keywords); the log counts how often that agrees with the matched ones."""

import hashlib
import json
import re
from pathlib import Path

import progress
from common import WORK, game_data, log

STANDARD = re.compile(r'^  m_Shader: \{fileID: 46, guid: 0000000000000000f000000000000000, type: 0\}$', re.M)
CACHE = WORK / 'material-keywords.json'


def rounded(value):
    return round(float(value), 4)


def key(name, floats, colors, textures):
    """What identifies a material: its name, float and colour properties, and its set texture slots with
    their tiling and offset (texture references themselves differ between the game and the export)."""
    return json.dumps(
        [
            name,
            sorted((k, rounded(v)) for k, v in floats.items()),
            sorted((k, [rounded(c) for c in v]) for k, v in colors.items()),
            sorted((k, [rounded(c) for c in v]) for k, v in textures.items()),
        ]
    )


def original_keywords(game: Path):
    """{key: [keyword sets]} of every material in the game's data files and Addressables bundles,
    cached in work/ while the data files are unchanged."""
    data = game_data(game)
    files = [
        p
        for p in sorted(data.iterdir())
        if p.is_file() and (p.suffix == '.assets' or re.fullmatch(r'level\d+', p.name))
    ]
    files += sorted((data / 'StreamingAssets' / 'aa').rglob('*.bundle'))
    stamp = hashlib.sha256(
        '\n'.join(f'{p.name} {p.stat().st_size} {p.stat().st_mtime_ns}' for p in files).encode()
    ).hexdigest()
    if CACHE.exists():
        cached = json.loads(CACHE.read_text(encoding='utf-8'))
        if cached.get('stamp') == stamp:
            return cached['materials']
    import UnityPy

    materials = {}
    for path in progress.track(files, 'Reading the game materials'):
        for obj in UnityPy.load(str(path)).objects:
            if obj.type.name != 'Material':
                continue
            tree = obj.read_typetree()
            saved = tree['m_SavedProperties']
            floats = dict(saved['m_Floats'])
            colors = {k: [v['r'], v['g'], v['b'], v['a']] for k, v in saved['m_Colors']}
            textures = {
                k: [v['m_Scale']['x'], v['m_Scale']['y'], v['m_Offset']['x'], v['m_Offset']['y']]
                for k, v in saved['m_TexEnvs']
                if v['m_Texture']['m_PathID'] != 0
            }
            keywords = sorted(set(tree.get('m_ValidKeywords', [])) | set(tree.get('m_InvalidKeywords', [])))
            entries = materials.setdefault(key(tree['m_Name'], floats, colors, textures), [])
            if keywords not in entries:
                entries.append(keywords)
    CACHE.parent.mkdir(parents=True, exist_ok=True)
    CACHE.write_text(json.dumps({'stamp': stamp, 'materials': materials}), encoding='utf-8')
    return materials


# A number as the export writes it: the particle materials' _CameraFadeParams is {r: 0, g: Infinity, ...}, and a
# key without it matched nothing, so those Fade particles lost _ALPHABLEND_ON (drawn opaque). float() reads all three.
NUMBER = r'([-\d.e]+|-?Infinity|NaN)'
VECTOR = rf'\{{r: {NUMBER}, g: {NUMBER}, b: {NUMBER}, a: {NUMBER}\}}'


def exported_properties(text):
    """(name, floats, colors, textures) of an exported material's YAML."""
    name = re.search(r'^  m_Name: ?(.*)$', text, re.M)[1]
    floats = {m[1]: float(m[2]) for m in re.finditer(r'^      (\w+): ' + NUMBER + '$', text, re.M)}
    colors = {
        m[1]: [float(m[i]) for i in range(2, 6)] for m in re.finditer(r'^      (\w+): ' + VECTOR + '$', text, re.M)
    }
    textures = {}
    for m in re.finditer(
        r'^      (\w+):\n        m_Texture: \{fileID: (-?\d+)[^\n]*\n'
        r'        m_Scale: \{x: ([-\d.e]+), y: ([-\d.e]+)\}\n'
        r'        m_Offset: \{x: ([-\d.e]+), y: ([-\d.e]+)\}',
        text,
        re.M,
    ):
        if m[2] != '0':
            textures[m[1]] = [float(m[i]) for i in range(3, 7)]
    return name, floats, colors, textures


def standard_keywords(floats, colors, textures):
    """The keywords Unity's Standard shader inspector sets from a material's properties
    (StandardShaderGUI.SetMaterialKeywords)."""
    keywords = set()
    mode = int(floats.get('_Mode', 0))
    keywords |= {1: {'_ALPHATEST_ON'}, 2: {'_ALPHABLEND_ON'}, 3: {'_ALPHAPREMULTIPLY_ON'}}.get(mode, set())
    if '_BumpMap' in textures or '_DetailNormalMap' in textures:
        keywords.add('_NORMALMAP')
    if '_MetallicGlossMap' in textures:
        keywords.add('_METALLICGLOSSMAP')
    if '_ParallaxMap' in textures:
        keywords.add('_PARALLAXMAP')
    if '_DetailAlbedoMap' in textures or '_DetailNormalMap' in textures:
        keywords.add('_DETAIL_MULX2')
    if max(colors.get('_EmissionColor', [0, 0, 0])[:3]) > 0.1 / 255:
        keywords.add('_EMISSION')
    if floats.get('_SpecularHighlights', 1) == 0:
        keywords.add('_SPECULARHIGHLIGHTS_OFF')
    if floats.get('_GlossyReflections', 1) == 0:
        keywords.add('_GLOSSYREFLECTIONS_OFF')
    if floats.get('_SmoothnessTextureChannel', 0) == 1:
        keywords.add('_SMOOTHNESS_TEXTURE_ALBEDO_CHANNEL_A')
    return sorted(keywords)


def material_keyword_overlay(exported: Path, game: Path, overlay: dict):
    originals = original_keywords(game)
    result, matched, derived, unknown, agree, disagree = {}, 0, 0, 0, 0, []
    for path in (exported / 'Assets').rglob('*.mat'):
        relative = path.relative_to(exported)
        text = overlay[relative].decode('utf-8') if relative in overlay else path.read_text(encoding='utf-8')
        name, floats, colors, textures = exported_properties(text)
        candidates = originals.get(key(name, floats, colors, textures), [])
        standard = STANDARD.search(text) is not None
        if len(candidates) == 1:
            keywords = candidates[0]
            matched += 1
            if standard:
                derivation = standard_keywords(floats, colors, textures)
                core = sorted(k for k in keywords if k in STANDARD_KEYWORDS)
                if derivation == core:
                    agree += 1
                else:
                    disagree.append(f'{name}: {core} vs {derivation}')
        elif standard:
            keywords = standard_keywords(floats, colors, textures)
            derived += 1
        else:
            unknown += 1
            continue
        text, count = re.subn(r'^  m_ShaderKeywords:.*$', '  m_ShaderKeywords: ' + ' '.join(keywords), text, flags=re.M)
        if count != 1:
            raise RuntimeError(f'{relative}: {count} m_ShaderKeywords lines')
        result[relative] = text.encode('utf-8')
    log(
        f'Material keywords: {matched} from the game, {derived} Standard ones derived from their properties, '
        f'{unknown} other unmatched kept as exported; the derivation agreed on {agree} matched Standard materials'
        + (f' and differed on {len(disagree)}, e.g. {disagree[:3]}' if disagree else '')
    )
    return result


# The keywords standard_keywords decides; matched materials can also carry others (instancing,
# keywords of another shader they once used), which the comparison leaves out.
STANDARD_KEYWORDS = {
    '_ALPHATEST_ON',
    '_ALPHABLEND_ON',
    '_ALPHAPREMULTIPLY_ON',
    '_NORMALMAP',
    '_METALLICGLOSSMAP',
    '_PARALLAXMAP',
    '_DETAIL_MULX2',
    '_EMISSION',
    '_SPECULARHIGHLIGHTS_OFF',
    '_GLOSSYREFLECTIONS_OFF',
    '_SMOOTHNESS_TEXTURE_ALBEDO_CHANNEL_A',
}
