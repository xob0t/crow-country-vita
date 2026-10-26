"""Check what the pipeline needs for a target and print a checklist with the command that fixes each gap.

    uv run tools/setup.py doctor [--target all|vita|windows|il2cpp] [--license]

Exits 1 if something required is missing. Changes nothing, except that --license starts the editor
once in batch mode with an empty project in a temporary folder (about a minute) to see that it runs,
and remembers a pass in work/license-check.ok.

tools/setup.py installs what these checks look for; the paths, versions and download pins are here.
"""

import argparse
import importlib.util
import os
import re
import shutil
import subprocess
import sys
import tempfile
from dataclasses import dataclass
from pathlib import Path

import progress
from common import (
    ASSETRIPPER,
    CECIL,
    CSC,
    EDITORS,
    EXTERNAL,
    ROOT,
    SOURCE_VERSION,
    STEAM,
    STEAM_APP_ID,
    STEAM_GAME,
    TARGET,
    WORK,
    default_game,
    editor_data,
    env,
    file_hash,
)
from rich.text import Text

SETUP = 'uv run tools/setup.py'

# Unity 2018.2.19f1's installers: Unity's CDN, except the PS Vita module, which Unity no longer serves (the
# archive.org copy is Unity's own signed installer; setup.py checks the hash and the signature).
UNITY_CDN = 'https://download.unity3d.com/download_unity/06990f28ba00'
INSTALLERS = {
    'editor': (
        f'{UNITY_CDN}/Windows64EditorInstaller/UnitySetup64-2018.2.19f1.exe',
        '4a2e95a7bd7b04cc95d3ce110e17775ba5d810b59c2c925ad6e34da4147d3f5e',
    ),
    'il2cpp': (
        f'{UNITY_CDN}/TargetSupportInstaller/UnitySetup-Windows-IL2CPP-Support-for-Editor-2018.2.19f1.exe',
        '56d829305af445d0d91a1b52790fe8a59e02020c73771c5162fee94c351c9d24',
    ),
    'vita': (
        'https://archive.org/download/unitypsvitasupport/'
        'UnitySetup-Playstation-Vita-Support-for-Editor-2018.2.19f1.exe',
        'e68ad5a92622c34abb0c535a6de166531a1e8a192944fd1a50fe8297f7427901',
    ),
}
# What each installer leaves in the editor folder, and what it is called.
EDITOR_FILES = {
    'editor': 'Editor/Unity.exe',
    'il2cpp': 'Editor/Data/PlaybackEngines/windowsstandalonesupport/Variations/win64_nondevelopment_il2cpp',
    'vita': 'Editor/Data/PlaybackEngines/PSP2Player/PSP2Player_Il2cpp.self',
}
MODULE_NAMES = {'il2cpp': 'Windows IL2CPP module', 'vita': 'PS Vita module'}
# What the pipeline reads from the editor's Data folder (common.editor_data: an installed editor's, or
# the files The Drifter's setup extracts into tools/external/unity-<version>), under Editor/Data. The
# PS Vita module's player and engine assemblies only for the vita target.
EDITOR_DATA_CHECKED = [
    'CGIncludes/UnityCG.cginc',
    'Managed/UnityEngine/UnityEngine.CoreModule.dll',
    'MonoBleedingEdge/lib/mono/unityaot/mscorlib.dll',
    'il2cpp/build/UnityLinker.exe',
    'UnityExtensions/Unity/GUISystem/UnityEngine.UI.dll',
]
EDITOR_DATA_CHECKED_VITA = [
    'PlaybackEngines/PSP2Player/Managed',
    'PlaybackEngines/PSP2Player/PSP2Player_Il2cpp.self',
]


def module_name(module, version=TARGET):
    return f'Unity {version} editor' if module == 'editor' else MODULE_NAMES[module]


