"""Paths, constants and small helpers shared by pipeline.py, its rule modules (asset_rules.py,
material_rules.py, shader_rules.py, video_rules.py, code_trim.py) and the other tools. Tools that
import pipeline also reach the names pipeline.py imports from here as pipeline.<name>."""

import concurrent.futures
import contextlib
import functools
import hashlib
import json
import os
import re
import subprocess
import sys
import time
import types
from pathlib import Path
from typing import NamedTuple

import progress
import truststore
from rich.text import Text

# Every download checks TLS with Windows' certificate store, which fetches missing root certificates
# on demand. Python's own copy of the store lacks them on a fresh Windows (archive.org's, for one).
truststore.inject_into_ssl()


def registry_env(name, scope):
    """A variable as stored for the user ('user') or the machine ('machine'), or None."""
    import winreg

    key, path = (
        (winreg.HKEY_CURRENT_USER, 'Environment')
        if scope == 'user'
        else (winreg.HKEY_LOCAL_MACHINE, r'SYSTEM\CurrentControlSet\Control\Session Manager\Environment')
    )
    try:
        with winreg.OpenKey(key, path) as handle:
            value, kind = winreg.QueryValueEx(handle, name)
    except OSError:
        return None
    return os.path.expandvars(value) if kind == winreg.REG_EXPAND_SZ else value


def effective_env(process, user, machine):
    """The value a variable has for the tools, and whether a new shell is needed to see it: (value, stale)."""
    if process:
        return process, False
    value = user or machine
    return value, bool(value)


def env(name):
    """(value, stale) of a variable: from this process, else as set for new shells (stale), since a shell
    opened before setup.py set it does not have it yet."""
    return effective_env(os.environ.get(name), registry_env(name, 'user'), registry_env(name, 'machine'))


def setting(name):
    """A variable's value for the tools (env), or ''."""
    return env(name)[0] or ''


ROOT = Path(__file__).resolve().parents[2]
TOOLS = ROOT / 'tools'
EXTERNAL = TOOLS / 'external'
HERE = Path(__file__).resolve().parent
WORK = ROOT / 'work'
BUILDS = ROOT / 'builds'
ARTIFACTS = ROOT / 'artifacts' / 'downgrade'
EDITORS = Path(os.environ['LOCALAPPDATA']) / 'Unity' / 'Editors'
# Steam's default install folder, and the game's folder in its library.
STEAM = Path(r'C:\Program Files (x86)\Steam')
STEAM_GAME = STEAM / 'steamapps' / 'common' / 'Crow Country'


def default_game():
    """The game installation pipeline.py reads (--game overrides it) and the test tools copy saves from:
    CROW_GAME for another install folder, else Steam's default folder."""
    return Path(setting('CROW_GAME') or STEAM_GAME)


DEFAULT_GAME = default_game()
# The newest 2018.2 release whose PS Vita build module is known to exist.
TARGET = '2018.2.19f1'
STEAM_APP_ID = '1996010'  # Crow Country's, from its Steam manifest


# Where the pipeline's stages put their output, per target platform ('vita' or 'windows'); the other
# tools find the builds there.
def vita_suffix(platform):
    return '-vita' if platform == 'vita' else ''


def rewritten_folder(version, platform):
    """work/assemblies-<version>[-vita]: the code stage's rewritten assemblies."""
    return WORK / f'assemblies-{version}{vita_suffix(platform)}'


def project_folder(version, platform):
    """work/crow-<version>[-vita]: the project stage's Unity project."""
    return WORK / f'crow-{version}{vita_suffix(platform)}'


def build_folder(platform, backend='mono', scenes=False, version=TARGET):
    """builds/crow-<version>[-vita][-il2cpp][-scenes]: a player built by pipeline.py build (a Windows
    player is CrowCountry.exe in it); scenes: a test build of some scenes (build --scenes)."""
    il2cpp = '-il2cpp' if platform == 'windows' and backend == 'il2cpp' else ''
    return BUILDS / f'crow-{version}{vita_suffix(platform)}{il2cpp}{"-scenes" if scenes else ""}'


