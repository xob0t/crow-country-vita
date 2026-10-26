"""Shaders: AssetRipper exports placeholders; the game's shaders are decompiled from their DX11
programs (USCSandbox, patched for Unity 2022's serialization) and rewritten by the rules here.
pipeline.asset_overlay takes shader_overlay's result."""

import hashlib
import os
import re
import shutil
import subprocess
import sys
from collections import Counter
from pathlib import Path

import progress
from common import (
    EXTERNAL,
    HERE,
    SHADER_NAME,
    SOURCE_VERSION,
    USCSANDBOX_COMMIT,
    USCSANDBOX_SHA256,
    WORK,
    apply_patch,
    editor_data,
    game_data,
    github_source,
    log,
    run,
)

# Shaders the target has built in. Materials that use them reference the built-in copy (AssetRipper
# exports no file for them), so a decompile failure there is no loss: USCSandbox stops at the end of
# Standard's data in sharedassets0.assets.
BUILTIN_SHADERS = {'Standard', 'Skybox/Procedural'}
# The decompiler's patches (patches/uscsandbox-<name>.patch), in the order they apply: the others are
# written against the code swizzle leaves.
PATCHES = ['swizzle', 'staterefs', 'tiers']


def uscsandbox():
    """Build the pinned USCSandbox DX11 shader decompiler, with our patches, using the local .NET 8 SDK."""
    repo = EXTERNAL / 'USCSandbox'
    exe = repo / 'USCSandbox' / 'bin' / 'Release' / 'net8.0' / 'USCSandbox.exe'
    patches = [HERE / 'patches' / f'uscsandbox-{name}.patch' for name in PATCHES]
    unlisted = {p.name for p in (HERE / 'patches').glob('uscsandbox-*.patch')} - {p.name for p in patches}
    if unlisted:
        sys.exit(f'patches missing from shader_rules.PATCHES: {", ".join(sorted(unlisted))}')
    stamp = repo / '.crow-patches'
    digest = hashlib.sha256(USCSANDBOX_COMMIT.encode() + b''.join(p.read_bytes() for p in patches)).hexdigest()
    if exe.exists() and stamp.exists() and stamp.read_text() == digest:
        return exe
    dotnet = EXTERNAL / 'dotnet' / 'dotnet.exe'
    if not dotnet.exists() or not (dotnet.parent / 'sdk').exists():
        sys.exit(f'No .NET 8 SDK in {dotnet.parent}: run tools/setup.py')
    github_source('nesrak1/USCSandbox', USCSANDBOX_COMMIT, USCSANDBOX_SHA256, repo)
    for patch in patches:
        apply_patch(patch, repo)
    # No build servers left running: they would hold the SDK's files.
    run([dotnet, 'build', '-c', 'Release', '--disable-build-servers', repo / 'USCSandbox' / 'USCSandbox.csproj'])
    stamp.write_text(digest)
    return exe


SHADER_LISTING = re.compile(r'^  (.+) \(path id (-?\d+)\)$', re.M)


def decompile_shaders(game: Path):
    """Decompile every DX11 shader in the game's data files, one at a time, so a shader the
    decompiler cannot read stops only itself. Returns {shader name: ShaderLab source}."""
    exe = uscsandbox()
    work = WORK / 'shaders'
    if work.exists():
        shutil.rmtree(work)
    work.mkdir(parents=True)
    shutil.copy2(EXTERNAL / 'USCSandbox' / 'Files' / 'classdata.tpk', work)
    # USCSandbox.exe runs on the .NET 8 runtime of the local SDK, which is not on PATH.
    env = {**os.environ, 'DOTNET_ROOT': str(EXTERNAL / 'dotnet')}
    data = game_data(game)
    files = sorted(data.glob('*.assets'))
    jobs = []
    for assets in files:
        listing = subprocess.run([str(exe), 'null', str(assets)], cwd=work, capture_output=True, text=True, env=env)
        jobs += [(assets, name, path_id) for name, path_id in SHADER_LISTING.findall(listing.stdout)]
    failures = []
    for assets, name, path_id in progress.track(jobs, 'Decompiling shaders'):
        result = subprocess.run(
            [str(exe), 'null', str(assets), path_id, '--version', SOURCE_VERSION],
            cwd=work,
            capture_output=True,
            text=True,
            env=env,
        )
        if result.returncode != 0 and name not in BUILTIN_SHADERS:
            message = [line for line in result.stderr.splitlines() + result.stdout.splitlines() if line.strip()]
            failures.append(f'{name} ({assets.name}): {message[0] if message else f"exit {result.returncode}"}')
    shaders = {}
    for path in (work / 'out').rglob('*.shader'):
        text = path.read_text(encoding='utf-8')
        match = SHADER_NAME.match(text)
        if match:
            shaders[match.group(1)] = text
    if failures:
        sys.exit('USCSandbox failed on: ' + '; '.join(failures))
    log(f'Decompiled {len(shaders)} shaders')
    return shaders


