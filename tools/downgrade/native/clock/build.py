"""Build the CrowClock native plugin for the PS Vita (clock.c, threads.c): CrowClock.suprx and its CrowClock_stub.a,
in work/native/clock, with the official SDK's psp2snc (SCE_PSP2_SDK_DIR). Also the bootstrap module
the editor would make from the two (CrowClock.suprx.b.suprx): IL2CPP's [DllImport] on the PS Vita
(libil2cpp/os/PSP2/dlopen.cpp) loads app0:/Media/Plugins/<name>.suprx, then <name>.suprx.b.suprx,
whose module_start registers the name and address of each function with RegisterModule.

The SDK's libScePower_stub.a has no clock functions. Each of its function stubs is an object that
names the function and holds its NID; scePowerSetConfigurationMode's name has the same length (28)
as scePowerSetArmClockFrequency and scePowerGetArmClockFrequency, so a copy of its object with the
name and NID replaced imports those (the firmware's user ScePower library exports them); the 32-character
crossbar ones are copies of scePowerGetBatteryRemainCapacity's. The NIDs
object (absolute _NID_ symbols) gets the same treatment, its other names made unique per copy.

    uv run tools/downgrade/native/clock/build.py
"""

import hashlib
import re
import shutil
import subprocess
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE.parents[1]))
from common import TARGET, WORK, editor_data, setting  # noqa: E402

OUT = WORK / 'native' / 'clock'
# The SDK setup.py installs (SCE_PSP2_SDK_DIR, read from the registry when this shell lacks it).
SDK = Path(setting('SCE_PSP2_SDK_DIR') or r'C:\vitasdk\PSVITA\sdk')
BIN = SDK / 'host_tools' / 'build' / 'bin'
SOURCES = [HERE / 'clock.c', HERE / 'threads.c']
# The functions the plugin exports: what the sources declare PRX_EXPORT.
EXPORT = re.compile(r'^PRX_EXPORT int (\w+)\(', re.MULTILINE)

# The SDK stub each new one is copied from, by name length.
BASES = {
    28: (b'scePowerSetConfigurationMode', 0x3CE187B6),
    32: (b'scePowerGetBatteryRemainCapacity', 0x94F5A53F),
}
# The functions to import, by NID (vitasdk's db, vita-headers db/360/ScePower.yml, user library ScePower;
# the bus and crossbar ones checked against wiki.henkaku.xyz's ScePower page): name lengths must be in BASES.
FUNCTIONS = {
    b'scePowerSetArmClockFrequency': 0x74DB5AE5,
    b'scePowerGetArmClockFrequency': 0xABC6F88F,
    b'scePowerSetGpuClockFrequency': 0x717DB06C,
    b'scePowerGetGpuClockFrequency': 0x1B04A1D6,
    b'scePowerGetBusClockFrequency': 0x478FE6F5,
    b'scePowerSetGpuXbarClockFrequency': 0xA7739DBE,
    b'scePowerGetGpuXbarClockFrequency': 0x0A750DEE,
}


# The editor's bootstrap source for a plugin (UnityEditor.PSP2.Extensions), for CrowClock's exports.
BRIDGE = r"""#include <stddef.h>
#include <kernel.h>
#include <moduleinfo.h>
SCE_MODULE_INFO(CrowClock_b, SCE_MODULE_ATTR_NONE, 1, 1);
{declarations}
typedef int (*EXTERNFUNCTION)();
static struct FunctionMap {{ const char* name; EXTERNFUNCTION address; }} s_FunctionNames[] = {{
{entries}
}};
extern "C" __declspec(dllimport) void RegisterModule(const char* module_name, void* exports,
  size_t exports_count, SceSize size, void* arg);
extern "C" int module_start(SceSize sz, const void* arg) {{
  RegisterModule("CrowClock", s_FunctionNames, sizeof(s_FunctionNames) / sizeof(s_FunctionNames[0]),
    sz, (void*)arg);
  return SCE_KERNEL_START_SUCCESS;
}}
"""


def bridge_source():
    """BRIDGE for every PRX_EXPORT function of the sources: one missing from it would fail only on the
    console, as EntryPointNotFound at its first call."""
    names = [name for source in SOURCES for name in EXPORT.findall(source.read_text(encoding='utf-8'))]
    return BRIDGE.format(
        declarations='\n'.join(f'extern "C" int {name}();' for name in names),
        entries='\n'.join(f'  {{ "{name}", &{name} }},' for name in names),
    )


def run(*command, cwd=OUT):
    result = subprocess.run([str(c) for c in command], cwd=cwd, capture_output=True, text=True, check=False)
    if result.returncode:
        sys.exit(f'{Path(command[0]).name} failed ({result.returncode}):\n{result.stdout}{result.stderr}')
    return result.stdout