SOURCE_VERSION = '2022.3.49f1'
USCSANDBOX_COMMIT = '563391b849116de5d470420cc0e3f1bc6a4b6157'
USCSANDBOX_SHA256 = '05459f727bd591cd588b1b4d3baa8d8c0b0b8bf4ad3990e7f1f1a187594f9c82'  # its GitHub zip
# Mono.Cecil, which the assembly rewriter, StubAssembly and the probe's injector are built with: the NuGet
# release of the version Unity 2020.3's Unity.Cecil.dll forks (0.10.0-beta6). The rewritten assemblies
# are byte-identical to those made with Unity.Cecil; later releases write them differently.
CECIL_VERSION = '0.10.0-beta6'
CECIL_SHA256 = 'fcfeb1c90073641fc56c21409af5d6499d28be691bf19c87bb9b90afe6d015cb'  # the .nupkg
CECIL = EXTERNAL / f'mono.cecil-{CECIL_VERSION}' / 'Mono.Cecil.dll'
CECIL_URL = f'https://api.nuget.org/v3-flatcontainer/mono.cecil/{CECIL_VERSION}/mono.cecil.{CECIL_VERSION}.nupkg'
ASSETRIPPER = EXTERNAL / 'assetripper-2.0.0' / 'AssetRipper.GUI.Free.exe'
CSC = Path(os.environ['WINDIR']) / 'Microsoft.NET' / 'Framework64' / 'v4.0.30319' / 'csc.exe'
# The PS Vita's address on the network (VitaShell's FTP server, vitacompanion's command server), from
# CROW_VITA_HOST, or DRIFTER_VITA_HOST for a console set up for The Drifter's port. Only the console tools
# need it (vita_host() stops with a message without it).
VITA_HOST = setting('CROW_VITA_HOST') or setting('DRIFTER_VITA_HOST')


def vita_host():
    if not VITA_HOST:
        sys.exit("Set CROW_VITA_HOST to the PS Vita's address on the network.")
    return VITA_HOST


def log(message):
    progress.console.print(Text.assemble((time.strftime('[%H:%M:%S]'), 'dim'), ' ', str(message)))


def run(command):
    log(' '.join(str(c) for c in command))
    subprocess.run([str(c) for c in command], check=True)


def editor_folder(version):
    """The installed editor's Editor folder (setup.py installs it there)."""
    return EDITORS / version / 'Editor'


def editor_data(version):
    """The installed editor's Editor/Data folder."""
    return editor_folder(version) / 'Data'


def editor_exe(version):
    return editor_folder(version) / 'Unity.exe'


def download(url, destination: Path, digest: str | None):
    """Download url to destination and check its hash: SHA-256, SHA-1 for a 40-digit pin (archive.org
    lists only SHA-1 and MD5 for an item's files) or SHA-512 for a 128-digit one (Microsoft lists only
    SHA-512 for .NET's files). A file already there with that hash is kept.
    Timeouts, dropped connections and server errors are retried a few times. Stops on a mismatch
    and leaves nothing behind. digest None: a file its publisher updates in place, which the caller
    checks another way (its signature)."""
    algorithm = {40: 'sha1', 128: 'sha512'}.get(len(digest or ''), 'sha256')
    if digest and destination.exists() and file_hash(destination, algorithm) == digest:
        return destination
    import http.client
    import urllib.error

    destination.parent.mkdir(parents=True, exist_ok=True)
    part = destination.with_name(destination.name + '.part')
    log(f'Downloading {url}')
    for attempt in range(1, DOWNLOAD_ATTEMPTS + 1):
        try:
            fetch(url, part)
            break
        except (OSError, http.client.HTTPException) as error:
            permanent = isinstance(error, urllib.error.HTTPError) and error.code < 500 and error.code != 429
            if permanent or attempt == DOWNLOAD_ATTEMPTS:
                part.unlink(missing_ok=True)
                sys.exit(f'{url}: {error}' + ('' if permanent else '. Check the connection and run it again.'))
            wait = 5 * 2 ** (attempt - 1)
            log(f'  {error}; trying again in {wait} s ({attempt} of {DOWNLOAD_ATTEMPTS} attempts failed)')
            time.sleep(wait)
    actual = file_hash(part, algorithm)
    if digest and actual != digest:
        part.unlink()
        sys.exit(
            f'{url}: {algorithm.upper()} {actual}, expected {digest}. The download is corrupt or not the pinned file.'
        )
    part.replace(destination)
    return destination


