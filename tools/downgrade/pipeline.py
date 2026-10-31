"""Translate the installed PC build of Crow Country (Unity 2022.3) into a Unity 2018.2 project and a
PS Vita player.

    uv run tools/downgrade/pipeline.py rip

Stages, each runnable alone:
  rip      AssetRipper exports the game as a project serialized for the target version.
  code     Rewrite the game's assemblies for the target (DowngradeAssemblies.cs, CrowCompat) into
           work/assemblies-<version>[-vita], from the game's Managed folder; prints what does not resolve.
  link     Run Unity's linker on the rewritten assemblies (after code), as the editor's IL2CPP build
           would, without the editor: reports in seconds what the build reports after every scene.
  loadcheck  Load every type of the rewritten assemblies and compile every method on the target's Mono
           (needs a Windows player): what Mono 2018 rejects, in seconds.
  project  Assemble work/crow-<version>[-vita]: the export with the rewritten assemblies, decompiled
           shaders over AssetRipper's dummies, the asset rules (asset_rules.py) and the build script.
  build    Import the project and build the player with the Unity editor in batch mode, into
           builds/crow-<version>[-vita].
  package  PS Vita: turn the build into builds/crow-<version>-vita.vpk, with the LiveArea art
           (livearea.py).
  quickcode  PS Vita, after a full build: rewrite the assemblies and rebuild only the IL2CPP module and
           metadata in builds/crow-<version>-vita, replaying the build's linker and IL2CPP steps
           (minutes, not the editor's half hour). For code changes; then vita.py sync.

CROW_* environment variables switch parts of the stages on and off: common.BUILD_SWITCHES lists them with
their values, and docs/build.md describes them.

The game installation is only read. Outputs go to work/, builds/ and artifacts/. The stages after rip
(project, build, package) follow The Drifter's port (github.com/xob0t/the-drifter-vita) as they are
written.
"""

import argparse
import filecmp
import hashlib
import json
import os
import re
import shutil
import socket
import subprocess
import sys
import time
import urllib.parse
import urllib.request
from collections import Counter
from pathlib import Path
from typing import NamedTuple

import progress
from asset_rules import (
    COMPAT_GUID,
    addressables_overlay,
    audio_overlay,
    canvas_channel_overlay,
    compat_component_overlay,
    i2_font_overlay,
    link_xml,
    mesh_duplicates,
    mesh_readable_overlay,
    mesh_hull_overlay,
    mesh_uv_weld_overlay,
    mesh_weld_overlay,
    mipmap_overlay,
    name_overlay,
    native_plugin_overlay,
    npot_texture_overlay,
    render_texture_overlay,
    rewired_vita_overlay,
    scene_dedup_overlay,
    scene_reference_overlay,
    settings_overlay,
    streamed_mesh_overlay,
    texture_cap_overlay,
    texture_dedup_overlay,
    texture_overlay,
    vita_lit_overlay,
)
from code_trim import TRIMMED_ON, trim_assemblies
from common import (
    ARTIFACTS,
    ASSETRIPPER,
    CECIL,
    CSC,
    DEFAULT_GAME,
    HERE,
    TARGET,
    VITA_HOST,
    WORK,
    build_folder,
    build_switches,
    describe_switches,
    editor_data,
    editor_exe,
    file_hash,
    folder_meta,
    game_data,
    log,
    meta_of,
    mono_cecil,
    project_folder,
    replace_path,
    rewritten_folder,
    run,
    stable_meta,
    walk,
)
from material_rules import material_keyword_overlay
from shader_rules import shader_overlay
from video_rules import video_overlay

# ---------------------------------------------------------------- rip

RIP_SETTINGS = {
    'DefaultVersion': '0.0.0a0',
    'BundledAssetsExportMode': 'DirectExport',
    'ScriptContentLevel': 'Level2',
    'AudioExportFormat': 'Default',
    'ImageExportFormat': 'Png',
    'LightmapTextureExportFormat': 'Yaml',
    'SpriteExportMode': 'Yaml',
    'ShaderExportMode': 'Dummy',
    'TextExportMode': 'Parse',
    'ScriptLanguageVersion': 'AutoSafe',
    'ScriptExportMode': 'DllExportWithoutRenaming',
    'PreferOriginalTextureExtension': '',
}
RIP_STAMP = 'crow-rip.txt'
# AssetRipper's server gets STARTUP_POLLS tries, POLL_SECONDS apart, to answer (about a minute);
# loading the game or exporting the project is one request that can take up to REQUEST_SECONDS.
STARTUP_POLLS, POLL_SECONDS = 120, 0.5
REQUEST_SECONDS = 3600
# Its log's line for each asset exported: "ExportProgress : (1810/27794) Exporting 'SneakTalkR'".
EXPORT_COUNT = r'^ExportProgress : \((\d+)/(\d+)\) (?:Exporting )?'


def rip_stamp(game: Path, version: str):
    """What an export depends on: the game's data files (names, sizes, dates), AssetRipper's
    executable and the export settings."""
    exe = ASSETRIPPER
    files = sorted(p for p in game_data(game).rglob('*') if p.is_file())
    lines = [f'{p.relative_to(game).as_posix()} {p.stat().st_size} {p.stat().st_mtime_ns}' for p in files]
    lines.append(f'{exe.name} {exe.stat().st_size} {exe.stat().st_mtime_ns}' if exe.exists() else 'no AssetRipper')
    lines.append(repr(sorted({**RIP_SETTINGS, 'TargetVersion': version}.items())))
    return hashlib.sha256('\n'.join(lines).encode()).hexdigest()


def rip_is_current(game: Path, out: Path, version: str):
    stamp = out / RIP_STAMP
    return stamp.exists() and stamp.read_text(encoding='utf-8').strip() == rip_stamp(game, version)


def rip(game: Path, out: Path, version: str):
    exe = ASSETRIPPER
    stamp = rip_stamp(game, version)
    with socket.socket() as s:
        s.bind(('127.0.0.1', 0))
        port = s.getsockname()[1]
    base = f'http://127.0.0.1:{port}'
    ARTIFACTS.mkdir(parents=True, exist_ok=True)
    with open(ARTIFACTS / 'assetripper.log', 'w', encoding='utf-8') as log_file:
        server = subprocess.Popen(
            [str(exe), '--headless', '--port', str(port), '--log-path', str(ARTIFACTS / 'assetripper-file.log')],
            stdout=log_file,
            stderr=subprocess.STDOUT,
        )
        try:
            for _ in range(STARTUP_POLLS):
                try:
                    urllib.request.urlopen(base + '/', timeout=2)
                    break
                except OSError:
                    time.sleep(POLL_SECONDS)
            else:
                raise RuntimeError('AssetRipper did not start')
            post(base, '/Settings/Update', {**RIP_SETTINGS, 'TargetVersion': version})
            settings = urllib.request.urlopen(base + '/Settings/Edit').read().decode()
            if f'name="TargetVersion" value="{version}"' not in settings:
                raise RuntimeError('AssetRipper rejected the target version setting')
            if out.exists():
                shutil.rmtree(out)
            log(f'Loading {game}')
            with progress.following('AssetRipper: loading', ARTIFACTS / 'assetripper.log'):
                post(base, '/LoadFolder', {'path': str(game)})
            log(f'Exporting to {out}')
            with progress.following('AssetRipper: exporting', ARTIFACTS / 'assetripper.log', pattern=EXPORT_COUNT):
                post(base, '/Export/UnityProject', {'path': str(out)})
            project_version = out / 'ExportedProject' / 'ProjectSettings' / 'ProjectVersion.txt'
            if not project_version.exists():
                raise RuntimeError('AssetRipper export produced no project; see ' + str(ARTIFACTS / 'assetripper.log'))
            log('Exported project: ' + project_version.read_text().strip())
            (out / RIP_STAMP).write_text(stamp + '\n', encoding='utf-8')
        finally:
            server.kill()


def post(base, path, fields):
    """POST a form to AssetRipper's server and wait for it to finish the request."""
    data = urllib.parse.urlencode(fields).encode()
    request = urllib.request.Request(base + path, data=data, method='POST')
    with urllib.request.urlopen(request, timeout=REQUEST_SECONDS) as response:
        response.read()


# ---------------------------------------------------------------- code


# Engine assemblies of each target platform. Vita modules can be empty shells
# (UnityEngine.VideoModule), so the game is rewritten against them; types they
# lack go to the shim like any other missing API.
PLATFORM_ENGINE = {'vita': Path('PlaybackEngines') / 'PSP2Player' / 'Managed'}


class EditorPaths(NamedTuple):
    """The installed editor's folders that the code stages compile, rewrite and link against."""

    data: Path  # Editor/Data
    bcl: Path  # the unityaot class libraries
    engine: Path  # the editor's UnityEngine and its modules
    target_engine: Path  # the platform player's (PLATFORM_ENGINE), else engine
    gui: Path  # uGUI: the editor's UnityEngine.UI.dll, and the player's in Standalone


def editor_paths(version, platform='windows'):
    data = editor_data(version)
    engine = data / 'Managed' / 'UnityEngine'
    return EditorPaths(
        data,
        data / 'MonoBleedingEdge' / 'lib' / 'mono' / 'unityaot',
        engine,
        data / PLATFORM_ENGINE[platform] if platform in PLATFORM_ENGINE else engine,
        data / 'UnityExtensions' / 'Unity' / 'GUISystem',
    )


def engine_library(version, out: Path, sources, modules=(), defines=()):
    """Compile a C# 5 library from source files against the editor's unityaot class libraries,
    UnityEngine's core module and the given UnityEngine modules (e.g. 'AudioModule')."""
    check_parts()
    paths = editor_paths(version)
    bcl, engine = paths.bcl, paths.engine
    compile_csharp(
        [
            CSC,
            '/nologo',
            '/noconfig',
            '/target:library',
            '/nostdlib+',
            '/unsafe',
            *(f'/define:{name}' for name in defines),
            *(f'/r:{bcl / name}' for name in ('mscorlib.dll', 'System.dll', 'System.Core.dll')),
            *(f'/r:{engine / f"UnityEngine.{name}.dll"}' for name in ('CoreModule', *modules)),
            # uGUI, and the UnityEngine facade it references, for the UI members CrowCompat provides.
            f'/r:{engine / "UnityEngine.dll"}',
            f'/r:{paths.gui / "UnityEngine.UI.dll"}',
            f'/out:{out}',
            *sources,
        ]
    )