ASSETRIPPER_ZIP = (
    'https://github.com/AssetRipper/AssetRipper/releases/download/2.0.0/AssetRipper_win_x64.zip',
    '9a7ef0e7c5c3ea5b90b4e6d855e2d98d5f7ec8c3f9e26fccbc194c6a7b01baf7',
)
# The .NET 8 SDK shader_rules.py builds USCSandbox with, in tools/external/dotnet: Microsoft's zip of the
# release, pinned to the SHA-512 in Microsoft's release metadata
# (builds.dotnet.microsoft.com/dotnet/release-metadata/8.0/releases.json).
DOTNET_VERSION = '8.0.425'
DOTNET_ZIP = (
    f'https://builds.dotnet.microsoft.com/dotnet/Sdk/{DOTNET_VERSION}/dotnet-sdk-{DOTNET_VERSION}-win-x64.zip',
    'f0b6f15bf6f1a0507205c0cb102ab99e1dee875c4682c8ed94665be1d580186a06b21455e83b3a01a0ff7f4cd887b67420f2e2fe09ed985534a4cea488ae1af9',
)
DOTNET = EXTERNAL / 'dotnet'

GAME_DIR = STEAM_GAME.name
GAME_EXE = 'Crow Country.exe'
GAME_DATA = 'Crow Country_Data'
# The game's GOG product ID, and the folder GOG installs it in by default.
GOG_ID = '1109825617'
GOG_GAME = Path(r'C:\GOG Games\Crow Country')
# The game code the rules are written for, by the SHA-256 of Crow Country_Data/Managed/Assembly-CSharp.dll:
# the Steam release of build 18838972. Another release may work, but the rules weren't checked on it.
GAME_CODE = {
    'cc90ced94d880c2d3b2e4defa8e150c726cd4f65d02e541f23c48c864d756997': 'Steam build 18838972',
}

# Where setup.py extracts an SDK archive by default; the SDK is its sdk folder.
VITA_SDK_FOLDER = Path(r'C:\vitasdk')
# The PS Vita SDK files Unity's PS Vita module checks before a build: under SCE_PSP2_SDK_DIR ...
SDK_TOOLS = (
    'host_tools/build/bin/psp2snc.exe',
    'host_tools/build/bin/psp2ld.exe',
    'host_tools/build/bin/arm-eabi-as.exe',
    'host_tools/build/bin/psp2snarl.exe',
    'host_tools/build/bin/psp2bin.exe',
    'host_tools/bin/psp2psarc.exe',
)
SDK_VERSION_FILE = 'target/include/sdk_version.h'
MIN_SDK = (3, 570)
# ... and under SCE_ROOT_DIR, from the SDK's Target Manager Server and Publishing Tools installers. The
# editor's PS Vita build looks for all three and runs psp2pubcmd (param.sfo) and psp2ctrl.
SCE_TOOLS = (
    'PSP2/Tools/Target Manager Server/bin/psp2ctrl.exe',
    'PSP2/Tools/Target Manager Server/bin/psp2run.exe',
    'PSP2/Tools/Publishing Tools/bin/psp2pubcmd.exe',
)
# The SDK's installers of those tools, and what each installs.
SCE_INSTALLERS = {'TMServer*.msi': SCE_TOOLS[:2], 'PublishingTools*.msi': SCE_TOOLS[2:]}
# What setup.py downloads when the machine has no SDK: SDK 3.570 and the two installers from archive.org's
# copy of the DevNet release, pinned to the SHA-1 in the item's metadata (archive.org lists no SHA-256).
VITA_SDK_ITEM = 'https://archive.org/download/psvita-sdk-3.570-devnet-full/SDK%20Software'
VITA_SDK_ZIP = (f'{VITA_SDK_ITEM}/PSVITA.zip', 'fbb3a9dfa24bc886eff02805e3005abdec755cb8')
VITA_SDK_DOWNLOADED = VITA_SDK_FOLDER / 'PSVITA' / 'sdk'  # where VITA_SDK_ZIP's SDK lands
# That SDK is 3.570.011. Sony's patches bring it to 3.570.071, The Drifter's port's version, applied over
# it in this order: the shader compiler psp2cgc (3.570.021), then libc (3.570.071).
VITA_SDK_PATCHED = (3, 570, 71)
_PATCHES = f'{VITA_SDK_ITEM}/Tools/Plugins%20-%20Addons/SDK%20Patches/%5BSDK%5D%20SDK%203.570%20Patch%20-%20'
VITA_SDK_PATCHES = (
    (f'{_PATCHES}psp2cgc-3.570.021/SDK-3_570_021-patch.zip', '5ecc5d6cefbeaa527a139a331d65d048d2238185'),
    (f'{_PATCHES}libc-3.570.071/SDK-3_570_071-P03460462-patch.zip', 'bd76d99979ba2d4881b533ca20c48a12edc01a2d'),
)
SCE_INSTALLER_DOWNLOADS = {
    'TMServer*.msi': (
        f'{VITA_SDK_ITEM}/Tools/Installers/Target%20Manager%20Server/TMServer-3_60_0_2.msi',
        '0ad5c3dd4efaeb9efc43675a3f94fc93e192abc6',
    ),
    'PublishingTools*.msi': (
        f'{VITA_SDK_ITEM}/Tools/Installers/Publishing%20Tools/PublishingTools-2_31_0_1669.msi',
        'ba551ea6ef78e8635e0f7b03b8b383e93b2157fd',
    ),
}