def patch(data: bytes, name: bytes, nid: int, mangle: bytes | None = None) -> bytes:
    base_name, base_nid = BASES[len(name)]
    old, new = base_nid.to_bytes(4, 'little'), nid.to_bytes(4, 'little')
    if data.count(old) < 1 or base_name not in data:
        sys.exit('libScePower_stub.a is not the one this script knows')
    data = data.replace(base_name, name).replace(old, new)
    if mangle:
        # Every other _NID_ symbol of the NIDs object gets a name no other copy defines.
        data = data.replace(b'_NID_', mangle).replace(mangle + name, b'_NID_' + name)
    return data


def stub_library(folder: Path) -> Path:
    stub = SDK / 'target' / 'lib' / 'libScePower_stub.a'
    extract = folder / 'power'
    shutil.rmtree(extract, ignore_errors=True)
    extract.mkdir(parents=True)
    run(BIN / 'psp2snarl.exe', 'x', stub, cwd=extract)
    head = extract / '_ScePower-0001_head.o'
    nids = (extract / '_ScePower-0001_NIDS.o').read_bytes()
    functions = {n: (extract / f'_ScePower-0001_F00_{nid:08x}.o').read_bytes() for n, (_, nid) in BASES.items()}
    members = [head]
    for index, (name, nid) in enumerate(FUNCTIONS.items()):
        f = extract / f'_ScePower-0001_F00_{nid:08x}.o'  # psp2ld wants this name
        f.write_bytes(patch(functions[len(name)], name, nid))
        n = extract / f'_ScePower-0001_NIDS_{index}.o'
        n.write_bytes(patch(nids, name, nid, b'_N%d__' % index))
        members += [f, n]
    library = folder / 'libCrowPower_stub.a'
    library.unlink(missing_ok=True)
    run(BIN / 'psp2snarl.exe', 'crs', library, *members)
    return library


def main():
    if not (BIN / 'psp2snc.exe').exists():
        sys.exit(f'No psp2snc in {BIN}; set SCE_PSP2_SDK_DIR')
    modules = editor_data(TARGET) / 'PlaybackEngines' / 'PSP2Player' / 'Data' / 'Modules'
    # The modules are rebuilt when a source, this script, the SDK or the editor's stub changes.
    digest = hashlib.sha256()
    for path in [*SOURCES, Path(__file__), BIN / 'psp2snc.exe', modules / 'Il2CppAssemblies_stub_weak.a']:
        digest.update(f'{path} {path.stat().st_size} '.encode() + hashlib.sha256(path.read_bytes()).digest())
    stamp = OUT / 'build.stamp'
    outputs = [OUT / 'CrowClock.suprx', OUT / 'CrowClock.suprx.b.suprx']
    if stamp.exists() and stamp.read_text() == digest.hexdigest() and all(p.exists() for p in outputs):
        print(f'{OUT / "CrowClock.suprx"}: up to date')
        return
    stamp.unlink(missing_ok=True)
    OUT.mkdir(parents=True, exist_ok=True)
    library = stub_library(OUT)
    run(BIN / 'psp2snc.exe', '-O2', '-Xmserrors', '-c', HERE / 'clock.c', '-o', OUT / 'clock.o')
    run(BIN / 'psp2snc.exe', '-O2', '-Xmserrors', '-c', HERE / 'threads.c', '-o', OUT / 'threads.o')
    stubs = OUT / 'stubs'
    shutil.rmtree(stubs, ignore_errors=True)
    stubs.mkdir()
    run(
        BIN / 'psp2snc.exe',
        '-Wl,--oformat=prx',
        f'-Wl,--prx-stub-output-dir={stubs}',
        OUT / 'clock.o',
        OUT / 'threads.o',
        library,
        SDK / 'target' / 'lib' / 'libSceKernel_stub.a',
        f'-Wl,-Map={OUT / "CrowClock.map"}',
        '-o',
        OUT / 'CrowClock.suprx',
    )
    plugin_stub = stubs / 'CrowClock_stub.a'
    bridge = OUT / 'bridge.cpp'
    bridge.write_text(bridge_source(), encoding='utf-8')
    run(BIN / 'psp2snc.exe', '-O2', '-Xmserrors', '-c', bridge, '-o', OUT / 'bridge.o')
    run(
        BIN / 'psp2snc.exe',
        '-Wl,--oformat=prx',
        '-Wl,--disable-warning=0431',
        OUT / 'bridge.o',
        plugin_stub,
        modules / 'Il2CppAssemblies_stub_weak.a',
        '-o',
        OUT / 'CrowClock.suprx.b.suprx',
    )
    made = sorted(p.name for p in stubs.iterdir())
    stamp.write_text(digest.hexdigest())
    print(f'{OUT / "CrowClock.suprx"}; stubs: {", ".join(made)}')


if __name__ == '__main__':
    main()