# The types of a uniform's declaration (float, half4, int2x2, ...).
UNIFORM_TYPE = r'(?:float|half|fixed|int|uint|bool)\w*'


def for_each_program(text, function):
    """text with function's result in place of each CGPROGRAM line and body, given as a match: [1] the
    CGPROGRAM line, [2] the body up to its ENDCG."""
    return re.sub(r'(CGPROGRAM\n)(.*?)(?=ENDCG)', function, text, flags=re.S)


def builtin_uniforms(version):
    """Uniforms that the target editor declares in includes every CGPROGRAM gets."""
    includes = editor_data(version) / 'CGIncludes'
    declaration = re.compile(rf'^\s*(?:uniform\s+)?{UNIFORM_TYPE}\s+(\w+)\s*(?:\[[^\]]*\])?\s*;', re.M)
    names = set()
    for name in ('HLSLSupport.cginc', 'UnityShaderVariables.cginc'):
        names.update(declaration.findall((includes / name).read_text(encoding='utf-8')))
    return names


# DX11 bytecode combines comparison masks (all bits set or clear) with bitwise
# and/or; the decompiler writes them as uint casts of the 0/1 floats it uses for
# masks. That truncates a value and-ed with a mask (an alpha becomes 0). The rule
# writes them as float arithmetic, with no integer casts or bitwise operations: and
# is a product (one operand is always a mask), or of two masks a maximum.
MASK_LOGIC = re.compile(r'uint([1-4])\(([^()]+)\) ([&|]) uint\1\(([^()]+)\)')


def mask_logic(text):
    return MASK_LOGIC.sub(lambda m: f'({m[2]}) * ({m[4]})' if m[3] == '&' else f'max({m[2]}, {m[4]})', text)


def strip_builtin_declarations(text, builtins):
    """The decompiler declares every uniform a program reads; drop those Unity already declares."""
    pattern = re.compile(rf'^[ \t]*{UNIFORM_TYPE}\s+(\w+)\s*(?:\[[^\]]*\])?\s*;[^\n]*\n', re.M)
    return pattern.sub(lambda m: '' if m.group(1) in builtins else m.group(0), text)


CONDITION = re.compile(r'^\s*#\s*(if|ifdef|ifndef|elif|else|endif)\b\s*(.*?)\s*(?://.*)?$')
CONDITIONAL_DECLARATION = re.compile(
    rf'^\s*(?:uniform\s+)?({UNIFORM_TYPE})\s+(\w+)\s*;|^\s*UNITY_DECLARE_TEX3D_FLOAT\((\w+)\)'
)


def conditional_uniforms(version):
    """{name: (condition, declaration)} for the uniforms the target's UnityShaderVariables.cginc
    declares only under a condition: the light probe proxy volume's, under
    UNITY_LIGHT_PROBE_PROXY_VOLUME, which is 0 on the PS Vita. The decompiler leaves Unity's own
    uniforms undeclared, so a program that reads one where the condition is false does not compile."""
    path = editor_data(version) / 'CGIncludes' / 'UnityShaderVariables.cginc'
    text = path.read_text(encoding='utf-8')
    # Only names declared once and never a macro: the others have a declaration or a #define in
    # every branch (_WorldSpaceLightPos0 is half4 or float4, _WorldSpaceCameraPos a stereo array).
    macros = set(re.findall(r'^\s*#\s*define\s+(\w+)', text, re.M))
    declared = Counter()
    stack, found, guard = [], {}, True  # the first #ifndef is the include guard
    for line in text.splitlines():
        match = CONDITION.match(line)
        if match:
            kind, expression = match[1], match[2]
            if kind == 'ifndef' and guard:
                guard = False
                stack.append(None)
            elif kind in ('if', 'ifdef', 'ifndef'):
                opening = {'if': f'({expression})', 'ifdef': f'defined({expression})'}
                stack.append([opening.get(kind, f'!defined({expression})')])
            elif kind == 'elif':
                stack[-1].append(f'({expression})')
            elif kind == 'else':
                stack[-1].append('1')
            else:
                stack.pop()
            continue
        declaration = CONDITIONAL_DECLARATION.match(line)
        if declaration:
            declared[declaration[2] or declaration[3]] += 1
        levels = [level for level in stack if level is not None]
        if not declaration or not levels:
            continue
        parts = []
        for level in levels:
            *before, current = level
            parts += [f'!{c}' for c in before] + ([current] if current != '1' else [])
        condition = ' && '.join(parts) or '1'
        if declaration[3]:
            # Unity's own macro: a texture and a separate sampler on DX11, a sampler3D elsewhere.
            found[declaration[3]] = (condition, f'UNITY_DECLARE_TEX3D_FLOAT({declaration[3]});')
        else:
            found[declaration[2]] = (condition, f'{declaration[1]} {declaration[2]};')
    return {name: value for name, value in found.items() if declared[name] == 1 and name not in macros}