DOWNLOAD_ATTEMPTS = 5


def fetch(url, part: Path):
    """One download attempt: url into part, from the start."""
    import urllib.request

    request = urllib.request.Request(url, headers={'User-Agent': 'crow-country-vita-setup'})
    with urllib.request.urlopen(request, timeout=60) as response, part.open('wb') as out:
        total = int(response.headers.get('Content-Length') or 0) or None
        with progress.step(f'Downloading {url.rsplit("/", 1)[-1]}', total=total, eta=True, count='MB') as step:
            while chunk := response.read(1 << 20):
                out.write(chunk)
                step.advance(len(chunk))


def split_at_dashes(argv):
    """(the words before '--', the words after it): a tool's own options, and those it passes on."""
    if '--' not in argv:
        return list(argv), []
    split = argv.index('--')
    return argv[:split], argv[split + 1 :]


def number(text: str):
    """A number as the probe logs it, with a decimal point or (in some locales) a comma."""
    return float(text.replace(',', '.'))


def file_hash(path: Path, algorithm='sha256'):
    digest = hashlib.new(algorithm)
    with path.open('rb') as handle:
        while chunk := handle.read(1 << 20):
            digest.update(chunk)
    return digest.hexdigest()


def mono_cecil():
    """Mono.Cecil.dll at CECIL_VERSION, fetched from NuGet on first use."""
    if not CECIL.exists():
        import tempfile
        import zipfile

        with tempfile.TemporaryDirectory() as temp:
            package = download(CECIL_URL, Path(temp) / 'mono.cecil.nupkg', CECIL_SHA256)
            with zipfile.ZipFile(package) as archive:
                data = archive.read('lib/net40/Mono.Cecil.dll')
        CECIL.parent.mkdir(parents=True, exist_ok=True)
        CECIL.write_bytes(data)
    return CECIL


def remove_tree(folder: Path):
    """Delete a folder or a file, read-only files included (Git's object files, for one). A link (a
    symbolic link or a junction) is removed, never what it points to. A file another program holds (an
    antivirus scan, a process still exiting) is tried again for a few seconds, then left. Returns
    (bytes deleted, files left)."""
    if is_link(folder):
        return (0, []) if remove_path(folder, os.rmdir if folder.is_dir() else os.remove) else (0, [folder])
    if not folder.is_dir():
        size = folder.lstat().st_size
        return (size, []) if remove_path(folder, os.remove) else (0, [folder])
    deleted, left, folders = 0, [], []
    for root, names, files in os.walk(folder):
        root = Path(root)
        folders.append(root)
        for name in [n for n in names if is_link(root / n)]:
            names.remove(name)  # not into it
            if not remove_path(root / name, os.rmdir):
                left.append(root / name)
        for name in files:
            path = root / name
            size = path.lstat().st_size
            if remove_path(path, os.remove):
                deleted += size
            else:
                left.append(path)
    for path in reversed(folders):
        remove_path(path, os.rmdir)  # stays when a file in it does
    return deleted, left


def is_link(path: Path):
    return path.is_symlink() or path.is_junction()


DELETING = '.deleting'


