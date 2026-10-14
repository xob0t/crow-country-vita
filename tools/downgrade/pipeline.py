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

The game installation is only read. Outputs go to work/, builds/ and artifacts/. The stages after rip
(project, build, package) follow The Drifter's port (github.com/xob0t/the-drifter-vita) as they are
written.
"""

import argparse
import hashlib
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

import progress
from asset_rules import (
    COMPAT_GUID,
    addressables_overlay,
    audio_overlay,
    compat_component_overlay,
    link_xml,
    name_overlay,
    native_plugin_overlay,
    render_texture_overlay,
    scene_reference_overlay,
    settings_overlay,
    texture_overlay,
)
from common import (
    ARTIFACTS,
    ASSETRIPPER,
    CECIL,
    CSC,
    DEFAULT_GAME,
    EDITORS,
    HERE,
    ROOT,
    TARGET,
    WORK,
    editor_data,
    folder_meta,
    game_data,
    log,
    meta_of,
    mono_cecil,
    run,
    stable_meta,
)
from material_rules import material_keyword_overlay
from shader_rules import shader_overlay

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


def engine_library(version, out: Path, sources, modules=(), defines=()):
    """Compile a C# 5 library from source files against the editor's unityaot class libraries,
    UnityEngine's core module and the given UnityEngine modules (e.g. 'AudioModule')."""
    check_parts()
    data = editor_data(version)
    bcl = data / 'MonoBleedingEdge' / 'lib' / 'mono' / 'unityaot'
    engine = data / 'Managed' / 'UnityEngine'
    run(
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
            f'/r:{data / "UnityExtensions" / "Unity" / "GUISystem" / "UnityEngine.UI.dll"}',
            f'/out:{out}',
            *sources,
        ]
    )