def declare_conditional_uniforms(text, conditional):
    """Declares, at the start of each CGPROGRAM that reads them, the conditional uniforms (see
    conditional_uniforms) where Unity's includes do not."""

    def program(match):
        body = match[2]
        used = [name for name in conditional if re.search(rf'\b{name}\b', body)]
        if not used:
            return match[0]
        lines = []
        for name in used:
            condition, declaration = conditional[name]
            lines += [f'#if !({condition})', declaration, '#endif']
        return match[1] + '\n'.join(lines) + '\n' + body

    return for_each_program(text, program)


GLOBAL_DECLARATION = re.compile(
    r'^\s*(?:uniform\s+)?(?:float|half|fixed|int|uint|bool|sampler)\w*\s+(\w+)\s*(?:\[[^\]]*\])?\s*;'
)


def guard_repeated_declarations(text):
    """The decompiler writes a program's vertex variants and then its fragment variants, each block
    with its own declarations, so one variant can compile two declarations of a uniform both stages
    read (SFBStandard's unity_WorldToLight). psp2cgc rejects that. Each global declaration of a name
    the program declares more than once gets a guard of its own, so only the first that the variant
    compiles counts (functions' locals are inside braces and stay as they are)."""

    def program(match):
        body = match[2].split('\n')
        globals_, depth = [], 0
        for index, line in enumerate(body):
            declaration = GLOBAL_DECLARATION.match(line) if depth == 0 else None
            if declaration:
                globals_.append((index, declaration[1]))
            depth += line.count('{') - line.count('}')
        repeated = {name for name in {n for _, n in globals_} if sum(n == name for _, n in globals_) > 1}
        for index, name in reversed(globals_):
            if name in repeated:
                guard = f'CROW_DECLARED_{name}'
                body[index] = f'#ifndef {guard}\n#define {guard}\n{body[index]}\n#endif'
        return match[1] + '\n'.join(body)

    return for_each_program(text, program)


SAMPLER_3D = re.compile(r'^(\s*)(?:sampler3D\w*\s+(\w+)\s*;|UNITY_DECLARE_TEX3D\w*\((\w+)\);).*$', re.M)
NO_3D_SAMPLERS = """#if defined(SHADER_API_PSP2)
#define CROW_TEX3D(s, c) float4(1, 1, 1, 1)
#define CROW_SAMPLE_TEX3D_SAMPLER(t, s, c) float4(0, 0, 0, 0)
#else
#define CROW_TEX3D(s, c) tex3D(s, c)
#define CROW_SAMPLE_TEX3D_SAMPLER(t, s, c) UNITY_SAMPLE_TEX3D_SAMPLER(t, s, c)
#endif"""


def no_3d_samplers_on_psp2(text):
    """psp2cgc has no 3D samplers: a program that declares or reads one does not compile, even where
    the read never runs. Two kinds of read: the light probe proxy volume (UNITY_SAMPLE_TEX3D_SAMPLER,
    behind a branch on unity_ProbeVolumeParams that is never taken where the volume is off, as it is
    on the PS Vita), read as zero there; and _DitherMaskLOD, the dither for semi-transparent shadows
    (Particles/Standard Surface's shadow caster; Unity's own shaders don't use it on the Vita), read as
    one, so those shadows are solid. Other platforms keep the reads."""

    def program(match):
        body = match[2]
        if 'tex3D(' not in body and 'UNITY_SAMPLE_TEX3D_SAMPLER(' not in body and 'sampler3D' not in body:
            return match[0]
        body = body.replace('UNITY_SAMPLE_TEX3D_SAMPLER(', 'CROW_SAMPLE_TEX3D_SAMPLER(')
        body = re.sub(r'\btex3D\(', 'CROW_TEX3D(', body)
        body = SAMPLER_3D.sub(lambda m: f'#if !defined(SHADER_API_PSP2)\n{m[0]}\n#endif', body)
        return match[1] + NO_3D_SAMPLERS + '\n' + body

    return for_each_program(text, program)