def delete(path: Path):
    """Delete a folder or a file whole, or leave it whole: it is first renamed to <name>.deleting, which
    Windows refuses while a program has a file in it open, so a tool is never left half there for the
    next run to trust. finish_deleting clears what an interrupted run left. Returns (bytes deleted,
    paths left)."""
    if is_link(path) or not path.exists():
        return remove_tree(path) if is_link(path) else (0, [])
    renamed = path.with_name(path.name + DELETING)
    if renamed.exists() or is_link(renamed):
        remove_tree(renamed)
    if not remove_path(path, lambda p: os.rename(p, renamed)):
        return 0, [path]
    return remove_tree(renamed)


def finish_deleting(*folders: Path):
    """Delete the <name>.deleting an interrupted delete left in these folders."""
    for folder in folders:
        if folder.is_dir():
            for path in folder.glob('*' + DELETING):
                remove_tree(path)


REMOVE_ATTEMPTS = 6  # half a second apart


def remove_path(path: Path, function):
    import stat

    for attempt in range(REMOVE_ATTEMPTS):
        try:
            function(path)
            return True
        except FileNotFoundError:
            return True
        except PermissionError:
            with contextlib.suppress(OSError):
                os.chmod(path, stat.S_IWRITE)
            if attempt:
                time.sleep(0.5)
        except OSError:  # a folder that still has files
            return False
    return False


def replace_path(source: Path, target: Path, attempts=REMOVE_ATTEMPTS):
    """os.replace, tried again on PermissionError as remove_path does: on Windows a file or folder just
    written (Defender or the indexer reading it) or a target open elsewhere without delete sharing refuses
    the rename for a moment. Raises the last error when it never goes through."""
    for attempt in range(attempts):
        try:
            os.replace(source, target)
            return
        except PermissionError:
            if attempt == attempts - 1:
                raise
            time.sleep(0.5)


def github_source(repository, commit, digest, folder: Path):
    """A GitHub repository's files at a pinned commit, from its zip archive, into folder (replaced)."""
    import tempfile
    import zipfile

    with tempfile.TemporaryDirectory() as temp:
        archive = download(f'https://github.com/{repository}/archive/{commit}.zip', Path(temp) / 'source.zip', digest)
        if folder.exists() and remove_tree(folder)[1]:  # a Git clone, from earlier versions of the tools
            sys.exit(f'Could not delete {folder}: a program has files in it open. Close it and run this again.')
        with zipfile.ZipFile(archive) as handle:
            for member in handle.infolist():
                name = member.filename.split('/', 1)[1]  # below the archive's top folder
                if name and not member.is_dir():
                    target = folder / name
                    target.parent.mkdir(parents=True, exist_ok=True)
                    target.write_bytes(handle.read(member))
    return folder


def apply_patch(patch: Path, folder: Path):
    """Apply a unified diff (git diff's output; the lines before its first file are a comment) to the
    files in folder. Each hunk must match at the line its header names. Lines are compared without
    their line ending, and each file keeps its own."""
    lines = patch.read_text(encoding='utf-8').splitlines()
    i = 0
    while i < len(lines):
        if not lines[i].startswith('--- '):
            i += 1
            continue
        path = folder / lines[i + 1].removeprefix('+++ b/')
        i += 2
        text = path.read_bytes().decode('utf-8')
        newline = '\r\n' if '\r\n' in text else '\n'
        source = re.split(r'\r?\n', text)
        patched, done = [], 0  # done: the source lines already copied or replaced
        while i < len(lines) and lines[i].startswith('@@'):
            header = re.match(r'@@ -(\d+)(?:,(\d+))? \+\d+(?:,(\d+))? @@', lines[i])
            start, old_count, new_count = int(header[1]), int(header[2] or 1), int(header[3] or 1)
            i += 1
            old, new = [], []
            while len(old) < old_count or len(new) < new_count:
                kind, body = lines[i][:1] or ' ', lines[i][1:]
                i += 1
                if kind != '+':
                    old.append(body)
                if kind != '-':
                    new.append(body)
            first = start - 1 if old_count else start
            if source[first : first + len(old)] != old:
                sys.exit(f'{patch.name}: a hunk does not match {path} at line {start}.')
            patched += source[done:first] + new
            done = first + len(old)
        path.write_bytes(newline.join(patched + source[done:]).encode('utf-8'))