# Free space a build needs on the repository's drive, in GB, from this pipeline's outputs: the
# AssetRipper export (work/rip-*, 2.6 GB, shared by the targets), the assembled project with Unity's
# Library (Vita 5.4 GB, Windows 6.3 GB), the player (Vita 1.5 GB and a 0.5 GB VPK, Windows Mono 1.2 GB,
# IL2CPP 2.1 GB) and Unity's temporary files during the build. A rebuild only rewrites the player and
# the files that changed.
FIRST_BUILD_GB = {'windows': 13, 'il2cpp': 14, 'vita': 13}
REBUILD_GB = {'windows': 4, 'il2cpp': 5, 'vita': 4}
RIP_GB = 3  # the export, counted once when several targets are built
# The editor and its modules (3.9 GB with both, installers included while they run).
EDITOR_GB = 5
LICENSE_CHECK = 'Unity license (batch mode)'
LICENSE_CHECK_SECONDS = 900  # how long the editor gets to start and quit in the license check
# Passed license checks, one line per editor build and target, so setup.py doesn't start the editor every run.
LICENSE_STAMP = WORK / 'license-check.ok'


@dataclass
class Check:
    name: str
    ok: bool
    detail: str = ''
    fix: str = ''
    required: bool = True


# ---------------------------------------------------------------- pure helpers


def parse_sdk_version(text):
    """(3, 570, 71) from sdk_version.h's '#define SCE_PSP2_SDK_VERSION 0x03570071', or None."""
    match = re.search(r'#define\s+SCE_PSP2_SDK_VERSION\s+0x([0-9A-Fa-f]{8})\b', text)
    if not match:
        return None
    digits = match.group(1)
    return int(digits[0:2]), int(digits[2:5]), int(digits[5:8])


def format_sdk_version(version):
    return f'{version[0]}.{version[1]:03d}.{version[2]:03d}'


def parse_library_folders(text):
    """The library paths in Steam's steamapps/libraryfolders.vdf (both the old and the new format)."""
    paths = [m.group(1) for m in re.finditer(r'"path"\s+"((?:[^"\\]|\\.)*)"', text)]
    if not paths:  # before 2021: "1" "D:\\SteamLibrary"
        paths = [m.group(1) for m in re.finditer(r'^\s*"\d+"\s+"((?:[^"\\]|\\.)*)"\s*$', text, re.M)]
    return [p.replace('\\\\', '\\') for p in paths]


def game_candidates(steam_roots, read_text):
    """Where the game can be: every Steam library of every Steam install, in order, without repeats.
    read_text(path) returns a file's text or None."""
    found = []
    for root in steam_roots:
        libraries = [root]
        text = read_text(Path(root) / 'steamapps' / 'libraryfolders.vdf')
        if text:
            libraries += parse_library_folders(text)
        for library in libraries:
            candidate = Path(library) / 'steamapps' / 'common' / GAME_DIR
            if all(os.path.normcase(str(candidate)) != os.path.normcase(str(f)) for f in found):
                found.append(candidate)
    return found


def unity_version(header: bytes):
    """The Unity version in the header of a serialized file such as globalgamemanagers."""
    match = re.search(rb'(\d{4}\.\d+\.\d+[abfp]\d+)\0', header[:512])
    return match.group(1).decode() if match else None