# The C# sources split into parts, each list in compile order: CrowCompat and the UnityEngine
# modules it uses, and the assembly rewriter.
COMPAT_SOURCES = [
    HERE / 'Compat2018.cs',
    *(
        HERE / 'compat' / f'{name}.cs'
        for name in ('ApiShims', 'TextCore', 'Addressables', 'Video', 'Diag', 'CamEffect', 'Quality')
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
]
REWRITER_SOURCES = [
    HERE / 'DowngradeAssemblies.cs',
    *(HERE / 'rewriter' / f'{name}.cs' for name in ('Redirect', 'Runtime', 'Patches')),
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
    data = editor_data(version)
    bcl = data / 'MonoBleedingEdge' / 'lib' / 'mono' / 'unityaot'
    engine = data / 'Managed' / 'UnityEngine'
    bin_dir = WORK / 'bin'
    bin_dir.mkdir(parents=True, exist_ok=True)
    cecil = bin_dir / CECIL.name  # beside the tools, which load it at run time
    if not cecil.exists() or cecil.read_bytes() != mono_cecil().read_bytes():
        shutil.copy2(mono_cecil(), cecil)
    compat = compat_path(platform)
    compat.parent.mkdir(parents=True, exist_ok=True)
    # CROW_VITA leaves out the shims of engine types the Vita's player lacks (VideoPlayer's members).
    engine_library(version, compat, COMPAT_SOURCES, COMPAT_MODULES, ['CROW_VITA'] if platform == 'vita' else [])
    rewriter = bin_dir / 'DowngradeAssemblies.exe'
    run([CSC, '/nologo', f'/r:{cecil}', f'/out:{rewriter}', *REWRITER_SOURCES])
    run([CSC, '/nologo', f'/r:{cecil}', f'/out:{bin_dir / "StubAssembly.exe"}', HERE / 'StubAssembly.cs'])
    target_engine = data / PLATFORM_ENGINE[platform] if platform in PLATFORM_ENGINE else engine
    return compat, rewriter, [bcl, target_engine, data / 'UnityExtensions' / 'Unity' / 'GUISystem']


# Managed wrappers of native libraries a platform lacks; their methods become no-ops
# returning defaults, so the game's own "not available" paths run instead.
STUBBED_ON = {'vita': ['Facepunch.Steamworks.Win64', 'GalaxyCSharp']}
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
    env = {**os.environ, 'CROW_API_VERSION': version}
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
    log(f'Rewrote {len(list(out.glob("*.dll")))} assemblies; {len(problems)} problems (log: {log_path})')
    return result.returncode


# ---------------------------------------------------------------- link


def link_check(version: str, platform: str):
    """Run Unity's linker (UnityLinker, as the editor's IL2CPP build does) on the rewritten assemblies
    with the target's engine and class libraries, outside the editor: it stops at the first type,
    member or attribute that doesn't resolve, which the editor reports only after building every
    scene. Returns the linker's exit code; its output goes to artifacts/downgrade/link-<platform>.log."""
    data = editor_data(version)
    linker = EDITORS / version / 'Editor' / 'Data' / 'il2cpp' / 'build' / 'UnityLinker.exe'
    if not linker.exists():
        linker = data / 'il2cpp' / 'build' / 'UnityLinker.exe'
    rewritten = WORK / (f'assemblies-{version}' + ('-vita' if platform == 'vita' else ''))
    staging = WORK / f'link-{platform}'
    if staging.exists():
        shutil.rmtree(staging)
    managed = staging / 'Managed'
    managed.mkdir(parents=True)
    engine = data / PLATFORM_ENGINE.get(platform, Path('Managed') / 'UnityEngine')
    bcl = data / 'MonoBleedingEdge' / 'lib' / 'mono' / 'unityaot'
    for source in [*engine.glob('*.dll'), *bcl.glob('*.dll'), *(bcl / 'Facades').glob('*.dll')]:
        shutil.copy2(source, managed / source.name)
    # The player's uGUI; the editor's references UnityEditor.
    shutil.copy2(data / 'UnityExtensions' / 'Unity' / 'GUISystem' / 'Standalone' / 'UnityEngine.UI.dll', managed)
    roots = [*rewritten.glob('*.dll'), compat_path(platform)]
    for dll in roots:
        shutil.copy2(dll, managed / dll.name)
    link = staging / 'link.xml'
    link.write_text(link_xml(roots, platform), encoding='utf-8')
    whitelists = data / 'PlaybackEngines' / 'PSP2Player' / 'Whitelists' if platform == 'vita' else None
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
        f'--plugin={linker.parents[2] / "PlaybackEngines" / "PSP2Player" / "Tools" / "PSP2Il2CppPlugin.dll"}',
        '--emit-null-checks',
        '--enable-array-bounds-check',
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
    player = ROOT / 'builds' / f'crow-{version}' / 'CrowCountry_Data' / 'Managed'
    if not player.exists():
        sys.exit(f'No Windows player in {player.parent.parent}: build one first (pipeline.py build --platform windows)')
    staging = WORK / 'loadcheck'
    if staging.exists():
        shutil.rmtree(staging)
    shutil.copytree(player, staging)
    rewritten = [*(WORK / f'assemblies-{version}').glob('*.dll'), compat_path('windows')]
    for dll in rewritten:
        shutil.copy2(dll, staging / dll.name)
    exe = staging / 'LoadCheck.exe'
    run([CSC, '/nologo', f'/out:{exe}', HERE / 'LoadCheck.cs'])
    mono = EDITORS / version / 'Editor' / 'Data' / 'MonoBleedingEdge' / 'bin' / 'mono.exe'
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


def sync_tree(source: Path, destination: Path, overlay: dict, excluded: set):
    """Mirror source into destination with generated files from overlay replacing or adding paths,
    and excluded source paths left out. Unchanged files keep their timestamps so Unity's import
    cache stays valid between runs."""
    destination.mkdir(parents=True, exist_ok=True)
    wanted = set(overlay)
    for path in source.rglob('*'):
        relative = path.relative_to(source)
        if relative in excluded or relative in overlay:
            continue
        wanted.add(relative)
        target = destination / relative
        if path.is_dir():
            target.mkdir(parents=True, exist_ok=True)
            continue
        stat = path.stat()
        if target.exists():
            other = target.stat()
            if other.st_size == stat.st_size and int(other.st_mtime) == int(stat.st_mtime):
                continue
        target.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(path, target)
    for relative, content in overlay.items():
        write_if_changed(destination / relative, content)
        wanted.update(relative.parents)
    for path in sorted(destination.rglob('*'), reverse=True):
        if path.relative_to(destination) not in wanted:
            shutil.rmtree(path) if path.is_dir() else path.unlink()


def write_if_changed(path: Path, content: bytes):
    if path.exists() and path.read_bytes() == content:
        return
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_bytes(content)


# The renamed game assemblies keep their originals' .meta, so the scripts' references stay valid.
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
    rewritten = WORK / (f'assemblies-{version}' + ('-vita' if platform == 'vita' else ''))
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
    """The rules for the exported assets and settings (asset_rules.py, shader_rules.py), in order: a
    rule reads the files the rules before it changed. Returns (overlay, exported paths left out)."""
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
        overlay.update(material_keyword_overlay(exported, game, overlay))
        overlay[Path('Assets') / 'link.xml'] = link_xml(assemblies, platform).encode()
        overlay[Path('Assets') / 'link.xml.meta'] = stable_meta('asset', 'link.xml', 'TextScriptImporter').encode()
        overlay.update(texture_overlay(exported, game, platform))
        moved, renamed = addressables_overlay(exported, overlay)
        overlay.update(moved)
        excluded.update(renamed)
        moved, renamed = name_overlay(exported, game, overlay)
        overlay.update(moved)
        excluded.update(renamed)
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


def assemble(rip_dir: Path, project: Path, version: str, game: Path, platform: str):
    exported = rip_dir / 'ExportedProject'
    plugins, plugin_files, assemblies = assembly_overlay(exported, game, version, platform)
    assets, excluded = asset_overlay(exported, game, version, platform, assemblies)
    overlay = {**plugins, **assets, **editor_scripts()}
    excluded |= plugin_files
    log(f'Synchronizing {project}')
    with progress.step(f'Synchronizing {project.name}'):
        for part in ('Assets', 'ProjectSettings', 'Packages'):
            sync_tree(
                exported / part,
                project / part,
                {k.relative_to(part): v for k, v in overlay.items() if k.parts[0] == part},
                {k.relative_to(part) for k in excluded if k.parts[0] == part},
            )


# ---------------------------------------------------------------- build

STEAM_APP_ID = '1996010'  # Crow Country's, from its Steam manifest


def build(project: Path, version: str, output: Path, platform: str, backend: str = 'mono', development: bool = False):
    """Import the project and build its player with the Unity editor in batch mode
    (unity/DowngradeBuild.cs)."""
    ARTIFACTS.mkdir(parents=True, exist_ok=True)
    unity = EDITORS / version / 'Editor' / 'Unity.exe'
    if not unity.exists():
        sys.exit(f'No Unity {version} editor at {unity}')
    variant = '-il2cpp' if platform == 'windows' and backend == 'il2cpp' else ''
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
                '-logFile',
                str(log_path),
            ]
        )
    log(f'Unity exited {result.returncode} after {time.time() - started:.0f}s; log {log_path}')
    summarize_log(log_path)
    if result.returncode != 0:
        sys.exit(result.returncode)
    # Unity can report success after a failed IL2CPP step; check for the player, and for
    # IL2CPP's failure in the log (a failed link leaves the previous build's game module).
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
    if 'BuilderFailedException' in log_path.read_text(encoding='utf-8', errors='replace'):
        sys.exit(f'Unity reported success but IL2CPP failed; see {log_path}')


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