SHADER_NAME = re.compile(r'\s*Shader\s+"([^"]+)"')  # a .shader file's name line

# Serialized YAML assets with one main object, which Unity names after the file (m_Name) ...
NATIVE_ASSETS = {
    '.asset',
    '.anim',
    '.mat',
    '.prefab',
    '.controller',
    '.overrideController',
    '.mixer',
    '.playable',
    '.physicsMaterial2D',
    '.fontsettings',
    '.guiskin',
    '.renderTexture',
}
# ... and with scenes, every serialized YAML file of a project: what can reference other files by GUID.
YAML_FILES = NATIVE_ASSETS | {'.unity'}

# Pieces of Unity's YAML the rules read and write. A number as the export writes it, for float(): the
# particle materials' _CameraFadeParams is {r: 0, g: Infinity, ...}, and a pattern without Infinity
# matched no such material, so the Fade particles lost _ALPHABLEND_ON (drawn opaque).
NUMBER = r'([-+\d.eE]+|-?Infinity|NaN)'
VECTOR = rf'\{{r: {NUMBER}, g: {NUMBER}, b: {NUMBER}, a: {NUMBER}\}}'  # a colour (Vector4 alike)
SHADER_LINE = re.compile(r'^  m_Shader: (\{.*\})$', re.M)  # a material's shader reference
NAME_LINE = re.compile(r'^  m_Name: ?(.*)$', re.M)  # the first is the main object's name


def builtin_shader(file_id):
    """A reference to a built-in shader: Standard 46, Legacy Shaders/Diffuse 7, Particles/Standard Surface 210."""
    return f'{{fileID: {file_id}, guid: 0000000000000000f000000000000000, type: 0}}'


BUILTIN_STANDARD = builtin_shader(46)
BUILTIN_LEGACY_DIFFUSE = builtin_shader(7)
BUILTIN_PARTICLES_SURFACE = builtin_shader(210)


def require(match, path, what):
    """match, or stop with the file's name when a rule's pattern found nothing in it."""
    if match is None:
        sys.exit(f'{path}: no {what}; the export changed, or the rule reads this file wrong')
    return match


def game_data(game: Path) -> Path:
    """The game's <name>_Data folder."""
    data = next(game.glob('*_Data'), None)
    if data is None:
        sys.exit(f'No <name>_Data folder in {game}: is that the game folder? (--game, CROW_GAME)')
    return data


def meta_guid(meta: Path):
    match = re.search(r'^guid: ([0-9a-f]{32})', meta.read_text(encoding='utf-8'), re.M)
    return match.group(1) if match else None


def meta_of(path: Path) -> Path:
    """The .meta file of an asset or folder (with_suffix would cut a name with dots)."""
    return path.with_name(path.name + '.meta')


def guid_for(kind, name):
    """A GUID that is the same on every run, for a generated asset of a kind and name."""
    return hashlib.md5(f'{kind}:{name}'.encode()).hexdigest()


def stable_meta(kind, name, importer):
    folder = 'folderAsset: yes\n' if kind == 'folder' else ''
    return f'fileFormatVersion: 2\nguid: {guid_for(kind, name)}\n{folder}{importer}:\n  userData:\n'


def folder_meta(folder: Path):
    """{.meta path: content} of a generated folder."""
    return {meta_of(folder): stable_meta('folder', folder, 'DefaultImporter').encode()}