def signer_is_unity(status, subject):
    """Get-AuthenticodeSignature's Status and SignerCertificate.Subject of a Unity installer."""
    return (
        status == 'Valid' and re.search(r'(^|,\s*)O=Unity Technologies( ApS| SF)?\s*(,|$)', subject, re.I) is not None
    )


def license_log_problem(text, returncode, platform):
    """Why a short batch-mode run of the editor failed, or None if it worked."""
    for pattern in (
        'No valid Unity Editor license',
        'Failed to activate/update license',
        'License is not active',
        'not licensed',
        'Aborting batchmode due to failure',
    ):
        if pattern.lower() in text.lower():
            line = next(candidate for candidate in text.splitlines() if pattern.lower() in candidate.lower())
            return line.strip()[:200]
    if returncode != 0:
        return f'the editor exited with code {returncode}'
    if platform == 'vita' and not re.search(r'Register platform support module: .*PSP2Player', text):
        return 'the editor did not load its PS Vita module'
    return None


def missing(root: Path, relatives):
    return [r for r in relatives if not (root / r).exists()]


def find_sdk_root(folder: Path, depth=4):
    """The folder holding host_tools/ and target/ (what SCE_PSP2_SDK_DIR names) in or below folder."""
    folder = Path(folder)
    if (folder / SDK_TOOLS[0]).exists():
        return folder
    if depth == 0 or not folder.is_dir():
        return None
    try:
        children = sorted(p for p in folder.iterdir() if p.is_dir())
    except OSError:
        return None
    for child in children:
        found = find_sdk_root(child, depth - 1)
        if found:
            return found
    return None


STATUS_STYLES = {'ok': 'green', 'MISSING': 'bold red', 'optional': 'yellow'}


def summarize(checks):
    """The checklist as lines (Text, each status in its colour), and the number of required items that
    failed."""
    lines, failed = [], 0
    width = max((len(c.name) for c in checks), default=0)
    for check in checks:
        status = 'ok' if check.ok else ('MISSING' if check.required else 'optional')
        failed += not check.ok and check.required
        line = Text(f'  [{status:^8}] {check.name:<{width}}  {check.detail}'.rstrip())
        line.stylize(STATUS_STYLES[status], 2, 12)
        lines.append(line)
        if not check.ok and check.fix:
            lines.append(Text(f'  {"":10} {"":<{width}}  fix: {check.fix}'))
    return lines, failed


# ---------------------------------------------------------------- the machine


def steam_roots():
    import winreg

    roots = [str(STEAM)]
    for key, path, name in (
        (winreg.HKEY_CURRENT_USER, r'Software\Valve\Steam', 'SteamPath'),
        (winreg.HKEY_LOCAL_MACHINE, r'SOFTWARE\WOW6432Node\Valve\Steam', 'InstallPath'),
        (winreg.HKEY_LOCAL_MACHINE, r'SOFTWARE\Valve\Steam', 'InstallPath'),
    ):
        try:
            with winreg.OpenKey(key, path) as handle:
                roots.append(str(Path(winreg.QueryValueEx(handle, name)[0])))
        except OSError:
            pass
    return roots


def read_text(path: Path):
    try:
        return path.read_text(encoding='utf-8', errors='replace')
    except OSError:
        return None


def steam_game():
    """The game's folder in the first Steam library that has it, or None."""
    return next((c for c in game_candidates(steam_roots(), read_text) if (c / GAME_EXE).exists()), None)


def gog_game():
    """The game's GOG install, from the key GOG's installer writes, else GOG's default folder; or None."""
    import winreg

    folders = []
    for path in (rf'SOFTWARE\WOW6432Node\GOG.com\Games\{GOG_ID}', rf'SOFTWARE\GOG.com\Games\{GOG_ID}'):
        try:
            with winreg.OpenKey(winreg.HKEY_LOCAL_MACHINE, path) as handle:
                folders.append(Path(winreg.QueryValueEx(handle, 'path')[0]))
        except OSError:
            pass
    return next((f for f in [*folders, GOG_GAME] if (f / GAME_EXE).exists()), None)