def compile_csharp(command: list):
    """Run csc, unless its output is there from the same command line, sources and references
    (<output>.stamp): a CrowCompat.dll that changed makes the editor import it, and reload the scripts,
    at the next build. The output is made reproducible (reproducible_assembly)."""
    out = Path(next(str(arg)[len('/out:') :] for arg in command if str(arg).startswith('/out:')))
    digest = hashlib.sha256()
    for arg in map(str, command):
        digest.update(arg.encode() + bytes(1))  # a separator
        path = Path(arg[len('/r:') :] if arg.startswith('/r:') else arg)
        if arg.endswith('.cs'):
            digest.update(path.read_bytes())
        elif arg.startswith('/r:') and path.exists():
            digest.update(f'{path.stat().st_size} {path.stat().st_mtime_ns}'.encode())
    stamp = out.with_name(out.name + '.stamp')
    if out.exists() and stamp.exists() and stamp.read_text() == digest.hexdigest():
        return
    stamp.unlink(missing_ok=True)
    run(command)
    reproducible_assembly(out)
    stamp.write_text(digest.hexdigest())


PRIVATE_DETAILS = b'<PrivateImplementationDetails>{'


def reproducible_assembly(path: Path):
    """The .NET Framework's csc (C# 5) has no /deterministic: each compile of the same sources writes a new
    PE time stamp, module version ID (MVID) and GUID in the name of its <PrivateImplementationDetails>{...}
    class (the only bytes that differed between two compiles of CrowCompat). They are set here from a hash
    of the rest of the file, as Roslyn's /deterministic does, so the same sources give the same bytes."""
    data = bytearray(path.read_bytes())

    def u16(at):
        return int.from_bytes(data[at : at + 2], 'little')

    def u32(at):
        return int.from_bytes(data[at : at + 4], 'little')

    coff = u32(0x3C) + 4
    optional = coff + 20
    directories = optional + (96 if u16(optional) == 0x10B else 112)
    sections = [optional + u16(coff + 16) + 40 * i for i in range(u16(coff + 2))]

    def offset(rva):
        for s in sections:
            if u32(s + 12) <= rva < u32(s + 12) + max(u32(s + 8), u32(s + 16)):
                return rva - u32(s + 12) + u32(s + 20)
        sys.exit(f'{path}: RVA {rva:#x} is in no section')

    cli = offset(u32(directories + 14 * 8))
    metadata = offset(u32(cli + 8))
    at = metadata + 16 + u32(metadata + 12)  # past the version string: flags, then the stream count
    streams, count = {}, u16(at + 2)
    at += 4
    for _ in range(count):
        start, size = u32(at), u32(at + 4)
        end = data.index(0, at + 8)
        streams[data[at + 8 : end].decode()] = (metadata + start, size)
        at = at + 8 + ((end - at - 8) // 4 + 1) * 4  # the name with its 0, padded to 4 bytes
    mvid = streams['#GUID'][0]  # the module's, the heap's first (the only one csc writes)
    strings_at, strings_size = streams['#Strings']
    details = data.find(PRIVATE_DETAILS, strings_at, strings_at + strings_size)
    details = details + len(PRIVATE_DETAILS) if details >= 0 else None
    data[coff + 4 : coff + 8] = bytes(4)
    data[mvid : mvid + 16] = bytes(16)
    if details:
        data[details : details + 36] = b'0' * 36
    digest = hashlib.sha256(data).digest()
    data[coff + 4 : coff + 8] = (int.from_bytes(digest[16:20], 'little') & 0x7FFFFFFF).to_bytes(4, 'little')
    data[mvid : mvid + 16] = digest[:16]
    if details:
        hexits = digest[16:32].hex().upper()
        name = f'{hexits[:8]}-{hexits[8:12]}-{hexits[12:16]}-{hexits[16:20]}-{hexits[20:32]}'
        data[details : details + 36] = name.encode()
    assert len(data) == path.stat().st_size
    path.write_bytes(data)


# The C# sources split into parts, each list in compile order: CrowCompat and the UnityEngine
# modules it uses, and the assembly rewriter.
COMPAT_SOURCES = [
    HERE / 'Compat2018.cs',
    *(
        HERE / 'compat' / f'{name}.cs'
        for name in (
            'ApiShims',
            'TextCore',
            'Addressables',
            'Video',
            'ProbeFiles',
            'Helpers',
            'Diag',
            'CrowEvents',
            'InputProbe',
            'CamEffect',
            'Quality',
            'LoopProfile',
            'Window',
            'Saves',
            'RewiredPlatform',
            'Memory',
            'MeshRelease',
            'Settings',
            'Loading',
            'SceneFlow',
            'RewriterRuntime',
            'ScriptCost',
            'Draws',
            'Merge',
            'MergeSplit',
            'Perf',
            'Particles',
            'SceneSplit',
            'MapPan',
            'Clock',
            'Audio',
            'AudioCensus',
            'Threads',
            'Alloc',
            'IdleAudio',
            'VitaLit',
            'Fight',
            'EnemyCost',
            'ActionProfile',
            'FixedStep',
            'ScaledColliders',
            'EnemySplit',
            'FastPaths',
            'Cameras',
            'UiPark',
            'Decor',
            'Cuts',
            'EnemyRebuild',
            'MergeEnemies',
            'PlayerMerge',
            'FsmTrim',
            'FsmCompile',
            'Pad',
            'Tweens',
        )
    ),
]
COMPAT_MODULES = [
    'ParticleSystemModule',
    'UIModule',
    'AssetBundleModule',
    'TextRenderingModule',
    'AudioModule',
    'VideoModule',
    'ScreenCaptureModule',
    'PhysicsModule',  # compat/Memory.cs: MeshCollider.sharedMesh
    'AnimationModule',  # compat/Decor.cs: Animator, Animation
    'ImageConversionModule',  # compat/PlayerMerge.cs: the player merge's look-check PNGs
]
REWRITER_SOURCES = [
    HERE / 'DowngradeAssemblies.cs',
    *(
        HERE / 'rewriter' / f'{name}.cs'
        for name in (
            'Redirect',
            'Runtime',
            'Patches',
            'ScriptCost',
            'IdleAudio',
            'Alloc',
            'LoadStats',
            'PlayMakerData',
            'ActionProfile',
            'EnemySplit',
            'FastPaths',
            'Decor',
            'Cuts',
            'FsmTrim',
            'FsmCompile',
            'Pad',
            'Tweens',
        )
    ),
]
# Every .cs file in these folders belongs to one of the lists.
PART_FOLDERS = {HERE / 'compat': COMPAT_SOURCES, HERE / 'rewriter': REWRITER_SOURCES}


def check_parts():
    """Stops if a part is missing from its list: the compile would leave it out without a word."""
    unlisted = sorted(
        f'{path.parent.name}/{path.name}'
        for folder, listed in PART_FOLDERS.items()
        for path in folder.glob('*.cs')
        if path not in listed
    )
    if unlisted:
        sys.exit(f'sources missing from COMPAT_SOURCES or REWRITER_SOURCES: {", ".join(unlisted)}')


def compat_path(platform):
    """CrowCompat, compiled per platform."""
    return WORK / 'bin' / platform / 'CrowCompat.dll'


def compile_tools(version, platform):
    bin_dir = WORK / 'bin'
    bin_dir.mkdir(parents=True, exist_ok=True)
    cecil = bin_dir / CECIL.name  # beside the tools, which load it at run time
    if not cecil.exists() or cecil.read_bytes() != mono_cecil().read_bytes():
        shutil.copy2(mono_cecil(), cecil)
    compat = compat_path(platform)
    compat.parent.mkdir(parents=True, exist_ok=True)
    # CROW_VITA: for CrowCompat code that differs on the PS Vita's player.
    engine_library(version, compat, COMPAT_SOURCES, COMPAT_MODULES, ['CROW_VITA'] if platform == 'vita' else [])
    rewriter = bin_dir / 'DowngradeAssemblies.exe'
    compile_csharp([CSC, '/nologo', f'/r:{cecil}', f'/out:{rewriter}', *REWRITER_SOURCES])
    compile_csharp([CSC, '/nologo', f'/r:{cecil}', f'/out:{bin_dir / "StubAssembly.exe"}', HERE / 'StubAssembly.cs'])
    compile_csharp(
        [CSC, '/nologo', f'/r:{cecil}', f'/out:{bin_dir / "SerializedLayout.exe"}', HERE / 'SerializedLayout.cs']
    )
    compile_csharp(
        [CSC, '/nologo', f'/r:{cecil}', f'/out:{bin_dir / "TrimAssemblies.exe"}', HERE / 'TrimAssemblies.cs']
    )
    paths = editor_paths(version, platform)
    return compat, rewriter, [paths.bcl, paths.target_engine, paths.gui]


# Managed wrappers of native libraries a platform lacks; their methods become no-ops
# returning defaults, so the game's own "not available" paths run instead. The Windows players are
# test players: with the real wrappers they signed in to the user's Steam account (achievements,
# play time), so they get the stubs too and never call steam_api or Galaxy.
STUBBED_ON = {
    'vita': ['Facepunch.Steamworks.Win64', 'GalaxyCSharp'],
    'windows': ['Facepunch.Steamworks.Win64', 'GalaxyCSharp'],
}
# The roots: the game's code, and the plugins its assets reference (scenes, prefabs and assets name
# their scripts' assemblies by GUID; counted in the export of the Steam release).
ROOTS = [
    'Assembly-CSharp',
    'Assembly-CSharp-firstpass',
    'PlayMaker',
    'MeshCombineStudio.Runtime',
    'Unity.TextMeshPro',
    'Unity.ProBuilder',
    'Rewired_Core',
    'DOTween',
    # Replaced by CrowCompat's Addressables (compat/Addressables.cs), which loads from Resources.
    '-Unity.Addressables',
    '-Unity.ResourceManager',
]


def rewrite_assemblies(plugins: Path, out: Path, version: str, platform: str, roots=ROOTS):
    """Rewrite the roots in plugins, and what they reference there, into out. Returns the rewriter's
    exit code: 0, or 3 with UNRESOLVED references, 4 with rules that matched nothing."""
    compat, rewriter, references = compile_tools(version, platform)
    if out.exists():
        shutil.rmtree(out)
    log_path = ARTIFACTS / f'rewrite-{version}-{platform}.log'
    ARTIFACTS.mkdir(parents=True, exist_ok=True)
    command = [rewriter, plugins, out, compat, ','.join(roots), *references]
    log(' '.join(str(c) for c in command))
    switches = build_switches(platform, ('code',))
    log('Code switches: ' + describe_switches(switches))
    env = {**os.environ, **switches, 'CROW_API_VERSION': version}
    result = subprocess.run([str(c) for c in command], capture_output=True, text=True, env=env)
    log_path.write_text(result.stdout + result.stderr, encoding='utf-8')
    for name in STUBBED_ON.get(platform, []):
        target = out / f'{name}.dll'
        if target.exists():
            stubbed = target.with_name(target.stem + '.stub.dll')
            run([WORK / 'bin' / 'StubAssembly.exe', target, stubbed])
            stubbed.replace(target)
    problems = [line for line in result.stdout.splitlines() if line.startswith(('UNRESOLVED', 'NO MATCH', 'MISSING'))]
    if result.returncode and not problems:
        sys.exit(f'The rewriter failed (exit code {result.returncode}); see {log_path}')
    if platform in TRIMMED_ON and not result.returncode:
        trim_assemblies(out, version, references)  # code_trim.py: unused PlayMaker actions and more
    log(f'Rewrote {len(list(out.glob("*.dll")))} assemblies; {len(problems)} problems (log: {log_path})')
    return result.returncode


# ---------------------------------------------------------------- link

# IL2CPP's null and array bounds checks, which the PS Vita module leaves out; the build sets the same
# (unity/DowngradeBuild.cs, SetAdditionalIl2CppArgs): keep the two in step.
IL2CPP_CHECKS = ['--emit-null-checks', '--enable-array-bounds-check']


def link_check(version: str, platform: str):
    """Run Unity's linker (UnityLinker, as the editor's IL2CPP build does) on the rewritten assemblies
    with the target's engine and class libraries, outside the editor: it stops at the first type,
    member or attribute that doesn't resolve, which the editor reports only after building every
    scene. Returns the linker's exit code; its output goes to artifacts/downgrade/link-<platform>.log."""
    paths = editor_paths(version, platform)
    linker = paths.data / 'il2cpp' / 'build' / 'UnityLinker.exe'
    rewritten = rewritten_folder(version, platform)
    staging = WORK / f'link-{platform}'
    if staging.exists():
        shutil.rmtree(staging)
    managed = staging / 'Managed'
    managed.mkdir(parents=True)
    bcl = paths.bcl
    for source in [*paths.target_engine.glob('*.dll'), *bcl.glob('*.dll'), *(bcl / 'Facades').glob('*.dll')]:
        shutil.copy2(source, managed / source.name)
    # The player's uGUI; the editor's references UnityEditor.
    shutil.copy2(paths.gui / 'Standalone' / 'UnityEngine.UI.dll', managed)
    roots = [*rewritten.glob('*.dll'), compat_path(platform)]
    for dll in roots:
        shutil.copy2(dll, managed / dll.name)
    link = staging / 'link.xml'
    exported = WORK / f'rip-{version}' / 'ExportedProject'
    link.write_text(link_xml(exported, paths.target_engine, roots, platform), encoding='utf-8')
    whitelists = paths.data / 'PlaybackEngines' / 'PSP2Player' / 'Whitelists' if platform == 'vita' else None
    command = [
        linker,
        f'-out={staging / "out"}',
        *(['-x', whitelists / 'Core.xml'] if whitelists else []),
        '-x',
        link,
        '-d',
        managed,
        *[arg for dll in ['UnityEngine.UI.dll', *(r.name for r in roots)] for arg in ('-a', managed / dll)],
        '--dotnetruntime=il2cpp',
        '--dotnetprofile=unityaot',
    ]
    log_path = ARTIFACTS / f'link-{platform}.log'
    result = subprocess.run([str(c) for c in command], capture_output=True, text=True)
    log_path.write_text(result.stdout + result.stderr, encoding='utf-8')
    lines = [line for line in (result.stdout + result.stderr).splitlines() if line.strip()]
    log(f'UnityLinker exited {result.returncode}' + (': ' + ' | '.join(lines[:3]) if result.returncode else ''))
    if result.returncode or platform != 'vita':
        return result.returncode
    # IL2CPP's conversion to C++ of every method the linker kept (the editor's arguments without the
    # C++ compile): its code generator rejects IL that loads and links (EmptyPointerConstrainedCalls).
    il2cpp = linker.parent / 'il2cpp.exe'
    stripped = staging / 'out'
    converted = sorted(stripped.glob('*.dll'))
    # The class libraries the linker kept whole aren't in its output; IL2CPP resolves them beside it.
    for dll in managed.glob('*.dll'):
        if not (stripped / dll.name).exists():
            shutil.copy2(dll, stripped / dll.name)
    command = [
        il2cpp,
        '--convert-to-cpp',
        '--dotnetprofile=unityjit',
        '--platform=PSP2',
        '--architecture=ARMv7',
        '--configuration=Release',
        f'--plugin={paths.data / "PlaybackEngines" / "PSP2Player" / "Tools" / "PSP2Il2CppPlugin.dll"}',
        *IL2CPP_CHECKS,
        *(f'--assembly={dll}' for dll in converted),
        f'--generatedcppdir={staging / "cpp"}',
    ]
    result = subprocess.run([str(c) for c in command], capture_output=True, text=True)
    (ARTIFACTS / f'il2cpp-{platform}.log').write_text(result.stdout + result.stderr, encoding='utf-8')
    errors = [line for line in (result.stdout + result.stderr).splitlines() if 'error' in line.lower()]
    log(f'IL2CPP conversion exited {result.returncode}' + (': ' + ' | '.join(errors[:3]) if result.returncode else ''))
    return result.returncode


# ---------------------------------------------------------------- loadcheck


def load_check(version: str):
    """Load every type of the game's rewritten assemblies and compile every method on the target's
    Mono (LoadCheck.cs), in a copy of the Windows player's Managed folder with the assemblies from the
    last code stage: a type layout Mono 2018 rejects shows in seconds instead of in the game. Needs a
    Windows player (pipeline.py build). Returns the number of failures."""
    player = build_folder('windows', version=version) / 'CrowCountry_Data' / 'Managed'
    if not player.exists():
        sys.exit(f'No Windows player in {player.parent.parent}: build one first (pipeline.py build --platform windows)')
    staging = WORK / 'loadcheck'
    if staging.exists():
        shutil.rmtree(staging)
    shutil.copytree(player, staging)
    rewritten = [*rewritten_folder(version, 'windows').glob('*.dll'), compat_path('windows')]
    for dll in rewritten:
        shutil.copy2(dll, staging / dll.name)
    tool = WORK / 'bin' / 'LoadCheck.exe'
    tool.parent.mkdir(parents=True, exist_ok=True)
    compile_csharp([CSC, '/nologo', f'/out:{tool}', HERE / 'LoadCheck.cs'])
    exe = shutil.copy2(tool, staging / tool.name)
    mono = editor_data(version) / 'MonoBleedingEdge' / 'bin' / 'mono.exe'
    env = {**os.environ, 'MONO_PATH': str(staging), 'MONO_LOG_LEVEL': 'debug', 'MONO_LOG_MASK': 'type'}
    result = subprocess.run(
        [str(mono), str(exe), str(staging), *(d.name for d in rewritten)], capture_output=True, text=True, env=env
    )
    log_path = ARTIFACTS / 'loadcheck.log'
    log_path.write_text(result.stdout + result.stderr, encoding='utf-8')
    reasons = sorted(
        {
            line
            for line in result.stdout.splitlines()
            if line.startswith('Mono: no implementation') or ('failed' in line.lower() and 'methods failed' not in line)
        }
    )
    failed = 0
    for line in result.stdout.splitlines():
        match = re.search(r'(\d+) methods failed', line)
        if match:
            failed += int(match[1])
            log(line)
    for line in reasons[:20]:
        log('  ' + line)
    if result.returncode:
        log(f'LoadCheck exited {result.returncode}; see {log_path}')
        failed += 1
    return failed


# ---------------------------------------------------------------- project


PLUGIN_META = """fileFormatVersion: 2
guid: {guid}
PluginImporter:
  serializedVersion: 2
  externalObjects: {{}}
  iconMap: {{}}
  executionOrder: {{}}
  isPreloaded: 0
  isOverridable: 0
  platformData:
  - first:
      Any:
    second:
      enabled: 1
      settings: {{}}
  - first:
      Editor: Editor
    second:
      enabled: 0
      settings:
        DefaultValueInitialized: true
  userData:
  assetBundleName:
  assetBundleVariant:
"""


def sync_tree(source: Path, destination: Path, overlay: dict, excluded: set, known: dict | None = None):
    """Mirror source into destination with generated files from overlay replacing or adding paths,
    and excluded source paths left out. An overlay value is the file's bytes, or a Path to copy it from.
    Unchanged files keep their timestamps so Unity's import cache stays valid between runs. A .meta
    file the editor generated (for a folder or file that has none in the source) stays, so its GUID
    doesn't change on every run. known: copy_if_changed's record, kept by the caller between runs."""
    destination.mkdir(parents=True, exist_ok=True)
    present = walk(destination)  # listed once: one stat per file was most of the time
    known = {} if known is None else known
    wanted = set()
    for relative, stat in walk(source).items():
        if relative in excluded or relative in overlay:
            continue
        wanted.add(relative)
        if stat is None:
            if relative not in present:
                (destination / relative).mkdir(parents=True, exist_ok=True)
        elif not same_time(stat, present.get(relative)):
            (destination / relative).parent.mkdir(parents=True, exist_ok=True)
            shutil.copy2(source / relative, destination / relative)
    for relative, content in overlay.items():
        if isinstance(content, Path):
            copy_if_changed(content, destination / relative, known, relative.as_posix())
        elif not (present.get(relative) and present[relative][0] == len(content)):
            write(destination / relative, content)  # new, or another size: no need to read it
        else:
            write_if_changed(destination / relative, content)
        wanted.add(relative)
        wanted.update(relative.parents)
    for relative in sorted(present, reverse=True):
        if relative in wanted or (relative.suffix == '.meta' and relative.with_suffix('') in wanted):
            continue
        path = destination / relative
        shutil.rmtree(path) if present[relative] is None else path.unlink()
    for key in [key for key in known if Path(key) not in overlay]:
        del known[key]


def same_time(stat, other):
    """Whether two walk() entries are files of one size and modification second (copy2 keeps both)."""
    return other is not None and stat[0] == other[0] and stat[1] // 10**9 == other[1] // 10**9


def copy_if_changed(source: Path, target: Path, known: dict | None = None, key: str = ''):
    """Copy source to target unless target has its size and modification time (as sync_tree mirrors),
    or the same bytes. A target with the same bytes keeps its own time, so the editor doesn't import it
    again (projects assembled before sync_tree copied these wrote them with the time of writing), and
    known[key] remembers the pair, so the next run doesn't compare them again."""
    known = {} if known is None else known
    stat = source.stat()
    stat = (stat.st_size, stat.st_mtime_ns)
    other = target.stat() if target.exists() else None
    other = (other.st_size, other.st_mtime_ns) if other else None
    if same_time(stat, other):
        known.pop(key, None)
        return
    if other is not None and other[0] == stat[0]:
        pair = [*stat, other[1]]
        if known.get(key) == pair:
            return
        if filecmp.cmp(source, target, shallow=False):
            known[key] = pair
            return
    target.parent.mkdir(parents=True, exist_ok=True)
    shutil.copy2(source, target)


def write(path: Path, content: bytes):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_bytes(content)


def write_if_changed(path: Path, content: bytes):
    if path.exists() and path.read_bytes() == content:
        return
    write(path, content)


# The renamed game assemblies keep their originals' .meta, so the scripts' references stay valid. The
# rewriter (DowngradeAssemblies.cs) and CrowCompat (Compat2018.cs) name them too: keep them in step.
ASSEMBLY_NAMES = {'Recovered.CrowCountry': 'Assembly-CSharp', 'Recovered.Firstpass': 'Assembly-CSharp-firstpass'}


def assembly_overlay(exported: Path, game: Path, version: str, platform: str):
    """The rewritten game assemblies and the plugins they use, with CrowCompat, in Assets/Plugins.
    Returns (overlay, the exported plugin files they replace, the assemblies).

    The rewriter reads the game's own assemblies (Managed), not AssetRipper's exported copies, which
    differ: the exported Rewired_Core has an interface method the game's lacks, so a class that
    implements the interface failed to load in Mono. The export gives only the .meta files, whose
    GUIDs the assets reference."""
    plugins = exported / 'Assets' / 'Plugins'
    relative = Path('Assets') / 'Plugins'
    excluded, original_meta = set(), {}
    for dll in plugins.glob('*.dll'):
        meta = meta_of(dll)
        original_meta[dll.stem] = meta.read_bytes()
        excluded.update({relative / dll.name, relative / meta.name})
    rewritten = rewritten_folder(version, platform)
    if rewrite_assemblies(game_data(game) / 'Managed', rewritten, version, platform):
        sys.exit(f'The game assemblies do not resolve against {version}; see the rewriter log in {ARTIFACTS}')
    compat = compat_path(platform)
    overlay = {}
    for dll in [*rewritten.glob('*.dll'), compat]:
        if dll == compat:
            meta = PLUGIN_META.format(guid=COMPAT_GUID).encode()
        else:
            source = ASSEMBLY_NAMES.get(dll.stem, dll.stem)
            meta = (
                original_meta.get(source)
                or PLUGIN_META.format(guid=hashlib.md5(dll.stem.encode()).hexdigest()).encode()
            )
        overlay[relative / dll.name] = dll.read_bytes()
        overlay[meta_of(relative / dll.name)] = meta
    log('Plugins: ' + ', '.join(sorted(p.name for p in overlay if p.suffix == '.dll')))
    return overlay, excluded, [*rewritten.glob('*.dll'), compat]


def asset_overlay(exported: Path, game: Path, version: str, platform: str, assemblies: list):
    """The rules for the exported assets and settings (shader_rules.py, asset_rules.py, material_rules.py,
    video_rules.py), in order: a rule reads the files the rules before it changed (asset_rules.current_bytes).
    Returns (overlay, exported paths left out)."""
    overlay, excluded = {}, set()
    overlay.update(shader_overlay(exported, game, version))
    with progress.step('Asset rules'):
        overlay.update(settings_overlay(exported, platform))
        if platform == 'windows':
            overlay.update(native_plugin_overlay(game))
        overlay.update(render_texture_overlay(exported))
        overlay.update(audio_overlay(exported, platform))
        overlay.update(compat_component_overlay(exported, overlay, platform))
        overlay.update(scene_reference_overlay(exported, overlay))
        overlay.update(canvas_channel_overlay(exported, overlay))
        overlay.update(rewired_vita_overlay(exported, overlay, platform))
        overlay.update(material_keyword_overlay(exported, game, overlay))
        overlay.update(vita_lit_overlay(exported, overlay, platform))
        engine = editor_paths(version, platform).target_engine
        overlay[Path('Assets') / 'link.xml'] = link_xml(exported, engine, assemblies, platform).encode()
        overlay[Path('Assets') / 'link.xml.meta'] = stable_meta('asset', 'link.xml', 'TextScriptImporter').encode()
        overlay.update(texture_overlay(exported, game, platform))
        overlay.update(npot_texture_overlay(exported, overlay, platform))
        overlay.update(texture_cap_overlay(exported, overlay, platform))
        overlay.update(mipmap_overlay(exported, overlay, platform))
        overlay.update(streamed_mesh_overlay(exported, game))
        duplicates = mesh_duplicates(exported, overlay)
        overlay.update(mesh_readable_overlay(exported, overlay, platform, duplicates))
        overlay.update(mesh_weld_overlay(exported, overlay, platform))
        overlay.update(mesh_hull_overlay(exported, overlay, platform, duplicates))
        overlay.update(mesh_uv_weld_overlay(exported, overlay, platform, duplicates))
        texture_copies, renamed_references = texture_dedup_overlay(exported, overlay)
        overlay.update(renamed_references)
        moved, renamed = addressables_overlay(exported, overlay)
        overlay.update(moved)
        excluded.update(renamed)
        moved, renamed = name_overlay(exported, game, overlay)
        overlay.update(moved)
        excluded.update(renamed)
        moved, renamed = i2_font_overlay(exported, overlay, excluded)
        overlay.update(moved)
        excluded.update(renamed)
        video_overlay(exported, overlay, excluded, platform)
        overlay.update(scene_dedup_overlay(exported, overlay, {**duplicates, **texture_copies}, platform))
    return overlay, excluded


# The pipeline's editor scripts (unity/*.cs: the build entry point) in the project.
EDITOR_SCRIPTS = Path('Assets') / 'Editor' / 'Downgrade'


def editor_scripts():
    """The pipeline's editor scripts, with fixed GUIDs so reruns don't reimport them."""
    overlay = folder_meta(EDITOR_SCRIPTS)
    for source in (HERE / 'unity').glob('*.cs'):
        overlay[EDITOR_SCRIPTS / source.name] = source.read_bytes()
        overlay[meta_of(EDITOR_SCRIPTS / source.name)] = stable_meta('script', source.name, 'MonoImporter').encode()
    return overlay


SYNC_RECORD = '.crow-sync.json'  # in the project's folder, outside Assets: the editor ignores it
# Also there: the hashes of the rule sources (PROJECT_SOURCES) this project was assembled with and the
# code and project switches it had (project_inputs), which build saves for quickcode_check. Gone while
# assemble runs, so a project it didn't finish has none.
PROJECT_INPUTS = '.crow-inputs.json'


def assemble(rip_dir: Path, project: Path, version: str, game: Path, platform: str):
    inputs = project_inputs(platform)  # as this run reads them
    log('Project switches: ' + describe_switches(build_switches(platform, ('project',))))
    (project / PROJECT_INPUTS).unlink(missing_ok=True)
    exported = rip_dir / 'ExportedProject'
    plugins, plugin_files, assemblies = assembly_overlay(exported, game, version, platform)
    assets, excluded = asset_overlay(exported, game, version, platform, assemblies)
    overlay = {**plugins, **assets, **editor_scripts()}
    excluded |= plugin_files
    log(f'Synchronizing {project}')
    # copy_if_changed's record of copied files whose project copy has the same bytes but its own time.
    record = project / SYNC_RECORD
    try:
        known = json.loads(record.read_text(encoding='utf-8'))
    except (OSError, ValueError):
        known = {}
    with progress.step(f'Synchronizing {project.name}'):
        for part in ('Assets', 'ProjectSettings', 'Packages'):
            sync_tree(
                exported / part,
                project / part,
                {k.relative_to(part): v for k, v in overlay.items() if k.parts[0] == part},
                {k.relative_to(part) for k in excluded if k.parts[0] == part},
                known.setdefault(part, {}),
            )
    record.write_text(json.dumps(known, sort_keys=True), encoding='utf-8')
    (project / PROJECT_INPUTS).write_text(json.dumps(inputs, indent=1), encoding='utf-8')


# ---------------------------------------------------------------- build


def build(
    project: Path,
    version: str,
    output: Path,
    platform: str,
    backend: str = 'mono',
    development: bool = False,
    scenes: list | None = None,
):
    """Import the project and build its player with the Unity editor in batch mode
    (unity/DowngradeBuild.cs). scenes: names of the build settings' scenes to build, for a test build
    in its own folder (main gives it one) that leaves the full build's log and code stage alone; None
    for all."""
    ARTIFACTS.mkdir(parents=True, exist_ok=True)
    unity = editor_exe(version)
    if not unity.exists():
        sys.exit(f'No Unity {version} editor at {unity}')
    if editor_has_open(project):
        sys.exit(f'A Unity editor has {project} open; close it first')
    # A Temp folder left by an editor that crashed or by quickcode: the build script's watcher would
    # take its staged assemblies for this build's (a leftover tempStrip ends its watch at once).
    shutil.rmtree(project / 'Temp', ignore_errors=True)
    warn_stale_plugins(project, version, platform)
    inputs = build_inputs(project, platform)
    # Every build switch goes to the editor with its value (unity/BuildUtil.cs: Switch); unset ones not at all.
    switches = build_switches(platform, ('build',))
    log('Build switches: ' + describe_switches(switches))
    env = {name: value for name, value in os.environ.items() if name not in switches}
    env.update((name, value) for name, value in switches.items() if value)
    variant = ('-il2cpp' if platform == 'windows' and backend == 'il2cpp' else '') + ('-scenes' if scenes else '')
    log_path = ARTIFACTS / f'build-{version}-{platform}{variant}.log'
    # The build entry point must match this script even if only the build stage reruns. Only the
    # scripts: the editor rewrites .meta files it imports, and a rewritten one would import again.
    for relative, content in editor_scripts().items():
        if relative.suffix == '.cs':
            write_if_changed(project / relative, content)
    target = 'PSP2' if platform == 'vita' else 'Win64'
    started = time.time()
    log_path.unlink(missing_ok=True)  # the step would show the last build's last line
    output.parent.mkdir(parents=True, exist_ok=True)  # the build script writes <output>.messages.txt beside it
    player_name = 'PS Vita' if platform == 'vita' else 'Windows'
    with progress.following(f'Unity: building the {player_name} player', log_path):
        result = subprocess.run(
            [
                str(unity),
                '-batchmode',
                '-nographics',
                '-projectPath',
                str(project),
                '-executeMethod',
                'DowngradeBuild.Build',
                '-buildOutput',
                str(output),
                '-scriptingBackend',
                'il2cpp' if platform == 'vita' else backend,
                '-buildTarget',
                target,
                *(['-developmentBuild'] if development else []),
                *(['-codeStage', str(CODE_STAGE)] if platform == 'vita' and not scenes else []),
                *(['-vitaTitleId', VITA_TITLE_ID] if platform == 'vita' else []),
                *(['-crowScenes', ';'.join(scenes)] if scenes else []),
                '-logFile',
                str(log_path),
            ],
            env=env,
        )
    log(f'Unity exited {result.returncode} after {time.time() - started:.0f}s; log {log_path}')
    summarize_log(log_path)
    log_build_steps(log_path)
    if result.returncode != 0:
        sys.exit(result.returncode)
    check_build_output(output, platform, started, log_path)
    reproducible_player_data(output / 'Media' if platform == 'vita' else output.parent / f'{output.stem}_Data')
    # What quickcode checks against, written right after the checks: a build that fails later (a native
    # plugin, Ctrl+C) still leaves it with the code stage the editor saved.
    if platform == 'vita' and not scenes:
        save_code_stage(project, version, log_path, {**inputs, **switch_record(platform)})
    if platform == 'vita':
        native_plugins(output)
        boot_config(output)
    if scenes:
        folder = output if platform == 'vita' else output.parent
        folder.with_name(folder.name + '.txt').write_text('\n'.join(scenes) + '\n', encoding='utf-8')
        log(f'TEST BUILD of the first scene and {", ".join(scenes)} in {folder}')


def check_build_output(output: Path, platform: str, started: float, log_path: Path):
    """Stop when a build the editor reported as a success is not one: Unity can report success after a
    failed IL2CPP step (a failed link leaves the previous build's game module), with errors in its
    report, or after scene steps of the editor scripts failed. A good build has no error."""
    player = next(output.glob('*.self'), None) if platform == 'vita' else (output if output.exists() else None)
    if player is None:
        sys.exit(f'Unity reported success but wrote no player at {output}')
    written = (
        output / 'Media' / 'globalgamemanagers'
        if platform == 'vita'
        else output.parent / f'{output.stem}_Data' / 'globalgamemanagers'
    )
    if not written.exists() or written.stat().st_mtime < started:
        sys.exit(f'Unity reported success but did not write {written}')
    log_text = log_path.read_text(encoding='utf-8', errors='replace')
    if 'BuilderFailedException' in log_text:
        sys.exit(f'Unity reported success but IL2CPP failed; see {log_path}')
    summary = re.findall(r'^DOWNGRADE_BUILD \w+ errors=(\d+)', log_text, re.M)
    if not summary:
        sys.exit(f'The editor log has no DOWNGRADE_BUILD summary; see {log_path}')
    errors = int(summary[-1])
    failed = [line[:SUMMARY_WIDTH] for line in SCENE_STEP_FAILED.findall(log_text)]
    if errors:
        # The build report's errors, which DowngradeBuild writes beside the player.
        messages = output.with_name(output.name + '.messages.txt')
        reported = messages.read_text(encoding='utf-8', errors='replace').splitlines() if messages.exists() else []
        failed += [line[:SUMMARY_WIDTH] for line in reported if line.startswith(('Error\t', 'Exception\t'))]
    if not errors and not failed:
        return
    for line in failed[:20]:
        log(f'  {line}')
    message = (
        f'The build reports {errors} errors, and {len(failed)} lines above say what failed ({log_path}, '
        f'{output.name}.messages.txt beside the player); a good build has none'
    )
    if build_switches(platform, ('check',))['CROW_ALLOW_SCENE_FAILURES'] != '1':
        sys.exit(f'{message}. CROW_ALLOW_SCENE_FAILURES=1 lets it through')
    log(f'WARNING (CROW_ALLOW_SCENE_FAILURES=1): {message}')


# The editor scripts' per-scene steps catch their exceptions, log them and go on, and Unity then
# reports success: a merge or split that threw halfway leaves its scene as far as it got, and an
# area whose MeshCombineStudio bake failed combines at run time again (Station Square -> Pool ran out
# of memory that way). check_build_output prints their failure lines and stops: MergeScenes.cs and
# PlayerMerge.cs write "<step> <scene> failed: <exception>"; an enemy type's or the player's atlas
# (compat/MergeEnemies.cs, compat/PlayerMerge.cs) "CROW_ENEMY_ATLAS scene=<scene> type=<type> failed: <exception>";
# MeshCombineBake.cs (the bake's and the strip's), ProBuilderMeshes.cs and VariantBake.cs a summary with
# failed=N. Any other error (a script without a failure line) counts in DOWNGRADE_BUILD's errors.
SCENE_STEP_FAILED = re.compile(
    r'^DOWNGRADE_(?:SPLIT|MERGE|PLAYER_MERGE|CROW_ENEMY_ATLAS) .* failed: .*$'
    r'|^DOWNGRADE_(?:MCS|PROBUILDER|VARIANT_BAKE) .*\bfailed=[1-9].*$',
    re.M,
)


def save_code_stage(project: Path, version: str, log_path: Path, inputs: dict):
    """Complete the code stage the editor saved (DowngradeBuild.SaveCodeStage) with what quickcode needs
    and checks against: the link XMLs, the serialized layout, and the non-code inputs and switches."""
    keep_linker_inputs(log_path)
    plugins = sorted((project / 'Assets' / 'Plugins').glob('*.dll'))
    (CODE_STAGE / LAYOUT).write_text('\n'.join(serialized_layout(plugins, version)) + '\n', encoding='utf-8')
    (CODE_STAGE / NON_CODE_INPUTS).write_text(json.dumps(inputs, indent=1), encoding='utf-8')


def log_build_steps(log_path: Path):
    """The build report's steps (DowngradeBuild's DOWNGRADE_STEP lines) under Build player, and how long the
    scenes took together."""
    text = log_path.read_text(encoding='utf-8', errors='replace')
    steps = re.findall(r'^DOWNGRADE_STEP ([\d.]+) (\d+) (.*)$', text, re.M)
    if not steps:
        return
    top = [f'{name.strip()} {float(seconds):.0f}s' for seconds, depth, name in steps if depth == '1']
    scenes = [float(seconds) for seconds, _, name in steps if name.startswith('Building scene ')]
    log('Build steps: ' + ', '.join(top) + (f'; {len(scenes)} scenes {sum(scenes):.0f}s' if scenes else ''))


def editor_has_open(project: Path):
    """Whether a Unity editor has the project open: it holds Temp/UnityLockfile open, or runs with the
    project's path (an editor left by a killed pipeline)."""
    lockfile = project / 'Temp' / 'UnityLockfile'
    if lockfile.exists():
        try:
            with lockfile.open('a'):
                pass
        except PermissionError:
            return True
    result = subprocess.run(
        [
            'powershell',
            '-NoProfile',
            '-Command',
            'Get-CimInstance Win32_Process -Filter "Name=\'Unity.exe\'" | ForEach-Object { $_.CommandLine }',
        ],
        capture_output=True,
        text=True,
    )
    opened = (re.search(r'-projectPath\s+("[^"]+"|\S+)', line, re.I) for line in result.stdout.splitlines())
    return any(Path(found[1].strip('"')).resolve() == project.resolve() for found in opened if found)


def warn_stale_plugins(project: Path, version: str, platform: str):
    """Warn when the code stage (or quickcode) rewrote the assemblies after the project stage put them in
    the project: the build would use the project's."""
    rewritten = rewritten_folder(version, platform)
    plugins = project / 'Assets' / 'Plugins'
    differ = [
        dll.name
        for dll in [*rewritten.glob('*.dll'), compat_path(platform)]
        if dll.exists() and not filecmp.cmp(dll, plugins / dll.name, shallow=False)
        if (plugins / dll.name).exists()
    ]
    if differ:
        log(
            f'WARNING: {", ".join(differ)} in {plugins} differ from the last code stage (work/); '
            "this build uses the project's: run pipeline.py project first for the new code"
        )


# What quickcode compares with the last full PS Vita build, saved in the code stage by build: the
# serialized layout of the build's assemblies (SerializedLayout.cs: each field line has its place among
# its type's serialized fields, "<assembly> field <type>.<field> #2 <field type>"), the hashes of the
# non-code inputs of its project (below the tools' folder) and its build switches: the rule sources as
# project read them (PROJECT_SOURCES: the rules, common.py's asset lists, the PS Vita shaders
# vita_lit_overlay copies in, the decompiler's patches), the build script as build wrote it
# (BUILD_SOURCES), and the switches (switch_record).
LAYOUT = 'serialized-layout.txt'
NON_CODE_INPUTS = 'non-code-inputs.json'
PROJECT_SOURCES = ('*_rules.py', 'common.py', 'vita_shaders/*', 'patches/*')
BUILD_SOURCES = ('unity/*.cs',)
NON_CODE_SOURCES = PROJECT_SOURCES + BUILD_SOURCES


def serialized_layout(dlls: list, version: str):
    """SerializedLayout.cs's lines for these assemblies: the types and fields the player's data is
    serialized with, and the [RuntimeInitializeOnLoadMethod] methods the build lists."""
    paths = editor_paths(version, 'vita')
    folders = [paths.bcl, paths.target_engine, paths.gui]
    tool = WORK / 'bin' / 'SerializedLayout.exe'
    compile_csharp([CSC, '/nologo', f'/r:{WORK / "bin" / CECIL.name}', f'/out:{tool}', HERE / 'SerializedLayout.cs'])
    out = WORK / 'bin' / 'serialized-layout.txt'
    run([tool, out, ';'.join(map(str, folders)), *dlls])
    return out.read_text(encoding='utf-8').splitlines()


def non_code_inputs(patterns=NON_CODE_SOURCES):
    """{file: SHA-256} of the tools' sources that shape the project's assets and the build script."""
    files = sorted({path for pattern in patterns for path in HERE.glob(pattern) if path.is_file()})
    return {path.relative_to(HERE).as_posix(): file_hash(path) for path in files}


SWITCH = 'switch '  # a switch's key in the input records


def switch_record(platform, stages=('code', 'project', 'build', 'player')):
    """{'switch <name>': value} of the build switches (common.BUILD_SWITCHES) for the input records: those
    that shape what the stages write."""
    return {SWITCH + name: value for name, value in build_switches(platform, stages).items()}


def project_inputs(platform):
    """What a project is assembled from besides the code and the export: the rule sources, and the code
    and project switches."""
    return {**non_code_inputs(PROJECT_SOURCES), **switch_record(platform, ('code', 'project'))}


def build_inputs(project: Path, platform: str):
    """The non-code inputs of this build: the project's record of its rule sources and switches, and the
    build script's now (build writes it into the project). Warns when they changed since project ran:
    the build has the project as it was."""
    try:
        inputs = json.loads((project / PROJECT_INPUTS).read_text(encoding='utf-8'))
    except (OSError, ValueError):
        log(
            f'WARNING: {project} has no {PROJECT_INPUTS} (project did not finish); '
            "taking today's inputs for the project's"
        )
        inputs = project_inputs(platform)
    changed = changed_inputs(inputs, project_inputs(platform))
    if changed:
        log(
            f'WARNING: {", ".join(changed)} changed since pipeline.py project assembled {project.name}; this build '
            'has the project as it was: run project and build for the change'
        )
    return {**inputs, **non_code_inputs(BUILD_SOURCES)}


def changed_inputs(before: dict, now: dict):
    """The inputs that differ, a switch with its two values."""
    return sorted(
        f'{name}={before.get(name)}->{now.get(name)}' if name.startswith(SWITCH) else name
        for name in before.keys() | now.keys()
        if before.get(name) != now.get(name)
    )


def quickcode_check(dlls: list, version: str, force: bool):
    """Stop (unless force) when the new assemblies serialize differently from the last full build's or
    list other startup methods, which only a full build bakes into the player's data; warn when the
    asset rules or build script changed since that build, which quickcode doesn't apply."""
    if not (CODE_STAGE / LAYOUT).exists():
        # build writes it right after its checks pass, and the editor's build deletes the stage first:
        # the last full build failed or was stopped, and the build may not match its stage.
        message = (
            f'The last full PS Vita build saved no {LAYOUT} in {CODE_STAGE}: it failed or was stopped, so '
            "quickcode can't check the serialized types against it. Run pipeline.py build --platform vita"
        )
        if not force:
            sys.exit(message + ', or quickcode --force to go on unchecked.')
        log('WARNING (--force): ' + message)
    else:
        before = (CODE_STAGE / LAYOUT).read_text(encoding='utf-8').splitlines()
        now = serialized_layout(dlls, version)
        removed, added = sorted(set(before) - set(now)), sorted(set(now) - set(before))
        if removed or added:
            for line in [*(f'  - {line}' for line in removed), *(f'  + {line}' for line in added)][:30]:
                log(line)
            message = (
                f'The serialized types or startup methods changed since the last full build ({len(removed)} lines '
                f'gone, {len(added)} new; all: diff {CODE_STAGE / LAYOUT} {WORK / "bin" / "serialized-layout.txt"}). '
                "The build's scenes and assets were written with the old ones: run project and build"
            )
            if not force:
                sys.exit(message + ', or quickcode --force to try anyway.')
            log('WARNING (--force): ' + message)
    if (CODE_STAGE / NON_CODE_INPUTS).exists():
        before = json.loads((CODE_STAGE / NON_CODE_INPUTS).read_text(encoding='utf-8'))
        # quickcode applies the code and player switches itself.
        applied = {SWITCH + name for name in build_switches('vita', ('code', 'player'))}
        before = {name: value for name, value in before.items() if name not in applied}
        now = {**non_code_inputs(), **switch_record('vita', ('project', 'build'))}
        changed = changed_inputs(before, now)
        if changed:
            log(
                f'WARNING: {", ".join(changed)} changed since the last full build; quickcode updates only the '
                'code, so those changes need project and build'
                + ('' if before.keys() >= now.keys() else ' (an input it did not record counts as changed)')
            )


# ---------------------------------------------------------------- quickcode

# What a PS Vita build leaves for quickcode: the inputs of its UnityLinker and IL2CPP steps, saved by
# the build script (DowngradeBuild.SaveCodeStage) from the project's Temp folder, under their paths
# there, and the linker's generated link XMLs (from the system's temp folder) in linker-xml.
CODE_STAGE = WORK / 'vita-code-stage'
# A link XML's argument, quoted (a path with a space: a Windows account named "John Smith") or not.
LINKER_XML = re.compile(r'-x\s+(?:"([^"]+\.tmp)"|([^"\s]+\.tmp))')


def linker_xmls(arguments: str):
    """The paths of the link XMLs in UnityLinker's argument string."""
    return [quoted or bare for quoted, bare in LINKER_XML.findall(arguments)]


def logged_arguments(log_text: str, tool: str):
    """The argument string of the editor log's last 'Invoking <tool> with arguments:' line."""
    found = re.findall(rf'^Invoking {tool} with arguments: (.*)$', log_text, re.M)
    if not found:
        sys.exit(f'The PS Vita build log has no {tool} command line; build once with pipeline.py build')
    return found[-1]


def keep_linker_inputs(log_path: Path):
    """Copy the link XMLs the editor generated for UnityLinker (in the system's temp folder, which
    gets cleaned) into the code stage."""
    arguments = logged_arguments(log_path.read_text(encoding='utf-8', errors='replace'), 'UnityLinker')
    folder = CODE_STAGE / 'linker-xml'
    folder.mkdir(parents=True, exist_ok=True)
    for path in linker_xmls(arguments):
        shutil.copy2(path, folder / Path(path).name)


def quick_code(version: str, game: Path, force: bool = False):
    """Rebuild the PS Vita player's code without the editor: rewrite the assemblies, then replay the
    last build's UnityLinker and IL2CPP command lines from its log, on its saved inputs with the new
    assemblies, and put the IL2CPP module and metadata into the build. IL2CPP keeps its object cache
    (the project's Library/il2cpp_cache), so only the changed C++ compiles. For code changes only:
    it stops when the serialized types or the startup methods differ from the last full build's, and
    warns when the asset rules or build script changed since (quickcode_check). Then: vita.py sync."""
    project = project_folder(version, 'vita')
    output = build_folder('vita', version=version)
    log_path = ARTIFACTS / f'build-{version}-vita.log'
    if not CODE_STAGE.exists() or not log_path.exists() or not (output / 'Media').exists():
        sys.exit('No saved PS Vita build to update: run pipeline.py build --platform vita once')
    if editor_has_open(project):
        sys.exit(f'A Unity editor has {project} open; close it first')
    if not any((CODE_STAGE / 'StagingArea' / 'Data' / 'Managed').glob('*.dll')):
        sys.exit(f'The last build saved no staged assemblies in {CODE_STAGE}; run pipeline.py build --platform vita')
    log_text = log_path.read_text(encoding='utf-8', errors='replace')
    linker_arguments = logged_arguments(log_text, 'UnityLinker')
    il2cpp_arguments = logged_arguments(log_text, 'il2cpp')
    rewritten = rewritten_folder(version, 'vita')
    log('Player switches: ' + describe_switches(build_switches('vita', ('player',))))
    if rewrite_assemblies(game_data(game) / 'Managed', rewritten, version, 'vita'):
        sys.exit(f'The game assemblies do not resolve against {version}; see the rewriter log in {ARTIFACTS}')
    quickcode_check([*rewritten.glob('*.dll'), compat_path('vita')], version, force)

    # The build's Temp folder as the editor had it, with the new assemblies. It goes again at the end,
    # however quickcode ends: the editor's next build must not find its files.
    temp = project / 'Temp'
    if temp.exists():
        shutil.rmtree(temp)
    try:
        replay_code_steps(project, temp, output, rewritten, version, linker_arguments, il2cpp_arguments)
    finally:
        shutil.rmtree(temp, ignore_errors=True)


def replay_code_steps(project, temp, output, rewritten, version, linker_arguments, il2cpp_arguments):
    shutil.copytree(CODE_STAGE, temp, ignore=shutil.ignore_patterns('linker-xml', LAYOUT, NON_CODE_INPUTS))
    managed = temp / 'StagingArea' / 'Data' / 'Managed'
    for dll in [*rewritten.glob('*.dll'), compat_path('vita')]:
        if not (managed / dll.name).exists():
            sys.exit(f"{dll.name} is not among the last build's assemblies; this change needs a full build")
        shutil.copy2(dll, managed / dll.name)
    for path in linker_xmls(linker_arguments):
        if not Path(path).exists():
            shutil.copy2(CODE_STAGE / 'linker-xml' / Path(path).name, path)

    tools = editor_data(version) / 'il2cpp' / 'build'
    started = time.time()
    with progress.step('UnityLinker'):
        result = subprocess.run(
            f'"{tools / "UnityLinker.exe"}" {linker_arguments}', cwd=project, capture_output=True, text=True
        )
    (ARTIFACTS / 'quickcode-linker.log').write_text(result.stdout + result.stderr, encoding='utf-8')
    if result.returncode:
        sys.exit(f'UnityLinker failed ({result.returncode}); see {ARTIFACTS / "quickcode-linker.log"}')
    # The editor then replaces the staged assemblies with the stripped ones, which IL2CPP reads. An
    # assembly the linker left out goes, as in the editor, and from IL2CPP's list: the last build's
    # listed it if it kept some of it then (Rewired_Windows before code_trim), and IL2CPP would
    # convert the unstripped copy, whose references to stripped code it can't resolve.
    stripped = managed / 'tempStrip'
    kept = {dll.name for dll in stripped.glob('*.dll')}
    for dll in managed.glob('*.dll'):
        if dll.name not in kept:
            dll.unlink()
    for dll in stripped.glob('*.dll'):
        shutil.copy2(dll, managed / dll.name)
    shutil.rmtree(stripped)
    il2cpp_arguments = re.sub(
        r' ?--assembly="([^"]+)"', lambda m: m[0] if Path(m[1]).name in kept else '', il2cpp_arguments
    )
    # The editor runs IL2CPP in the output's folder: its linker flags name ..\..\..\ApplicationInfo.o (in Temp).
    native = temp / 'StagingArea' / 'Data' / 'Native'
    native.mkdir(parents=True, exist_ok=True)
    with progress.step('IL2CPP: C++ and the module'):
        result = subprocess.run(
            f'"{tools / "il2cpp.exe"}" {il2cpp_arguments}', cwd=native, capture_output=True, text=True
        )
    (ARTIFACTS / 'quickcode-il2cpp.log').write_text(result.stdout + result.stderr, encoding='utf-8')
    if result.returncode:
        sys.exit(f'IL2CPP failed ({result.returncode}); see {ARTIFACTS / "quickcode-il2cpp.log"}')

    module = temp / 'StagingArea' / 'Data' / 'Native' / 'Il2CppAssemblies.suprx'
    metadata = next((temp / 'StagingArea').rglob('global-metadata.dat'), None)
    if not module.exists() or metadata is None:
        sys.exit(f'IL2CPP wrote no module or metadata under {temp / "StagingArea"}')
    copy_whole(
        {
            output / 'Media' / 'Modules' / 'il2CppAssemblies.suprx': module,
            output / 'Media' / 'Metadata' / 'global-metadata.dat': metadata,
            output / 'SymbolFiles' / 'il2CppAssemblies.suprx': module,
        }
    )
    native_plugins(output)
    boot_config(output)
    log(f'Updated the code of {output} in {time.time() - started:.0f}s; vita.py sync uploads it')


def copy_whole(files: dict):
    """Copy each source into <target>.part, all of them first, then rename the parts over their targets
    one right after the other (replace_path, which retries): a new module with the old metadata crashes at
    launch, so a quickcode stopped mid-copy leaves the build's files whole and as a set, and says so if a
    rename still fails. vita_app_files never sends a .part a killed quickcode left; the next writes over it."""
    parts = {target: target.with_name(target.name + '.part') for target in files}
    renamed = []
    try:
        for target, source in files.items():
            shutil.copy2(source, parts[target])
        for target, part in parts.items():
            replace_path(part, target)
            renamed.append(target)
    except BaseException:
        for part in parts.values():
            part.unlink(missing_ok=True)
        if renamed:
            log(
                f'ERROR: only {", ".join(t.name for t in renamed)} of {len(parts)} files updated: the build has '
                'new and old code mixed, which crashes at launch; run quickcode again before vita.py sync'
            )
        raise


# The PS Vita native plugins the player loads from Media/Plugins for [DllImport("<name>")]: the module
# and its bootstrap (native/clock/build.py). They go into the build after the editor's (or quickcode's)
# work rather than through Assets/Plugins/PSVita, so quickcode can add or update them too.
NATIVE_PLUGINS = {'clock': ['CrowClock.suprx', 'CrowClock.suprx.b.suprx']}


def native_plugins(output: Path):
    folder = output / 'Media' / 'Plugins'
    folder.mkdir(parents=True, exist_ok=True)
    for name, files in NATIVE_PLUGINS.items():
        script = HERE / 'native' / name / 'build.py'
        if subprocess.run([sys.executable, str(script)]).returncode:
            sys.exit(f'Building the {name} native plugin failed ({script})')
        for file in files:
            shutil.copy2(WORK / 'native' / name / file, folder / file)
    log(f'Native plugins in {folder}: {", ".join(f for files in NATIVE_PLUGINS.values() for f in files)}')


# The PS Vita player's graphics jobs: the 2018.2 editor has no setting for them on the PS Vita, but its
# player reads gfx-enable-gfx-jobs from Media/boot.config at start and then records the camera's draws in
# Unity's job workers beside its render thread (Station Square, main thread: standing 24.9 -> 22.1 ms,
# steady real fight 59.1 -> 55.2). docs/vita.md has their history. CROW_GFX_JOBS=on (the default) writes
# gfx-enable-gfx-jobs=1; off writes it out; build and quickcode run it.
def boot_config(output: Path):
    path = output / 'Media' / 'boot.config'
    if not path.exists():
        sys.exit(f'No {path}: the PS Vita build is incomplete, and its graphics jobs would be off')
    lines = path.read_text(encoding='utf-8').splitlines()
    lines = [line for line in lines if not line.startswith('gfx-enable-gfx-jobs=')]
    jobs = build_switches('vita', ('player',))['CROW_GFX_JOBS'] == 'on'
    if jobs:
        lines.insert(0, 'gfx-enable-gfx-jobs=1')
    path.write_bytes(('\n'.join(lines) + '\n').encode('utf-8'))
    log(f'Graphics jobs {"on" if jobs else "off"} in {path}')


def reproducible_player_data(data: Path):
    """What the editor writes differently into globalgamemanagers on every build of the same project, set
    in place (four objects, each the same size before and after), so two builds of one tree give the same
    bytes there (docs/build.md, "Checking that builds are reproducible"):
    - RuntimeInitializeOnLoadManager: the [RuntimeInitializeOnLoadMethod] methods, which the player runs in
      the order listed. The editor lists them in another order on every build (CrowCompat's AssemblyAliases,
      MainThread and ProbeFiles before or among I2 Localization's 15 AutoRegisters). Sorted here by
      assembly, namespace, class and method name: CrowCompat's run first, each load type in a fixed order.
    - MonoManager's list of the player's scripts (m_Scripts) and ResourceManager's list of the Resources
      assets' dependencies (m_DependentAssets), each in another order on some builds: sorted by object (the
      player looks things up in them; the orders the editor wrote varied and all worked).
    - BuildSettings' buildGUID (Application.buildGUID), new on every build: set from a hash of the
      player's scenes and assets (level*, sharedassets*, resources.assets and this file without it).
    data: the player's data folder (the PS Vita's Media, a Windows player's <name>_Data)."""
    import UnityPy

    path = data / 'globalgamemanagers'
    content = bytearray(path.read_bytes())
    env = UnityPy.load(bytes(content))
    objects = {o.type.name: o for o in env.objects}

    written = {}  # each object's bytes as last written

    def patch(obj, tree):
        start = obj.byte_start
        old = written.setdefault(obj.path_id, obj.get_raw_data())
        if content[start : start + len(old)] != old:
            sys.exit(f'{path}: {obj.type.name} is not where UnityPy reads it')
        new = obj.save_typetree(tree)
        if len(new) != len(old):
            sys.exit(f'{path}: {obj.type.name} would change size ({len(old)} -> {len(new)} bytes)')
        content[start : start + len(new)] = new
        written[obj.path_id] = new

    manager = objects['RuntimeInitializeOnLoadManager']
    tree = manager.read_typetree()
    assemblies, namespaces = tree['m_AssemblyNames'], tree['m_NamespaceNames']
    classes, methods = tree['m_ClassInfos'], tree['m_ClassMethodInfos']
    if tree['m_BeforeUnityMethodExecutionOrders'] or tree['m_AfterUnityMethodExecutionOrders']:
        sys.exit(f'{path}: Unity classes among the RuntimeInitializeOnLoadMethods; sort them too')

    def class_key(c):
        return assemblies[c['m_AssemblyNameIndex']], namespaces[c['m_NamespaceIndex']], c['m_ClassName']

    before = {id(methods[i]) for i in tree['m_BeforeMethodExecutionOrders']}
    after = {id(methods[i]) for i in tree['m_AfterMethodExecutionOrders']}
    new_assemblies, new_namespaces = sorted(assemblies), sorted(namespaces)
    new_classes, new_methods, was = [], [], []
    # Each class's methods follow one another (its m_MethodIndex and m_NumOfMethods): laid out again class by
    # class, and run in that order within each load type.
    for index, c in enumerate(sorted(classes, key=class_key)):
        first = c['m_MethodIndex']
        own = sorted(methods[first : first + c['m_NumOfMethods']], key=lambda m: m['m_MethodName'])
        new_classes.append(
            {
                **c,
                'm_AssemblyNameIndex': new_assemblies.index(assemblies[c['m_AssemblyNameIndex']]),
                'm_NamespaceIndex': new_namespaces.index(namespaces[c['m_NamespaceIndex']]),
                'm_MethodIndex': len(new_methods),
            }
        )
        new_methods += [{**m, 'm_ClassIndex': index} for m in own]
        was += [id(m) for m in own]
    if sorted(was) != sorted(map(id, methods)):
        sys.exit(f'{path}: RuntimeInitializeOnLoadManager lists methods outside their classes; sort it by hand')
    tree.update(
        m_AssemblyNames=new_assemblies,
        m_NamespaceNames=new_namespaces,
        m_ClassInfos=new_classes,
        m_ClassMethodInfos=new_methods,
        m_BeforeMethodExecutionOrders=[i for i, m in enumerate(was) if m in before],
        m_AfterMethodExecutionOrders=[i for i, m in enumerate(was) if m in after],
    )
    patch(manager, tree)

    mono = objects['MonoManager']
    tree = mono.read_typetree()
    tree['m_Scripts'] = sorted(tree['m_Scripts'], key=lambda s: (s['m_FileID'], s['m_PathID']))
    patch(mono, tree)

    resources = objects['ResourceManager']
    tree = resources.read_typetree()
    tree['m_DependentAssets'] = sorted(
        tree['m_DependentAssets'], key=lambda d: (d['m_Object']['m_FileID'], d['m_Object']['m_PathID'])
    )
    patch(resources, tree)

    settings = objects['BuildSettings']
    tree = settings.read_typetree()
    tree['buildGUID'] = dict.fromkeys(tree['buildGUID'], 0)
    patch(settings, tree)
    digest = hashlib.sha256(content)
    for file in sorted(data.iterdir()):
        if file.is_file() and file.name.startswith(('level', 'sharedassets', 'resources.assets')):
            digest.update(file.name.encode() + bytes(1) + file_hash(file).encode())
    words = digest.digest()[:16]
    tree['buildGUID'] = {
        key: int.from_bytes(words[4 * i : 4 * i + 4], 'little') for i, key in enumerate(tree['buildGUID'])
    }
    patch(settings, tree)
    path.write_bytes(content)
    log(f'{path}: RuntimeInitializeOnLoadMethods sorted ({len(new_methods)}), build GUID {words.hex()}')


# Build log lines worth a look; the summary prints each distinct one with its count, the first
# SUMMARY_LINES of them, cut at SUMMARY_WIDTH characters.
LOG_KEYS = re.compile(
    r'DOWNGRADE_BUILD|error CS\d+|Exception|Build Finished|Aborting batchmode|'
    r'Compilation failed|Unable to parse|could not be loaded|is missing'
)
SUMMARY_LINES, SUMMARY_WIDTH = 80, 300


def summarize_log(log_path: Path):
    if not log_path.exists():
        return
    counts = Counter()
    for line in log_path.read_text(encoding='utf-8', errors='replace').splitlines():
        if LOG_KEYS.search(line):
            counts[line.strip()[:SUMMARY_WIDTH]] += 1
    for line, count in list(counts.items())[:SUMMARY_LINES]:
        log(f'{count:5} x {line}')
    if len(counts) > SUMMARY_LINES:
        log(f'... and {len(counts) - SUMMARY_LINES} more distinct lines; see {log_path}')


# ---------------------------------------------------------------- package

VITA_TITLE_ID = 'CROW00001'  # build passes it to DowngradeBuild.cs, which sets the content ID with it
VITA_FTP = (VITA_HOST, 1337)  # VitaShell's or vitacompanion's FTP server
VITA_COMMANDS = (VITA_HOST, 1338)  # vitacompanion's command server


def vita_app_files(build: Path):
    """Files of the installed app for a PC Hosted PS Vita build, as (path in the app, local path or
    bytes): the player becomes eboot.bin with the trial flag cleared, the LiveArea art replaces
    Unity's placeholders, host-only files are left out (The Drifter's port's packaging), and so are the
    .part files of a quickcode killed while it copied (copy_whole): sync and the VPK used to take them."""
    import livearea

    selfs = list(build.glob('*.self'))
    if len(selfs) != 1:
        sys.exit(f'expected one .self in {build}, found {[p.name for p in selfs]}')
    player = bytearray(selfs[0].read_bytes())
    player[0x80] = 0  # trial flag, as the community PSVita Unity Utilities do
    marker = player.find(b'trial.png')
    if marker >= 0:
        player[marker] = 0  # no trial watermark
    patcher_usse(player)
    yield 'eboot.bin', bytes(player)
    art = livearea.generate()
    skip = {'SymbolFiles', 'configuration.psp2path'}
    for path in sorted(build.rglob('*')):
        relative = path.relative_to(build)
        if path.is_dir() or relative.parts[0] in skip or path.suffix in ('.self', '.bat', '.part'):
            continue
        name = relative.as_posix()
        yield name, art / VITA_ARTWORK[name] if name in VITA_ARTWORK else path


# The USSE memory of GXM's shader patcher, which holds every patched vertex and fragment program (the shader
# code for one vertex layout, blend state and output format). Unity 2018.2.19f1's PS Vita player allocates
# a fixed 512 KB at start from g_ShaderPatcherCombinedUsseSize (an initialized global of its data segment;
# no setting or boot.config key reads it), and frees a shader's patched programs only when the shader is
# unloaded. CrowVita/Lit stays loaded all session, so its patched programs pile up over the areas played,
# and when the 512 KB are used up GfxDeviceGXM::CreateFragmentProgram ignores sceGxmShaderPatcherCreate-
# FragmentProgram's error and draws with a pointer it never set: the render thread's crash in
# sceGxmSetFragmentProgram (26 Oct after 2 h 10 min of play, 17 Oct; both dumps show the patcher's USSE
# 97-98% used; work/scratch/fix/CRASH.md). The player is a plain fself (no segment digests), so the
# global's initial value is patched in eboot.bin. Costs the difference in main memory.
VITA_PATCHER_USSE_OFFSET = 0x15D9164  # g_ShaderPatcherCombinedUsseSize in PSP2Player_Il2cpp.self, 2018.2.19f1
VITA_PATCHER_USSE_UNITY = 0x80000  # its value, and that of g_ShaderPatcherBufferSize just before it
VITA_PATCHER_USSE_BYTES = 0x100000


def patcher_usse(player: bytearray):
    """Sets the shader patcher's USSE memory in the player (eboot.bin) to VITA_PATCHER_USSE_BYTES."""
    at = VITA_PATCHER_USSE_OFFSET

    def word(offset):
        return int.from_bytes(player[offset : offset + 4], 'little')

    if word(at - 4) != VITA_PATCHER_USSE_UNITY or word(at) not in (VITA_PATCHER_USSE_UNITY, VITA_PATCHER_USSE_BYTES):
        sys.exit(
            f'The player has {word(at - 4):#x} {word(at):#x} where 2018.2.19f1 keeps the shader patcher\'s '
            f'buffer and USSE sizes ({at - 4:#x}): another player; find g_ShaderPatcherCombinedUsseSize again'
        )
    player[at : at + 4] = VITA_PATCHER_USSE_BYTES.to_bytes(4, 'little')


# LiveArea images from livearea.py, in place of Unity's placeholders.
VITA_ARTWORK = {
    'sce_sys/icon0.png': 'icon0.png',
    'sce_sys/pic0.png': 'pic0.png',
    'sce_sys/livearea/contents/bg0.png': 'bg0.png',
    'sce_sys/livearea/contents/default_gate.png': 'gate.png',
}
PACKAGE_CHUNK = 1 << 22
VPK_DATE = (2024, 1, 1, 0, 0, 0)  # every file's date in the VPK


def package_vita(build: Path) -> Path:
    """Turn a PC Hosted PS Vita build into an installable VPK."""
    import zipfile

    if not any(build.glob('*.self')):
        sys.exit(f'No PS Vita build in {build}; build it first: pipeline.py build --platform vita')
    vpk = build.with_name(build.name + '.vpk')  # with_suffix would cut the version at its dots
    files = list(vita_app_files(build))
    total = sum(len(source) if isinstance(source, bytes) else source.stat().st_size for _, source in files)
    with (
        zipfile.ZipFile(vpk, 'w', zipfile.ZIP_DEFLATED) as archive,
        progress.step('Packaging the VPK', total=total, eta=True, count='MB') as step,
    ):
        for name, source in files:
            step.note(name)
            # The same headers for every file, whatever the time of the build or the files' dates and
            # attributes: two packagings of one build give the same VPK.
            info = zipfile.ZipInfo(name, date_time=VPK_DATE)
            info.compress_type = archive.compression
            info.external_attr = 0o100644 << 16
            if isinstance(source, bytes):
                archive.writestr(info, source)
                step.advance(len(source))
                continue
            with source.open('rb') as src, archive.open(info, 'w') as dest:
                while chunk := src.read(PACKAGE_CHUNK):
                    dest.write(chunk)
                    step.advance(len(chunk))
    log(f'Packaged {vpk} ({vpk.stat().st_size / 2**20:.0f} MB)')
    return vpk


def writes_vita_build(stage, platform):
    """Whether the stage writes builds/crow-<version>-vita (or its VPK), which vita.py's sync, install and
    seed read under vita-build as readers: a PS Vita build, package and quickcode (both PS Vita only)."""
    return stage in ('package', 'quickcode') or (stage == 'build' and platform == 'vita')


def check_lock(stage=None, platform='vita'):
    """Every stage writes in work/, builds/ or artifacts/, which other jobs share: one pipeline.py at a
    time, under lock.py run (which sets CROW_LOCKS). Without it, stop while another run holds a build
    lock, and warn otherwise (also for a lock taken without run, or by an older lock.py, whose holder
    can't be told from this process's parents). Every run this process is in counts (lock.held_here), so
    `lock.py run vita-build -- lock.py run vita-device -- ... pipeline.py` runs under its own vita-build. A
    stage that writes the PS Vita build (writes_vita_build) needs vita-build itself: pc-build doesn't wait
    for a sync's reader (lock.py), which would get files rewritten as it sends them. No stage (a script's
    own steps, such as a private quickcode into work/scratch): any build lock will do."""
    import lock

    needed = 'vita-build' if platform == 'vita' or writes_vita_build(stage, platform) else 'pc-build'
    command = f'uv run tools/downgrade/lock.py run {needed} "<you>: <purpose>" --wait 1800 -- '
    command += 'uv run tools/downgrade/pipeline.py ...'
    here = set(lock.held_here())
    if here & set(lock.BUILD_LOCKS):
        if writes_vita_build(stage, platform) and 'vita-build' not in here:
            sys.exit(
                f'{stage} writes the PS Vita build, which vita.py sync reads alongside a pc-build run: '
                f'run it under vita-build: {command}'
            )
        return
    held = [name for name in lock.BUILD_LOCKS if lock.holder_of(name) and not lock.stale(name)]
    holders = ', '.join(f'{name} ({lock.holder_of(name)})' for name in held)
    if any(lock.process_of(name) for name in held):
        sys.exit(f'Held by another run: {holders}. Wait for it: {command}')
    if held:
        log(f'WARNING: {holders} held; unless that is this job, run: {command}')
    else:
        log('Note: not run under lock.py run, so another job could start a pipeline at the same time')


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument(
        'stage', choices=['rip', 'code', 'link', 'loadcheck', 'project', 'build', 'package', 'quickcode']
    )
    parser.add_argument('--platform', choices=['windows', 'vita'], default='vita')
    parser.add_argument(
        '--backend',
        choices=['mono', 'il2cpp'],
        default='mono',
        help='Windows players: il2cpp runs the code as the Vita does',
    )
    parser.add_argument('--game', type=Path, default=DEFAULT_GAME)
    parser.add_argument('--version', default=TARGET, help='Unity 2018.2 patch release to target')
    parser.add_argument(
        '--scenes',
        help='build: a TEST build of only these scenes of the build settings, by name, comma-separated '
        '("Title,Station Square"), into builds/crow-<version>[-vita]-scenes (vita.py --build takes it); the '
        'first scene (Logos) is always in. The game loads scenes by name: '
        'loading one left out fails',
    )
    parser.add_argument('--development', action='store_true', help="build: Unity's development player")
    parser.add_argument(
        '--force', action='store_true', help='quickcode: go on when the serialized types changed (see quick_code)'
    )
    args = parser.parse_args()
    ignored = [
        option
        for option, given, stages in (
            ('--scenes', args.scenes, ('build',)),
            ('--development', args.development, ('build',)),
            ('--force', args.force, ('quickcode',)),
            ('--backend il2cpp', args.backend == 'il2cpp', ('build',)),
        )
        if given and args.stage not in stages
    ]
    if args.backend == 'il2cpp' and args.platform == 'vita':
        ignored.append('--backend il2cpp (the PS Vita build is IL2CPP only)')
    if args.platform == 'windows' and args.stage in ('package', 'quickcode'):
        ignored.append(f'--platform windows ({args.stage} is PS Vita only)')
    if ignored:
        parser.error(f'{args.stage} does not take {", ".join(ignored)}')
    build_switches(args.platform)  # stops on a value a switch doesn't take, before any stage starts
    check_lock(args.stage, args.platform)
    if not args.version.startswith('2018.2.'):
        parser.error('the compatibility rules are written for Unity 2018.2')
    if not (game_data(args.game) / 'Managed' / 'Assembly-CSharp.dll').exists():
        sys.exit(f'{args.game} is not a Mono build of Crow Country (no Managed/Assembly-CSharp.dll).')
    rip_dir = WORK / f'rip-{args.version}'
    if args.stage == 'rip':
        rip(args.game, rip_dir, args.version)
    if args.stage == 'code':
        out = rewritten_folder(args.version, args.platform)
        sys.exit(rewrite_assemblies(game_data(args.game) / 'Managed', out, args.version, args.platform))
    if args.stage == 'loadcheck':
        sys.exit(1 if load_check(args.version) else 0)
    if args.stage == 'link':
        sys.exit(link_check(args.version, args.platform))
    project = project_folder(args.version, args.platform)
    if args.stage == 'project':
        assemble(rip_dir, project, args.version, args.game, args.platform)
    if args.stage == 'build':
        output = build_folder(args.platform, args.backend, bool(args.scenes), args.version)
        player = output if args.platform == 'vita' else output / 'CrowCountry.exe'
        scenes = [name.strip() for name in args.scenes.split(',') if name.strip()] if args.scenes else None
        build(project, args.version, player, args.platform, args.backend, args.development, scenes)
        if args.platform == 'windows':
            # The Steam wrapper is stubbed (STUBBED_ON), so nothing starts steam_api; an appid file left
            # by an older build would let it sign in to Steam if the real wrapper came back.
            (output / 'steam_appid.txt').unlink(missing_ok=True)
    if args.stage == 'package':
        package_vita(build_folder('vita', version=args.version))
    if args.stage == 'quickcode':
        quick_code(args.version, args.game, args.force)


if __name__ == '__main__':
    main()