# Linear fog. The decompiler writes the fog keyword as a shader_feature, so a build keeps the FOG_LINEAR
# variant only where a material has the keyword on, which no material does: SFB/UnlitVertColourAlpha,
# Unlit/TransparentColor, the legacy particles and ProBuilder's Standard Vertex Color drew with no fog on
# every platform (the game's own build has the variant). FOG_LINEAR is a global keyword that Unity sets
# from the scene's fog, so it becomes a multi_compile (the build still strips it where no scene uses linear
# fog). The fog coordinate came out as DX11 computes it from the clip-space z (reversed z: from near at
# the near plane to 0 at the far one): (1 - z / near) * far. On the PS Vita the clip-space z runs from
# -near to far as in OpenGL, where that is below 0 past about three times the near plane, so a restored
# variant would still draw no fog there. Each such line is rewritten to give UnityCG.cginc's
# UNITY_Z_0_FAR_FROM_CLIPSPACE (copied, as these programs don't include UnityCG.cginc), so the next three
# lines of the program, 1 - x, times far and max(x, 0), end at Unity's fog coordinate for the platform:
# the same value as before on Direct3D, the clip-space z on the PS Vita and OpenGL (as Unity's own shaders
# and CrowVita/Lit have it, which compat/Cameras.cs's NearPlane counts on).
FOG_FEATURE = re.compile(r'^([ \t]*)#pragma shader_feature FOG_LINEAR[ \t]*$', re.M)
FOG_COORDINATE = re.compile(r'^([ \t]*)(\w+\.[xyzw]) = ([\w.]+) / _ProjectionParams\.y;$')
FOG_DEPTH = """#ifndef CROW_Z_0_FAR_FROM_CLIPSPACE
#if defined(UNITY_REVERSED_Z) && UNITY_REVERSED_Z == 1
#define CROW_Z_0_FAR_FROM_CLIPSPACE(coord) max(((1.0 - (coord) / _ProjectionParams.y) * _ProjectionParams.z), 0)
#elif defined(UNITY_REVERSED_Z)
#define CROW_Z_0_FAR_FROM_CLIPSPACE(coord) max(-(coord), 0)
#else
#define CROW_Z_0_FAR_FROM_CLIPSPACE(coord) (coord)
#endif
#endif"""


def linear_fog(name, text):
    """Returns the text with linear fog as above, how many FOG_LINEAR pragmas and fog coordinates changed."""
    text, features = FOG_FEATURE.subn(r'\1#pragma multi_compile __ FOG_LINEAR', text)
    lines = text.split('\n')
    rewritten = 0
    for index, line in enumerate(lines):
        match = FOG_COORDINATE.match(line)
        if not match:
            continue
        indent, target, depth = match[1], match[2], match[3]
        # The decompiled sequence: x = z / near; y = 1.0 - x; y = y * far; y = max(y, 0.0); then y with
        # unity_FogParams. A z / near the rule doesn't know stops the conversion.
        after = '\n'.join(lines[index + 1 : index + 12])
        step = re.search(rf'^\s*(\w+\.[xyzw]) = 1\.0 - {re.escape(target)};$', after, re.M)
        far = step and re.search(rf'^\s*{re.escape(step[1])} = {re.escape(step[1])} \* _ProjectionParams\.z;$', after, re.M)
        if not far or 'unity_FogParams' not in after:
            sys.exit(f'{name}: the z / _ProjectionParams.y in line {index + 1} is not the fog sequence linear_fog knows')
        lines[index] = f'{indent}{target} = 1.0 - CROW_Z_0_FAR_FROM_CLIPSPACE({depth}) * _ProjectionParams.w;'
        rewritten += 1
    text = '\n'.join(lines)
    if '/ _ProjectionParams.y' in text:
        sys.exit(f'{name}: a / _ProjectionParams.y that linear_fog did not rewrite')
    if rewritten and not features:
        sys.exit(f'{name}: fog coordinates without a FOG_LINEAR keyword')

    def program(match):
        if 'CROW_Z_0_FAR_FROM_CLIPSPACE(' not in match[2]:
            return match[0]
        return match[1] + FOG_DEPTH + '\n' + match[2]

    return for_each_program(text, program), features, rewritten