def find_game():
    """The game's folder in a Steam library, else its GOG install, or None."""
    return steam_game() or gog_game()


def steam_build(game: Path):
    """The Steam build ID of the install in game, from its library's appmanifest, or None."""
    text = read_text(game.parent.parent / f'appmanifest_{STEAM_APP_ID}.acf')
    match = re.search(r'"buildid"\s+"(\d+)"', text or '')
    return match.group(1) if match else None


def sdk_version(sdk: Path):
    text = read_text(sdk / SDK_VERSION_FILE)
    return parse_sdk_version(text) if text else None


def sce_root_candidates():
    value, _ = env('SCE_ROOT_DIR')
    candidates = [Path(value)] if value else []
    candidates += [
        Path(os.environ[variable]) / 'SCE'
        for variable in ('ProgramFiles(x86)', 'ProgramFiles')
        if os.environ.get(variable)
    ]
    return candidates


def sce_root():
    """The first SCE folder with the Target Manager tools, or None."""
    return next((c for c in sce_root_candidates() if not missing(c, SCE_TOOLS)), None)


def sdk_status(sdk: Path):
    """(what is wrong with the SDK in a folder or None, its version as shown)."""
    gaps = missing(sdk, SDK_TOOLS)
    version = sdk_version(sdk)
    shown = format_sdk_version(version) if version else f'unknown, no {SDK_VERSION_FILE}'
    if gaps:
        return f'{sdk} lacks ' + ', '.join(Path(g).name for g in gaps), shown
    if version and version[:2] < MIN_SDK:
        return f'{sdk}: version {shown}; Unity needs {MIN_SDK[0]}.{MIN_SDK[1]} or later', shown
    return None, shown


def sdk_candidates():
    value, _ = env('SCE_PSP2_SDK_DIR')
    candidates = [Path(value)] if value else []
    candidates += [VITA_SDK_FOLDER / 'sdk', VITA_SDK_DOWNLOADED]
    for root in sce_root_candidates():
        candidates += sorted((root / 'PSP2 SDKs').glob('*'), reverse=True)
    return candidates


def dotnet35():
    """Whether .NET Framework 3.5 is on. Publishing Tools' installer needs it; a new Windows has it off."""
    import winreg

    try:
        with winreg.OpenKey(winreg.HKEY_LOCAL_MACHINE, r'SOFTWARE\Microsoft\NET Framework Setup\NDP\v3.5') as key:
            return winreg.QueryValueEx(key, 'Install')[0] == 1
    except OSError:
        return False


def dotnet_sdks():
    """The SDK versions in tools/external/dotnet."""
    folder = DOTNET / 'sdk'
    return sorted(p.name for p in folder.iterdir() if p.is_dir()) if folder.is_dir() else []


def visual_cpp():
    """The installation path of a Visual Studio (or Build Tools) with the C++ x64 tools, or None."""
    vswhere = (
        Path(os.environ.get('PROGRAMFILES(X86)', r'C:\Program Files (x86)'))
        / 'Microsoft Visual Studio'
        / 'Installer'
        / 'vswhere.exe'
    )
    if not vswhere.exists():
        return None
    result = subprocess.run(
        [
            str(vswhere),
            '-products',
            '*',
            '-latest',
            '-requires',
            'Microsoft.VisualStudio.Component.VC.Tools.x86.x64',
            '-property',
            'installationPath',
        ],
        capture_output=True,
        text=True,
    )
    return result.stdout.strip() or None


def free_gb(path: Path):
    while not path.exists():
        path = path.parent
    return shutil.disk_usage(path).free / 2**30


# ---------------------------------------------------------------- checks


PYTHON_MODULES = {
    'numpy': 'numpy',
    'PIL': 'Pillow',
    'UnityPy': 'UnityPy',
    'yaml': 'PyYAML',
    'av': 'PyAV',
    'Crypto': 'pycryptodome',
    'truststore': 'truststore',
}


def check_python():
    for module, package in PYTHON_MODULES.items():
        if importlib.util.find_spec(module) is None:
            return Check('Python packages', False, f'no {package} (module {module!r})', 'uv sync')
    return Check('Python packages', True, ', '.join(PYTHON_MODULES.values()))