def walk(folder: Path):
    """{path relative to folder: (size, modification time in ns) of a file, or None for a folder} of
    everything under folder. One directory listing per folder: on Windows a listing has every file's
    size and time, so no file is opened or stat'ed (a second for the export's 185,000 paths, where
    rglob with a stat of each took 13)."""
    found, folders = {}, [Path()]
    while folders:
        relative = folders.pop()
        with os.scandir(folder / relative) as entries:
            for entry in entries:
                path = relative / entry.name
                if entry.is_dir(follow_symlinks=False):
                    found[path] = None
                    folders.append(path)
                else:
                    stat = entry.stat(follow_symlinks=False)
                    found[path] = (stat.st_size, stat.st_mtime_ns)
    return found


@functools.cache
def listing(folder: Path):
    """walk(folder), once per run: for folders that don't change while the tools run (the export)."""
    return walk(folder)


SCAN_CACHE = WORK / 'scan-cache'
SCAN_THREADS = 16


def code_fingerprint(function, seen=None):
    """What a function computes, as text that is the same on every run: its bytecode and that of the
    code it defines (lambdas, comprehensions), the names and constants they use, the values of plain
    types (numbers, strings, patterns, collections of them) it reads from its module or its closure,
    and the same of the tools' own functions it calls. Not what it reaches through other objects."""
    seen = set() if seen is None else seen
    if id(function) in seen:
        return ''
    seen.add(id(function))
    parts, names = [], set()

    def code(c):
        parts.append(c.co_code.hex() + repr(c.co_names))
        names.update(c.co_names)
        for const in c.co_consts:
            if isinstance(const, types.CodeType):
                code(const)
            else:
                parts.append(plain(const) or type(const).__name__)

    code(function.__code__)
    values = []
    for name, cell in zip(function.__code__.co_freevars, function.__closure__ or (), strict=True):
        with contextlib.suppress(ValueError):  # a variable of the enclosing function not set yet
            values.append((name, cell.cell_contents))
    values += [(name, function.__globals__[name]) for name in sorted(names) if name in function.__globals__]
    for name, value in values:
        if isinstance(value, types.FunctionType) and Path(value.__code__.co_filename).parent == HERE:
            parts.append(f'{name}=({code_fingerprint(value, seen)})')
        elif (text := plain(value)) is not None:
            parts.append(f'{name}={text}')
    return ' '.join(parts)


@functools.cache
def code_digest(function):
    """A short hash of code_fingerprint(function), once per run, to keep beside what the function made."""
    return hashlib.sha256(code_fingerprint(function).encode()).hexdigest()[:16]


def plain(value):
    """A value of a plain type as text that is the same on every run (sets sorted), or None."""
    if value is None or isinstance(value, str | bytes | int | float | Path):
        return repr(value)
    if isinstance(value, re.Pattern):
        return f're({value.pattern!r}, {value.flags})'
    if isinstance(value, dict):
        items = [(plain(key), plain(item)) for key, item in value.items()]
        if any(key is None or item is None for key, item in items):
            return None
        return 'dict[' + ', '.join(sorted(f'{key}: {item}' for key, item in items)) + ']'
    if isinstance(value, tuple | list | set | frozenset):
        items = [plain(item) for item in value]
        if None in items:
            return None
        return f'{type(value).__name__}[{", ".join(sorted(items) if isinstance(value, set | frozenset) else items)}]'
    return None


