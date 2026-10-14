"""Build the CrowClock native plugin for the PS Vita (clock.c): CrowClock.suprx and its CrowClock_stub.a,
in work/native/clock, with the official SDK's psp2snc (SCE_PSP2_SDK_DIR). Also the bootstrap module
the editor would make from the two (CrowClock.suprx.b.suprx): IL2CPP's [DllImport] on the PS Vita
(libil2cpp/os/PSP2/dlopen.cpp) loads app0:/Media/Plugins/<name>.suprx, then <name>.suprx.b.suprx,
whose module_start registers the name and address of each function with RegisterModule.

The SDK's libScePower_stub.a has no clock functions. Each of its function stubs is an object that
names the function and holds its NID; scePowerSetConfigurationMode's name has the same length (28)
as scePowerSetArmClockFrequency and scePowerGetArmClockFrequency, so a copy of its object with the
name and NID replaced imports those (the firmware's user ScePower library exports them). The NIDs
object (absolute _NID_ symbols) gets the same treatment, its other names made unique per copy.

    uv run tools/downgrade/native/clock/build.py
"""

import os
import shutil
import subprocess
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[3]
OUT = ROOT / 'work' / 'native' / 'clock'
SDK = Path(os.environ.get('SCE_PSP2_SDK_DIR', r'C:\vitasdk\sdk'))
BIN = SDK / 'host_tools' / 'build' / 'bin'

BASE_NAME = b'scePowerSetConfigurationMode'
BASE_NID = 0x3CE187B6
# The functions to import, by NID (vitasdk's db): name lengths must equal BASE_NAME's.
FUNCTIONS = {
    b'scePowerSetArmClockFrequency': 0x74DB5AE5,
    b'scePowerGetArmClockFrequency': 0xABC6F88F,
}


# The editor's bootstrap source for a plugin (UnityEditor.PSP2.Extensions), for CrowClock's functions.
BRIDGE = r"""#include <stddef.h>
#include <kernel.h>
#include <moduleinfo.h>
SCE_MODULE_INFO(CrowClock_b, SCE_MODULE_ATTR_NONE, 1, 1);
extern "C" int CrowClockSetArm();
extern "C" int CrowClockGetArm();
typedef int (*EXTERNFUNCTION)();
static struct FunctionMap { const char* name; EXTERNFUNCTION address; } s_FunctionNames[] = {
  { "CrowClockSetArm", &CrowClockSetArm },
  { "CrowClockGetArm", &CrowClockGetArm },
};
extern "C" __declspec(dllimport) void RegisterModule(const char* module_name, void* exports,
  size_t exports_count, SceSize size, void* arg);
extern "C" int module_start(SceSize sz, const void* arg) {
  RegisterModule("CrowClock", s_FunctionNames, sizeof(s_FunctionNames) / sizeof(s_FunctionNames[0]),
    sz, (void*)arg);
  return SCE_KERNEL_START_SUCCESS;
}
"""


def run(*command, cwd=OUT):
    result = subprocess.run([str(c) for c in command], cwd=cwd, capture_output=True, text=True)
    if result.returncode:
        sys.exit(f'{Path(command[0]).name} failed ({result.returncode}):\n{result.stdout}{result.stderr}')
    return result.stdout


def patch(data: bytes, name: bytes, nid: int, mangle: bytes | None = None) -> bytes:
    old, new = BASE_NID.to_bytes(4, 'little'), nid.to_bytes(4, 'little')
    if data.count(old) < 1 or BASE_NAME not in data:
        sys.exit('libScePower_stub.a is not the one this script knows')
    data = data.replace(BASE_NAME, name).replace(old, new)
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
    function = (extract / f'_ScePower-0001_F00_{BASE_NID:08x}.o').read_bytes()
    members = [head]
    for index, (name, nid) in enumerate(FUNCTIONS.items()):
        f = extract / f'_ScePower-0001_F00_{nid:08x}.o'  # psp2ld wants this name
        f.write_bytes(patch(function, name, nid))
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
    OUT.mkdir(parents=True, exist_ok=True)
    library = stub_library(OUT)
    run(BIN / 'psp2snc.exe', '-O2', '-Xmserrors', '-c', HERE / 'clock.c', '-o', OUT / 'clock.o')
    stubs = OUT / 'stubs'
    shutil.rmtree(stubs, ignore_errors=True)
    stubs.mkdir()
    run(
        BIN / 'psp2snc.exe',
        '-Wl,--oformat=prx',
        f'-Wl,--prx-stub-output-dir={stubs}',
        OUT / 'clock.o',
        library,
        f'-Wl,-Map={OUT / "CrowClock.map"}',
        '-o',
        OUT / 'CrowClock.suprx',
    )
    plugin_stub = stubs / 'CrowClock_stub.a'
    bridge = OUT / 'bridge.cpp'
    bridge.write_text(BRIDGE, encoding='utf-8')
    run(BIN / 'psp2snc.exe', '-O2', '-Xmserrors', '-c', bridge, '-o', OUT / 'bridge.o')
    modules = Path(os.environ['LOCALAPPDATA']) / 'Unity' / 'Editors' / '2018.2.19f1' / 'Editor' / 'Data'
    modules = modules / 'PlaybackEngines' / 'PSP2Player' / 'Data' / 'Modules'
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
    print(f'{OUT / "CrowClock.suprx"}; stubs: {", ".join(made)}')


if __name__ == '__main__':
    main()