def check_csc():
    return Check(
        '.NET Framework C# compiler',
        CSC.exists(),
        str(CSC),
        'part of Windows (.NET Framework 4.8); turn it on in Windows features',
    )


def check_game(game: Path):
    name = 'Crow Country'
    fix = f'{SETUP} --game <the folder with {GAME_EXE}>'
    if not (game / GAME_EXE).exists():
        found = find_game()
        hint = f'; found in {found}' if found and found != game else ''
        return Check(name, False, f'no {GAME_EXE} in {game}{hint}', fix)
    data = game / GAME_DATA
    try:
        with (data / 'globalgamemanagers').open('rb') as handle:
            version = unity_version(handle.read(512))
    except OSError:
        version = None
    if version != SOURCE_VERSION:
        return Check(name, False, f'{game}: built with Unity {version or "?"}, the rules expect {SOURCE_VERSION}', fix)
    code = data / 'Managed' / 'Assembly-CSharp.dll'
    if not code.exists():
        return Check(name, False, f'{game}: no {GAME_DATA}/Managed/Assembly-CSharp.dll (not a Mono build)', fix)
    release = GAME_CODE.get(file_hash(code))
    if release is None:
        build = steam_build(game)
        return Check(
            name,
            False,
            f'{game} ({f"Steam build {build}, " if build else ""}Unity {version}): another release than the '
            f'rules were written for ({", ".join(GAME_CODE.values())}); it may build, but nothing checked it',
            'update the game, or have Steam verify its files',
            required=False,
        )
    return Check(name, True, f'{game} ({release}, Unity {version})')


def check_assetripper():
    return Check('AssetRipper 2.0.0', ASSETRIPPER.exists(), str(ASSETRIPPER.parent), SETUP)


def check_dotnet():
    """The .NET 8 SDK shader_rules.py builds USCSandbox with."""
    sdks = [v for v in dotnet_sdks() if v.startswith('8.')]
    ok = (DOTNET / 'dotnet.exe').exists() and bool(sdks)
    return Check(
        '.NET 8 SDK',
        ok,
        f'{DOTNET} (SDK {", ".join(sdks)})' if ok else f'no .NET 8 SDK in {DOTNET}',
        SETUP,
    )


def check_cecil():
    return Check(
        'Mono.Cecil',
        CECIL.exists(),
        str(CECIL.parent) if CECIL.exists() else 'not yet: pipeline.py code fetches it from NuGet on first use',
        SETUP,
        required=False,
    )


def check_uscsandbox():
    exe = EXTERNAL / 'USCSandbox' / 'USCSandbox' / 'bin' / 'Release' / 'net8.0' / 'USCSandbox.exe'
    return Check(
        'USCSandbox',
        exe.exists(),
        str(exe.parents[4]) if exe.exists() else 'not yet: pipeline.py project builds it on first use',
        'uv run tools/downgrade/pipeline.py project (builds it with the .NET 8 SDK)',
        required=False,
    )


def check_editor(module='editor'):
    root = EDITORS / TARGET
    fix = SETUP if module == 'editor' else f'{SETUP} --{module}'
    path = root / EDITOR_FILES[module]
    return Check(module_name(module, TARGET), path.exists(), str(root if module == 'editor' else path), fix)


def check_editor_data(version, vita):
    """The editor files the pipeline reads (common.editor_data)."""
    data = editor_data(version)
    if not data.exists():
        return Check("Unity's files", False, f'no {data} (comes with the editor)', SETUP)
    gaps = missing(data, EDITOR_DATA_CHECKED + (EDITOR_DATA_CHECKED_VITA if vita else []))
    return Check(
        "Unity's files",
        not gaps,
        f'{data} lacks ' + ', '.join(gaps) if gaps else str(data),
        f'{SETUP}{" --vita" if vita else ""}',
    )


def check_visual_cpp():
    path = visual_cpp()
    return Check(
        'Visual Studio C++ tools',
        bool(path),
        path or 'not found (IL2CPP compiles the Windows player with them)',
        'winget install Microsoft.VisualStudio.2022.BuildTools --override '
        '"--quiet --wait --add Microsoft.VisualStudio.Workload.VCTools --includeRecommended"',
    )