def scan(name: str, folder: Path, select, function, version=''):
    """{relative path: function(path)} for the files under folder whose relative path select() accepts.
    The results are kept in work/scan-cache/<name>.json under each file's size and modification time,
    so a rerun reads only the files that changed. function's result must be JSON; version names what
    else it depends on (a change discards the cache). So does a change to function itself or to what
    code_fingerprint sees of it (a constant it reads, such as asset_rules.NAME_SEARCH_BYTES)."""
    version = f'{version} code {code_digest(function)}'
    cache_path = SCAN_CACHE / f'{name}.json'
    try:
        cache = json.loads(cache_path.read_text(encoding='utf-8'))
    except (OSError, ValueError):
        cache = {}
    entries = cache.get('files', {}) if cache.get('version') == version else {}
    results, fresh, missing = {}, {}, []
    for relative, stat in listing(folder).items():
        if stat is None or not select(relative):
            continue
        key = relative.as_posix()
        entry = entries.get(key)
        if entry is None or entry[:2] != list(stat):
            missing.append((relative, stat))
            continue
        fresh[key] = entry
        results[relative] = entry[2]
    # Reading many files is mostly waiting (on the disk, on the virus scanner opening each one), so
    # SCAN_THREADS read at once.
    with concurrent.futures.ThreadPoolExecutor(SCAN_THREADS) as pool:
        values = pool.map(lambda item: function(folder / item[0]), missing)
        for (relative, stat), value in zip(missing, values, strict=True):
            fresh[relative.as_posix()] = [*stat, value]
            results[relative] = value
    if fresh != entries:
        SCAN_CACHE.mkdir(parents=True, exist_ok=True)
        part = cache_path.with_name(f'{cache_path.name}.{os.getpid()}.part')
        part.write_text(json.dumps({'version': version, 'files': fresh}), encoding='utf-8')
        part.replace(cache_path)  # whole, also with another run writing it at the same time
    return results


def expect(count: int, minimum: int, what: str):
    """Stop when a rule that must apply matched fewer than minimum (its count on the export it was
    written for): a game update or another AssetRipper can turn it into a no-op that only logs 0, and
    the console then shows the result (L and R doing nothing, a VideoPlayer hole that crashes the
    player)."""
    if count < minimum:
        sys.exit(
            f'{what}: {count}, expected at least {minimum}. The export changed or the rule no longer matches '
            'it; fix the rule, or lower its minimum where fewer is right.'
        )


# ---------------------------------------------------------------- build switches


class Switch(NamedTuple):
    """A CROW_* environment variable that changes what a stage writes.
    stage: 'code' (the rewriter: code, project, quickcode), 'project' (the asset rules), 'build' (the editor
    scripts in unity/, as the build writes the scenes), 'player' (written into the PS Vita build by build and
    quickcode), 'check' (the build's checks; writes nothing).
    values: the allowed values, space-separated, or '' for a whole number.
    vita, other: the default with the PS Vita's scene data (the PS Vita, or CROW_VITA_DATA=1) and without; the
    name of another switch takes its value; '' leaves it unset (the code's own default).
    platforms: where it applies."""

    stage: str
    values: str
    vita: str
    other: str
    platforms: tuple = ('vita', 'windows')