# Instructions the decompiler lost, per shader: (the decompiled text, with the instruction restored).
# - SFB/CC02RedDitherPoster's fragment program indexes a 4x4 ordered dither table by
#   (row << 2 & 12) + (column & 3), from texcoord1, the vertex program's uv times the source size
#   in texels (which nothing else reads). The float-to-uint conversion of texcoord1 that fills tmp0
#   came out missing, so tmp0 was read uninitialized (DX11's compiler rejects it; psp2cgc compiled
#   garbage dither). Row y, column x: if the original took them the other way, the pattern is
#   transposed, which an ordered dither shows the same.
DECOMPILER_FIXES = {
    'SFB/CC02RedDitherPoster': [
        (
            '                tmp0.x = float1(int1(tmp0.x) << 2);\n',
            '                tmp0.xy = floor(inp.texcoord1.yx);\n                tmp0.x = float1(int1(tmp0.x) << 2);\n',
        )
    ]
}


# Vertex inputs the target lays out differently, per shader: (the decompiled text, the target's).
# - Unlit/UIPixelate snaps its image to the screen's 416x234 grid (com.sfbgames UIPixelate): the
#   grid's offset in uv1.xy, the image's size in grid cells in uv1.zw, a Vector4 uv1 SFB's uGUI has.
#   2018.2's UIVertex.uv1 is a Vector2, so CrowCompat's VertexHelper.SetUV1 puts zw in uv2
#   (compat/ApiShims.cs), which the canvases pass (asset_rules.canvas_channel_overlay). Unread, zw
#   were 0 and the shader drew the image unsnapped.
VERTEX_INPUT_FIXES = {
    'Unlit/UIPixelate': [
        (
            '                float4 texcoord1 : TEXCOORD1;\n            };\n            struct v2f\n',
            '                float4 texcoord1 : TEXCOORD1;\n                float2 texcoord2 : TEXCOORD2;\n'
            '            };\n            struct v2f\n',
        ),
        ('o.texcoord1 = v.texcoord1;', 'o.texcoord1 = float4(v.texcoord1.xy, v.texcoord2.xy);'),
    ]
}


def fix_decompiler(name, text):
    for old, new in DECOMPILER_FIXES.get(name, []) + VERTEX_INPUT_FIXES.get(name, []):
        if text.count(old) != 1:
            sys.exit(f'{name}: the decompiled text for a DECOMPILER_FIXES or VERTEX_INPUT_FIXES rule is not there once')
        text = text.replace(old, new)
    return text


def shader_overlay(exported: Path, game: Path, version: str):
    """Replace AssetRipper's dummy shaders with decompiled source, keeping their .meta GUIDs. Shaders
    with the same name in several files (the Addressables bundle's copies) get the same source."""
    decompiled = decompile_shaders(game)
    builtins = builtin_uniforms(version)
    conditional = conditional_uniforms(version)
    overlay, missing, fog = {}, [], {}
    for path in (exported / 'Assets').rglob('*.shader'):
        text = path.read_text(encoding='utf-8', errors='replace')
        match = SHADER_NAME.match(text)
        if not match or 'DummyShaderTextExporter' not in text:
            continue
        name = match.group(1)
        if name not in decompiled:
            missing.append(name)
            continue
        text, features, rewritten = linear_fog(name, fix_decompiler(name, decompiled[name]))
        if features or rewritten:
            fog[name] = f'{name} ({features} programs, {rewritten} fog coordinates)'
        text = mask_logic(strip_builtin_declarations(text, builtins))
        text = declare_conditional_uniforms(guard_repeated_declarations(text), conditional)
        overlay[path.relative_to(exported)] = no_3d_samplers_on_psp2(text).encode()
    if missing:
        sys.exit(f'No decompiled source for the exported shaders {sorted(set(missing))}')
    log(f'Replaced {len(overlay)} dummy shaders')
    log('Linear fog: ' + ', '.join(fog[name] for name in sorted(fog)))
    return overlay
