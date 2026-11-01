"""Code the PS Vita's player never runs, left out of its IL2CPP module (TrimAssemblies.cs).

Unity's linker keeps the script assemblies whole (they are its roots), so IL2CPP converts every
method in them, and the module is loaded whole into the console's memory. Most of the game's
PlayMaker actions are never used: 1,141 of the 1,339 action classes in the game assembly (and most
of Rewired's PlayMaker actions in firstpass) are named by no FSM in any scene, prefab or asset.
PlayMaker creates an FSM's actions from the type names in its data (ActionData.actionNames), so an
action no data names is only reachable from code; TrimAssemblies removes the ones no remaining code
references either, and SmoothSave, which only those actions use (its XmlSerializer calls kept
System.Xml's serializer in the module). It also empties two methods: Rewired's XmlSerializer helper,
which nothing calls, and the firstpass's Windows platform initializer, which then returns null as
on a PS Vita build of Rewired, so the linker drops Rewired_Windows. Measured on the build's
UnityLinker and IL2CPP steps: 488.6 -> 396.7 MB of C++, global-metadata.dat 12.6 -> 10.0 MB. The
serialized layout stays as it was, so quickcode still applies it."""

import subprocess
from pathlib import Path

from common import ARTIFACTS, WORK, log, scan

# Platforms whose assemblies are trimmed: the PS Vita, where the module's size is memory.
TRIMMED_ON = {'vita'}
FSM_FILES = ('.unity', '.prefab', '.asset')
ACTION_NAMES = b'actionNames:\n'


def action_names(path: Path):
    """The action type names of the FSMs in a YAML file: each actionNames list's items."""
    data = path.read_bytes()
    if ACTION_NAMES not in data:
        return []
    names = set()
    start = data.find(ACTION_NAMES)
    while start >= 0:
        # The list's items follow at the key's indentation: "<indent>- <name>".
        line_start = data.rfind(b'\n', 0, start) + 1
        prefix = data[line_start:start] + b'- '
        position = start + len(ACTION_NAMES)
        while data.startswith(prefix, position):
            end = data.find(b'\n', position)
            end = len(data) if end < 0 else end
            names.add(data[position + len(prefix) : end].strip().strip(b'\'"').decode('utf-8'))
            position = end + 1
        start = data.find(ACTION_NAMES, position)
    return sorted(names)


def used_actions(exported: Path):
    """Every PlayMaker action type name the export's scenes, prefabs and assets use."""
    found = scan('fsm-action-names', exported / 'Assets', lambda relative: relative.suffix in FSM_FILES, action_names)
    return sorted({name for names in found.values() for name in names})


def trim_assemblies(folder: Path, version: str, references: list):
    """TrimAssemblies (compiled by pipeline.compile_tools) on the rewritten assemblies in folder, with
    the action names the export's data uses. Its output goes to artifacts/downgrade/trim.log."""
    exported = WORK / f'rip-{version}' / 'ExportedProject'
    if not (exported / 'Assets').exists():
        raise SystemExit(f'No export in {exported} to read the FSMs from; run pipeline.py rip first')
    names = used_actions(exported)
    listed = ARTIFACTS / 'used-actions.txt'
    listed.write_text('\n'.join(names) + '\n', encoding='utf-8')
    command = [WORK / 'bin' / 'TrimAssemblies.exe', folder, listed, *references]
    result = subprocess.run([str(c) for c in command], capture_output=True, text=True, check=False)
    (ARTIFACTS / 'trim.log').write_text(result.stdout + result.stderr, encoding='utf-8')
    if result.returncode:
        raise SystemExit(f'TrimAssemblies failed ({result.returncode}); see {ARTIFACTS / "trim.log"}')
    last = (result.stdout.strip().splitlines() or ['(no output)'])[-1]
    log(f'{len(names)} PlayMaker action types in the data. {last}')