# Every build switch, checked and resolved here (build_switches), logged by the stages and recorded with
# the project (pipeline.PROJECT_INPUTS) and the PS Vita build's code stage (pipeline.NON_CODE_INPUTS), so
# that build and quickcode warn when one differs from what the project or the build had. The editor gets
# each build switch's value explicitly (unity/BuildUtil.cs: Switch). docs/build.md describes them.
BUILD_SWITCHES = {
    # 1: a Windows player with the PS Vita's scene data (the defaults below as on the PS Vita), for measurements.
    'CROW_VITA_DATA': Switch('build', '0 1', '0', '0'),
    # Enemies' random looks (RandomVariant) picked at build time, every enemy's in every scene (unity/VariantBake.cs).
    'CROW_VARIANT_BAKE': Switch('build', 'on off', 'on', 'off'),
    # The player's ProBuilder meshes built at build time (unity/ProBuilderMeshes.cs); bake keeps the components.
    'CROW_PROBUILDER': Switch('build', 'on bake off', 'on', 'off'),
    # The static merge (unity/MergeScenes.cs, compat/Merge.cs); ab keeps the parts' meshes for -crow-merge off.
    'CROW_MERGE': Switch('build', 'on ab off', 'on', 'off'),
    # The merge's largest part in vertices and its budget per scene in KB; unset: Merge's own.
    'CROW_MERGE_MAX_VERTICES': Switch('build', '', '', ''),
    'CROW_MERGE_MAX_KB': Switch('build', '', '', ''),
    # The scene split after the merge (Merge.Split): on, the still parts; movers, also those that move on their own.
    'CROW_SPLIT': Switch('build', 'movers on off', 'movers', 'off'),
    # The player merge (unity/PlayerMerge.cs); ab keeps the parts for -crow-player-merge off. PARTIAL=1 merges
    # with the uses of the scenes there are (a test build before any full one).
    'CROW_PLAYER_MERGE': Switch('build', 'on ab off', 'on', 'off'),
    'CROW_PLAYER_MERGE_PARTIAL': Switch('build', '0 1', '0', '0'),
    # MeshCombineStudio's run-time combining done at build time, and the combined sources' renderers left out
    # (unity/MeshCombineBake.cs).
    'CROW_MCS_BAKE': Switch('build', 'on off', 'on', 'off'),
    'CROW_MCS_STRIP': Switch('build', 'on off', 'CROW_MCS_BAKE', 'CROW_MCS_BAKE'),
    # The inert scripts left out of the scenes (unity/StripScenes.cs).
    'CROW_STRIP': Switch('build', 'on off', 'on', 'off'),
    # See-through layers stacked at one place drawn once, with the stack's alpha (unity/StackedLayers.cs).
    'CROW_STACK_FOLD': Switch('build', 'on off', 'on', 'off'),
    # The materials saved with CrowVita/Lit (asset_rules.vita_lit_overlay); off leaves the swap to run time.
    'CROW_LIT_BAKE': Switch('project', 'on off', 'on', 'on', ('vita',)),
    # on: the FSMs' compiled bodies only (rewriter/FsmCompile.cs); off keeps the originals beside them.
    'CROW_FSM_COMPILE': Switch('code', 'on off', 'off', 'off'),
    # 1: PlayMaker's actions and the behaviours timed, for -crow-action-profile (rewriter/ActionProfile.cs).
    'CROW_ACTION_PROFILE': Switch('code', '0 1', '0', '0'),
    # The PS Vita player's graphics jobs, in Media/boot.config (pipeline.boot_config; docs/vita.md).
    'CROW_GFX_JOBS': Switch('player', 'on off', 'on', 'on', ('vita',)),
    # 1: a build whose editor scripts failed in a scene, or with errors, goes through, for experiments.
    'CROW_ALLOW_SCENE_FAILURES': Switch('check', '0 1', '0', '0'),
}


def build_switches(platform, stages=None):
    """{name: value} of the build switches that apply on platform (and to stages, all if None): the
    environment's value, else the default. Stops on a value the switch doesn't take."""
    given = {}
    for name, switch in BUILD_SWITCHES.items():
        value = os.environ.get(name, '').strip()
        if value and not (value in switch.values.split() if switch.values else value.isdigit()):
            allowed = ', '.join(switch.values.split()) if switch.values else 'a whole number'
            sys.exit(f'{name}={value}: {name} takes {allowed} (common.BUILD_SWITCHES, docs/build.md)')
        given[name] = value
    vita_data = platform == 'vita' or given['CROW_VITA_DATA'] == '1'

    def resolved(name):
        switch = BUILD_SWITCHES[name]
        default = switch.vita if vita_data else switch.other
        return given[name] or (resolved(default) if default in BUILD_SWITCHES else default)

    return {
        name: resolved(name)
        for name, switch in BUILD_SWITCHES.items()
        if platform in switch.platforms and (stages is None or switch.stage in stages)
    }


def describe_switches(values: dict):
    """The switches as name=value, those set in the environment marked with *."""
    return ' '.join(
        f'{name}={value or "(unset)"}{"*" if os.environ.get(name) else ""}' for name, value in values.items()
    )