def check_vita_sdk():
    """The PS Vita SDK and the Target Manager tools, through the variables Unity reads."""
    fix = f'{SETUP} --vita (downloads the SDK), or --vita-sdk <your SDK archive or folder>'
    checks = []
    sdk, stale = env('SCE_PSP2_SDK_DIR')
    if not sdk:
        found = next((c for c in sdk_candidates() if (c / SDK_TOOLS[0]).exists()), None)
        checks.append(
            Check(
                'PS Vita SDK',
                False,
                'SCE_PSP2_SDK_DIR is not set' + (f'; an SDK is in {found}' if found else ''),
                f'{SETUP} --vita-sdk "{found}"' if found else fix,
            )
        )
    else:
        problem, shown = sdk_status(Path(sdk))
        checks.append(
            Check('PS Vita SDK', False, problem, fix)
            if problem
            else Check(
                'PS Vita SDK',
                True,
                f'{sdk} (version {shown})' + (' - set for new shells; this one does not see it' if stale else ''),
            )
        )
    fix = f'{SETUP} --vita (installs Target Manager Server and Publishing Tools from the SDK)'
    root, stale = env('SCE_ROOT_DIR')
    if not root:
        found = sce_root()
        checks.append(
            Check(
                'Target Manager (SCE_ROOT_DIR)',
                False,
                'SCE_ROOT_DIR is not set' + (f'; the tools are in {found}' if found else ''),
                fix,
            )
        )
    else:
        gaps = missing(Path(root), SCE_TOOLS)
        checks.append(
            Check(
                'Target Manager (SCE_ROOT_DIR)',
                not gaps,
                f'{root} lacks ' + ', '.join(Path(g).name for g in gaps)
                if gaps
                else str(root) + (' - set for new shells; this one does not see it' if stale else ''),
                fix,
            )
        )
    return checks


def vita_host_setting():
    """(variable, value) of the console's address: CROW_VITA_HOST, else DRIFTER_VITA_HOST (common.VITA_HOST)."""
    for name in ('CROW_VITA_HOST', 'DRIFTER_VITA_HOST'):
        value, _ = env(name)
        if value:
            return name, value
    return None, None


def check_vita_host():
    name, value = vita_host_setting()
    return Check(
        'Console address',
        bool(value),
        f'{name}={value}' if value else "CROW_VITA_HOST is not set (vita.py needs the console's address)",
        f"{SETUP} --vita-host <the console's IP address>",
        required=False,
    )


def space_needed(targets):
    """GB a build of the targets needs: a first build's or a rebuild's for each, by whether its project
    is there, with the export they share counted once."""
    need = 0
    for target in targets:
        exists = project_dir(TARGET, 'vita' if target == 'vita' else 'windows').exists()
        need += (REBUILD_GB if exists else FIRST_BUILD_GB)[target]
    shared = (len(targets) - 1) * RIP_GB if not (WORK / f'rip-{TARGET}').exists() else 0
    return need - shared


def check_space(targets):
    need = space_needed(targets)
    free = free_gb(WORK)
    return Check(
        'Free disk space',
        free >= need,
        f'{free:.0f} GB free on {ROOT.anchor}; building {", ".join(targets)} from here needs about {need} GB',
        f'free space on {ROOT.anchor} or move the repository to a larger drive',
    )