def vita_app_files(build: Path):
    """Files of the installed app for a PC Hosted PS Vita build, as (path in the app, local path or
    bytes): the player becomes eboot.bin with the trial flag cleared, the LiveArea art replaces
    Unity's placeholders, host-only files are left out (The Drifter's port's packaging)."""
    import livearea

    selfs = list(build.glob('*.self'))
    if len(selfs) != 1:
        sys.exit(f'expected one .self in {build}, found {[p.name for p in selfs]}')
    player = bytearray(selfs[0].read_bytes())
    player[0x80] = 0  # trial flag, as the community PSVita Unity Utilities do
    marker = player.find(b'trial.png')
    if marker >= 0:
        player[marker] = 0  # no trial watermark
    yield 'eboot.bin', bytes(player)
    art = livearea.generate()
    skip = {'SymbolFiles', 'configuration.psp2path'}
    for path in sorted(build.rglob('*')):
        relative = path.relative_to(build)
        if path.is_dir() or relative.parts[0] in skip or path.suffix in ('.self', '.bat'):
            continue
        name = relative.as_posix()
        yield name, art / VITA_ARTWORK[name] if name in VITA_ARTWORK else path


# LiveArea images from livearea.py, in place of Unity's placeholders.
VITA_ARTWORK = {
    'sce_sys/icon0.png': 'icon0.png',
    'sce_sys/pic0.png': 'pic0.png',
    'sce_sys/livearea/contents/bg0.png': 'bg0.png',
    'sce_sys/livearea/contents/default_gate.png': 'gate.png',
}
PACKAGE_CHUNK = 1 << 22


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
            if isinstance(source, bytes):
                archive.writestr(name, source)
                step.advance(len(source))
                continue
            info = zipfile.ZipInfo.from_file(source, name)
            info.compress_type = archive.compression
            with source.open('rb') as src, archive.open(info, 'w') as dest:
                while chunk := src.read(PACKAGE_CHUNK):
                    dest.write(chunk)
                    step.advance(len(chunk))
    log(f'Packaged {vpk} ({vpk.stat().st_size / 2**20:.0f} MB)')
    return vpk


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument('stage', choices=['rip', 'code', 'link', 'loadcheck', 'project', 'build', 'package'])
    parser.add_argument('--platform', choices=['windows', 'vita'], default='vita')
    parser.add_argument(
        '--backend',
        choices=['mono', 'il2cpp'],
        default='mono',
        help='Windows players: il2cpp runs the code as the Vita does',
    )
    parser.add_argument('--game', type=Path, default=DEFAULT_GAME)
    parser.add_argument('--version', default=TARGET, help='Unity 2018.2 patch release to target')
    args = parser.parse_args()
    if not args.version.startswith('2018.2.'):
        parser.error('the compatibility rules are written for Unity 2018.2')
    if not (game_data(args.game) / 'Managed' / 'Assembly-CSharp.dll').exists():
        sys.exit(f'{args.game} is not a Mono build of Crow Country (no Managed/Assembly-CSharp.dll).')
    rip_dir = WORK / f'rip-{args.version}'
    if args.stage == 'rip':
        rip(args.game, rip_dir, args.version)
    if args.stage == 'code':
        out = WORK / (f'assemblies-{args.version}' + ('-vita' if args.platform == 'vita' else ''))
        sys.exit(rewrite_assemblies(game_data(args.game) / 'Managed', out, args.version, args.platform))
    if args.stage == 'loadcheck':
        sys.exit(1 if load_check(args.version) else 0)
    if args.stage == 'link':
        sys.exit(link_check(args.version, args.platform))
    suffix = '-vita' if args.platform == 'vita' else ''
    project = WORK / f'crow-{args.version}{suffix}'
    if args.stage == 'project':
        assemble(rip_dir, project, args.version, args.game, args.platform)
    if args.stage == 'build':
        variant = '-il2cpp' if args.platform == 'windows' and args.backend == 'il2cpp' else ''
        output = ROOT / 'builds' / f'crow-{args.version}{suffix}{variant}'
        player = output if args.platform == 'vita' else output / 'CrowCountry.exe'
        build(project, args.version, player, args.platform, args.backend)
        if args.platform == 'windows':
            # Without it, steam_api relaunches the player through Steam and exits, as the original does.
            (output / 'steam_appid.txt').write_text(STEAM_APP_ID + '\n')
    if args.stage == 'package':
        package_vita(ROOT / 'builds' / f'crow-{args.version}-vita')


if __name__ == '__main__':
    main()