def check_license(version, platform):
    """Start the editor in batch mode with a new empty project and read its log (about a minute)."""
    unity = EDITORS / version / EDITOR_FILES['editor']
    name = LICENSE_CHECK
    fix = (
        'activate a license in the editor, or remove its license check with unigate '
        f'(git clone https://github.com/xob0t/unigate, then in its folder: uv run unigate.py --editor '
        f'"{unity.parent}")'
    )
    if not unity.exists():
        return Check(name, False, 'no editor', SETUP)
    with tempfile.TemporaryDirectory(prefix='crow-license-') as temp:
        log_path = Path(temp) / 'editor.log'
        try:
            result = subprocess.run(
                [
                    str(unity),
                    '-batchmode',
                    '-nographics',
                    '-quit',
                    '-createProject',
                    str(Path(temp) / 'project'),
                    '-buildTarget',
                    'PSP2' if platform == 'vita' else 'Win64',
                    '-logFile',
                    str(log_path),
                ],
                timeout=LICENSE_CHECK_SECONDS,
            )
            code = result.returncode
        except subprocess.TimeoutExpired:
            code = None
        text = read_text(log_path) or ''
        problem = (
            license_log_problem(text, code, platform)
            if code is not None
            else f'the editor did not quit within {LICENSE_CHECK_SECONDS // 60} minutes'
        )
        if problem and text:
            kept = WORK / 'license-check.log'
            kept.parent.mkdir(parents=True, exist_ok=True)
            kept.write_text(text, encoding='utf-8')
            problem += f' (log: {kept})'
    if problem is None:
        LICENSE_STAMP.parent.mkdir(parents=True, exist_ok=True)
        with LICENSE_STAMP.open('a', encoding='utf-8') as handle:
            handle.write(license_key(version, platform) + '\n')
    return Check(name, problem is None, problem or f'the editor starts in batch mode with the {platform} target', fix)


def license_key(version, platform):
    unity = EDITORS / version / EDITOR_FILES['editor']
    return f'{version} {platform} {unity.stat().st_mtime_ns if unity.exists() else 0}'


def license_checked(version, platform):
    text = read_text(LICENSE_STAMP)
    return bool(text) and license_key(version, platform) in text.splitlines()


def license_status(platform, version, license):
    """license: the check setup.py has just run, True to run it now, or False to report the last one."""
    if isinstance(license, Check):
        return license
    if license:
        return check_license(version, platform)
    if license_checked(version, platform):
        return Check(LICENSE_CHECK, True, f'the editor ran in batch mode with the {platform} target when last checked')
    return Check(LICENSE_CHECK, True, f'not checked; {SETUP} doctor --license starts the editor once')


# ---------------------------------------------------------------- targets

TARGETS = ('windows', 'il2cpp', 'vita')


def project_dir(version, platform):
    return WORK / (f'crow-{version}' + ('-vita' if platform == 'vita' else ''))


def checks_for(targets, game: Path, license=False):
    """Every requirement of the targets, for the doctor's full checklist."""
    version = TARGET
    vita = 'vita' in targets
    checks = [
        check_python(),
        check_csc(),
        check_game(game),
        check_assetripper(),
        check_dotnet(),
        check_cecil(),
        check_uscsandbox(),
        check_editor(),
    ]
    if 'il2cpp' in targets:
        checks += [check_editor('il2cpp'), check_visual_cpp()]
    if vita:
        checks.append(check_editor('vita'))
    checks.append(check_editor_data(version, vita))
    if vita:
        checks += [*check_vita_sdk(), check_vita_host()]
    checks.append(check_space(targets))
    # The license check starts the editor with the PS Vita target when the targets include it (that also
    # checks the module loads), else the Windows one.
    checks.append(license_status('vita' if vita else 'windows', version, license))
    return checks


def main(argv=None, license_check=None):
    parser = argparse.ArgumentParser(
        prog='setup.py doctor', description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter
    )
    parser.add_argument(
        '--target',
        choices=('all', *TARGETS),
        action='append',
        help='vita (the VPK), windows (Mono player), il2cpp (Windows IL2CPP player), or all three (the '
        'default); repeat it for two',
    )
    parser.add_argument('--license', action='store_true', help='start the editor once in batch mode (about a minute)')
    args = parser.parse_args(argv)
    chosen = args.target or ['all']
    targets = TARGETS if 'all' in chosen else tuple(t for t in TARGETS if t in chosen)
    checks = checks_for(targets, default_game(), license=license_check or args.license)
    lines, failed = summarize(checks)
    title = 'every target' if targets == TARGETS else f'the {" and ".join(targets)} target' + 's' * (len(targets) > 1)
    for line in [Text(f'Requirements for {title}:'), *lines]:
        progress.console.print(line)
    if failed:
        progress.console.print(Text(f'{failed} required item(s) missing.', 'bold red'))
        return 1
    progress.console.print(Text('Everything required is in place.', 'bold green'))
    return 0


if __name__ == '__main__':
    sys.exit(main())
