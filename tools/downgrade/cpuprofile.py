"""Where the main thread's time goes at about the PS Vita's CPU speed: a sampling profiler on top of
vitalike.py. The Windows IL2CPP player runs throttled as vitalike runs it; each time the duty
controller suspends the player (every --period ms of wall time, so every period * cap of the
player's time), it walks the main thread's stack with dbghelp. At the end the addresses are named
from the build's GameAssembly.pdb (IL2CPP's C++ for the game's code: PlayMakerFSM_Update_m..., an
action's ..._OnUpdate_m...) and the editor's UnityPlayer pdb (the engine).

    uv run tools/downgrade/cpuprofile.py <tag> [--scene NAME] [--skip 2] [--beats 12] [vitalike options]
                                         -- <-crow-* flags...>
    uv run tools/downgrade/cpuprofile.py report <tag> [--top 60]

--scene adds -crow-scene NAME. Sampling starts at the --skip'th heartbeat in the scene the flags
load (the load and the first seconds left out) and the player quits --beats heartbeats later, or
when the game moves on to another scene (the samples since the last heartbeat, the load's, dropped).
Output in artifacts/cpuprofile/<tag>/: player.log, stacks.txt (collapsed stacks, root first, one
line per distinct stack with its count, as addresses), named-stacks.txt (the same, named: flame graph
input) and report.txt: the time by kind of work (CATEGORIES), per player loop step, per script
Update/LateUpdate/FixedUpdate, per PlayMaker action, and self and inclusive per function. The names
come from the player copy the run used; report reuses named-stacks.txt once it exists.
"""

import argparse
import collections
import ctypes
import ctypes.wintypes as wt
import re
import subprocess
import sys
import threading
import time
from pathlib import Path

import vitalike
from common import ROOT

OUT = ROOT / 'artifacts' / 'cpuprofile'
UNITY_PDB_DIR = (
    Path.home()
    / 'AppData/Local/Unity/Editors/2018.2.19f1/Editor/Data/PlaybackEngines/windowsstandalonesupport'
    / 'Variations/win64_nondevelopment_il2cpp'
)

kernel32 = vitalike.kernel32
ntdll = vitalike.ntdll
dbghelp = ctypes.WinDLL(str(Path(__import__('os').environ['SystemRoot']) / 'System32' / 'dbghelp.dll'))

TH32CS_SNAPTHREAD = 0x4
THREAD_ALL = 0x0008 | 0x0010 | 0x0040 | 0x0002  # GET_CONTEXT | SET_CONTEXT | QUERY_INFORMATION | SUSPEND_RESUME
CONTEXT_FULL = 0x10000B
MAX_FRAMES = 128


class ThreadEntry(ctypes.Structure):
    _fields_ = [
        ('dwSize', wt.DWORD),
        ('cntUsage', wt.DWORD),
        ('th32ThreadID', wt.DWORD),
        ('th32OwnerProcessID', wt.DWORD),
        ('tpBasePri', wt.LONG),
        ('tpDeltaPri', wt.LONG),
        ('dwFlags', wt.DWORD),
    ]


class Address64(ctypes.Structure):
    _fields_ = [('Offset', ctypes.c_uint64), ('Segment', wt.WORD), ('Mode', wt.DWORD)]


class StackFrame64(ctypes.Structure):
    _fields_ = [
        ('AddrPC', Address64),
        ('AddrReturn', Address64),
        ('AddrFrame', Address64),
        ('AddrStack', Address64),
        ('AddrBStore', Address64),
        ('FuncTableEntry', ctypes.c_void_p),
        ('Params', ctypes.c_uint64 * 4),
        ('Far', wt.BOOL),
        ('Virtual', wt.BOOL),
        ('Reserved', ctypes.c_uint64 * 3),
        ('KdHelp', ctypes.c_byte * 512),  # KDHELP64 and room to spare
    ]


class SymbolInfo(ctypes.Structure):
    _fields_ = [
        ('SizeOfStruct', wt.ULONG),
        ('TypeIndex', wt.ULONG),
        ('Reserved', ctypes.c_uint64 * 2),
        ('Index', wt.ULONG),
        ('Size', wt.ULONG),
        ('ModBase', ctypes.c_uint64),
        ('Flags', wt.ULONG),
        ('Value', ctypes.c_uint64),
        ('Address', ctypes.c_uint64),
        ('Register', wt.ULONG),
        ('Scope', wt.ULONG),
        ('Tag', wt.ULONG),
        ('NameLen', wt.ULONG),
        ('MaxNameLen', wt.ULONG),
        ('Name', ctypes.c_wchar * 1024),
    ]


kernel32.CreateToolhelp32Snapshot.restype = wt.HANDLE
kernel32.Thread32First.argtypes = [wt.HANDLE, ctypes.POINTER(ThreadEntry)]
kernel32.Thread32Next.argtypes = [wt.HANDLE, ctypes.POINTER(ThreadEntry)]
kernel32.OpenThread.restype = wt.HANDLE
kernel32.GetThreadContext.argtypes = [wt.HANDLE, ctypes.c_void_p]
dbghelp.SymInitializeW.argtypes = [wt.HANDLE, wt.LPCWSTR, wt.BOOL]
dbghelp.SymSetOptions.argtypes = [wt.DWORD]
dbghelp.SymRefreshModuleList.argtypes = [wt.HANDLE]
dbghelp.StackWalk64.argtypes = [
    wt.DWORD,
    wt.HANDLE,
    wt.HANDLE,
    ctypes.POINTER(StackFrame64),
    ctypes.c_void_p,
    ctypes.c_void_p,
    ctypes.c_void_p,
    ctypes.c_void_p,
    ctypes.c_void_p,
]
dbghelp.SymFromAddrW.argtypes = [wt.HANDLE, ctypes.c_uint64, ctypes.c_void_p, ctypes.POINTER(SymbolInfo)]
dbghelp.SymLoadModuleExW.argtypes = [
    wt.HANDLE,
    wt.HANDLE,
    wt.LPCWSTR,
    wt.LPCWSTR,
    ctypes.c_uint64,
    wt.DWORD,
    ctypes.c_void_p,
    wt.DWORD,
]
dbghelp.SymLoadModuleExW.restype = ctypes.c_uint64
dbghelp.SymCleanup.argtypes = [wt.HANDLE]
dbghelp.SymGetModuleBase64.argtypes = [wt.HANDLE, ctypes.c_uint64]
dbghelp.SymGetModuleBase64.restype = ctypes.c_uint64
FUNCTION_TABLE = ctypes.cast(dbghelp.SymFunctionTableAccess64, ctypes.c_void_p)
MODULE_BASE = ctypes.cast(dbghelp.SymGetModuleBase64, ctypes.c_void_p)


def first_thread(pid):
    """The process's only thread while it is still suspended at creation: the main thread."""
    snapshot = kernel32.CreateToolhelp32Snapshot(TH32CS_SNAPTHREAD, 0)
    entry = ThreadEntry()
    entry.dwSize = ctypes.sizeof(entry)
    ok = kernel32.Thread32First(snapshot, ctypes.byref(entry))
    try:
        while ok:
            if entry.th32OwnerProcessID == pid:
                return entry.th32ThreadID
            ok = kernel32.Thread32Next(snapshot, ctypes.byref(entry))
    finally:
        kernel32.CloseHandle(snapshot)
    raise SystemExit('no thread found for the player')


class Sampler:
    def __init__(self, process, thread_id):
        self.process = process
        self.thread = kernel32.OpenThread(THREAD_ALL, False, thread_id)
        self.buffer = ctypes.create_string_buffer(1232 + 16)
        self.context = (ctypes.addressof(self.buffer) + 15) & ~15
        ctypes.c_uint32.from_address(self.context + 0x30).value = CONTEXT_FULL
        self.stacks = collections.Counter()  # committed: whole heartbeat intervals in the scene
        self.segment = collections.Counter()  # since the last heartbeat
        self.on = False
        self.ready = False
        self.failed = 0

    def init_symbols(self, build):
        dbghelp.SymSetOptions(0x2 | 0x4 | 0x200)  # UNDNAME | DEFERRED_LOADS | FAIL_CRITICAL_ERRORS
        backup = build / 'CrowCountry_BackUpThisFolder_ButDontShipItWithYourGame'  # GameAssembly.pdb
        path = ';'.join(str(p) for p in (build, backup, UNITY_PDB_DIR))
        if not dbghelp.SymInitializeW(self.process, path, True):
            raise OSError(ctypes.get_last_error(), 'SymInitialize failed')
        self.ready = True

    def sample(self):
        if not self.on or not self.ready:
            return
        ctypes.c_uint32.from_address(self.context + 0x30).value = CONTEXT_FULL
        if not kernel32.GetThreadContext(self.thread, ctypes.c_void_p(self.context)):
            self.failed += 1
            return
        frame = StackFrame64()
        frame.AddrPC.Offset = ctypes.c_uint64.from_address(self.context + 0xF8).value
        frame.AddrStack.Offset = ctypes.c_uint64.from_address(self.context + 0x98).value
        frame.AddrFrame.Offset = ctypes.c_uint64.from_address(self.context + 0xA0).value
        frame.AddrPC.Mode = frame.AddrStack.Mode = frame.AddrFrame.Mode = 3  # AddrModeFlat
        pcs = []
        while len(pcs) < MAX_FRAMES:
            if not dbghelp.StackWalk64(
                0x8664,
                self.process,
                self.thread,
                ctypes.byref(frame),
                ctypes.c_void_p(self.context),
                None,
                FUNCTION_TABLE,
                MODULE_BASE,
                None,
            ):
                break
            if frame.AddrPC.Offset == 0:
                break
            pcs.append(frame.AddrPC.Offset)
        self.segment[tuple(pcs)] += 1

    def names(self):
        """Address -> module!offset (of the call instruction, for return addresses); report() names
        them offline, so a report can be named again without a run."""
        cache = {}
        modules = {}
        for stack in self.stacks:
            for depth, pc in enumerate(stack):
                if pc in cache:
                    continue
                address = pc - 1 if depth else pc  # return addresses point past the call
                base = dbghelp.SymGetModuleBase64(self.process, address)
                if base not in modules:
                    modules[base] = module_name(self.process, base)
                cache[pc] = f'{modules[base]}!{address - base:#x}' if base else f'?{address:#x}'
        return cache


def symbolize(keys, build):
    """module!offset -> function name, from the build's modules and PDBs (System32 for Windows'
    own, which name exports only)."""
    process = wt.HANDLE(0x5EED)  # any value: no live process, the modules are loaded by hand
    dbghelp.SymSetOptions(0x2 | 0x200)  # UNDNAME | FAIL_CRITICAL_ERRORS (deferred loads fail without a process)
    backup = build / 'CrowCountry_BackUpThisFolder_ButDontShipItWithYourGame'  # GameAssembly.pdb
    if not dbghelp.SymInitializeW(process, ';'.join(str(p) for p in (build, backup, UNITY_PDB_DIR)), False):
        raise OSError(ctypes.get_last_error(), 'SymInitialize failed')
    system = Path(__import__('os').environ['SystemRoot']) / 'System32'
    bases, names = {}, {}
    info = SymbolInfo()
    displacement = ctypes.c_uint64()
    try:
        for key in keys:
            module, _, offset = key.partition('!')
            if not offset:
                names[key] = key
                continue
            if module not in bases:
                path = build / module if (build / module).exists() else system / module
                base = 0x100000000 * (len(bases) + 1)
                loaded = dbghelp.SymLoadModuleExW(process, None, str(path), None, base, 0, None, 0)
                bases[module] = base if loaded else None
            name = None
            if bases[module] is not None:
                info.SizeOfStruct = 88  # sizeof(SYMBOL_INFOW)
                info.MaxNameLen = 1024
                if dbghelp.SymFromAddrW(process, bases[module] + int(offset, 16), ctypes.byref(displacement), info):
                    name = info.Name
            names[key] = (
                name
                if name and module in ('GameAssembly.dll', 'UnityPlayer.dll')
                else (f'{module}!{name}' if name else key)
            )
    finally:
        dbghelp.SymCleanup(process)
    return names


psapi = ctypes.WinDLL('psapi')
psapi.GetModuleBaseNameW.argtypes = [wt.HANDLE, ctypes.c_void_p, wt.LPWSTR, wt.DWORD]


def module_name(process, base):
    name = ctypes.create_unicode_buffer(260)
    psapi.GetModuleBaseNameW(process, ctypes.c_void_p(base), name, 260)
    return name.value or f'{base:#x}'


class SamplingDuty(vitalike.Duty):
    """vitalike's duty controller with a stack sample of the main thread in every off slice."""

    def __init__(self, *args, sampler, **kwargs):
        super().__init__(*args, **kwargs)
        self.sampler = sampler

    def cycle(self):
        kernel32.SetThreadAffinityMask(kernel32.GetCurrentThread(), self.cpu_mask)
        kernel32.SetThreadPriority(kernel32.GetCurrentThread(), 15)
        self.timer = kernel32.CreateWaitableTimerExW(None, None, 0x2, 0x1F0003)
        run_slice = self.period * self.fraction
        ntdll.NtResumeProcess(self.handle)
        while not self.stop.is_set():
            start = time.perf_counter()
            self.wait(run_slice)
            if ntdll.NtSuspendProcess(self.handle) != 0:
                break
            suspended = time.perf_counter()
            self.ran += suspended - start
            try:
                self.sampler.sample()
            except Exception as error:  # one bad sample shouldn't stop the run
                self.sampler.failed += 1
                self.sampler.last_error = repr(error)
            for guard in self.guards:
                ntdll.NtResumeProcess(guard)
            target = self.ran * (1 - self.fraction) / self.fraction - self.off - (time.perf_counter() - suspended)
            self.wait(min(max(target, 0.0), 4 * self.period))
            for guard in self.guards:
                ntdll.NtSuspendProcess(guard)
            ntdll.NtResumeProcess(self.handle)
            self.off += time.perf_counter() - suspended
            self.cycles += 1


HEARTBEAT = re.compile(r'^HEARTBEAT t=\S+ scene=(\S+)', re.M)


def run(args, flags):
    vitalike.prepare_player(args.build.resolve())
    out = (OUT / args.tag).resolve()
    out.mkdir(parents=True, exist_ok=True)
    for stale in out.glob('*'):
        if stale.is_file():
            stale.unlink()
    if args.scene:
        flags = ['-crow-scene', args.scene, *flags]
    if '-crow-heartbeat' not in flags:
        flags += ['-crow-heartbeat', '5']
    if '-crow-quit-after' not in flags:
        flags += ['-crow-quit-after', '1200']
    command = [
        str(vitalike.COPY / 'CrowCountry.exe'),
        '-screen-fullscreen', '0', '-screen-width', '960', '-screen-height', '544', '-logFile', '-',
        '-crow-no-vsync', '-crow-windowed', '-crow-mute', *flags,
    ]  # fmt: skip
    game_mask, controller_mask = vitalike.pick_cpus(args.cores)
    (out / 'cpuprofile.txt').write_text(
        f'cap {args.cap}% period {args.period} ms cores {args.cores} scene {args.scene} '
        f'skip {args.skip} beats {args.beats}\n'
        f'args {" ".join(command[1:])}\n'
    )
    job = vitalike.job_object(vitalike.JOB_LIMIT_AFFINITY | vitalike.JOB_KILL_ON_CLOSE, game_mask)
    guards = vitalike.start_guards(game_mask) if args.guard else None
    log_path = out / 'player.log'
    log = log_path.open('w', encoding='utf-8', errors='replace')
    process = subprocess.Popen(command, cwd=vitalike.COPY, stdout=log, stderr=subprocess.STDOUT, creationflags=0x4)
    handle = wt.HANDLE(int(process._handle))
    sampler = Sampler(handle, first_thread(process.pid))
    started = time.time()
    duty = None
    try:
        vitalike.check(kernel32.AssignProcessToJobObject(job, handle), 'AssignProcessToJobObject')
        vitalike.check(kernel32.SetPriorityClass(handle, 0x80), 'SetPriorityClass')
        duty = SamplingDuty(
            handle, args.cap / 100, args.period / 1000, controller_mask, guards[1] if guards else (), sampler=sampler
        )
        duty.start()
        threading.Thread(target=vitalike.offscreen, args=(process.pid,), daemon=True).start()
        target = None
        counted = seen = 0
        while process.poll() is None and time.time() - started < args.timeout:
            time.sleep(1)
            beats = HEARTBEAT.findall(log_path.read_text(encoding='utf-8', errors='replace'))
            if not beats:
                continue
            if not sampler.ready:
                sampler.init_symbols(vitalike.COPY)  # modules are loaded by the first heartbeat
            scene = beats[-1]
            if sampler.on and scene != target:  # the samples since the last heartbeat include the load
                print(f'{target} ended (now {scene}) after {sum(1 for b in beats if b == target) - counted} heartbeats')
                sampler.segment = collections.Counter()
                break
            if sampler.on and len(beats) != seen:
                segment, sampler.segment = sampler.segment, collections.Counter()
                sampler.stacks.update(segment)
            seen = len(beats)
            if args.scene and scene != args.scene.replace(' ', '_'):
                continue
            in_scene = sum(1 for b in beats if b == scene)
            if target != scene:
                target, counted = scene, 0
            if in_scene >= args.skip and not sampler.on:
                sampler.segment = collections.Counter()
                sampler.on = True
                counted = in_scene
                print(f'sampling {scene} from heartbeat {in_scene}')
            if sampler.on and in_scene - counted >= args.beats:
                break
        else:
            if process.poll() is None:
                print(f'timed out after {args.timeout} s')
    finally:
        sampler.on = False
        if duty is not None:
            duty.stop.set()
            duty.join(5)
        ntdll.NtSuspendProcess(handle)
        names = sampler.names() if sampler.ready else {}
        process.kill()
        process.wait()
        log.close()
        kernel32.CloseHandle(job)
        if guards is not None:
            kernel32.TerminateJobObject(guards[0], 0)
            kernel32.CloseHandle(guards[0])
    total = sum(sampler.stacks.values())
    with open(out / 'stacks.txt', 'w', encoding='utf-8') as file:
        for stack, count in sampler.stacks.most_common():
            file.write(';'.join(names.get(pc, '?') for pc in reversed(stack)) + f' {count}\n')
    note = f'run {time.time() - started:.0f} s; {duty.report() if duty else ""}; '
    note += f'{total} samples, {sampler.failed} failed'
    with open(out / 'cpuprofile.txt', 'a') as file:
        file.write(note + '\n')
    print(note)
    report(args.tag, args.top)


# Frames where the player loop hands over to a kind of work, for the breakdown. IL2CPP names a
# method <Type>_<Method>_m<hash>; Unity's own functions keep their C++ names.
SCRIPT_ENTRY = re.compile(
    r'^(\w+?)_(Update|LateUpdate|FixedUpdate|OnGUI|OnRenderImage|OnPreCull|OnPreRender|OnPostRender'
    r'|OnWillRenderObject)_m[0-9A-F]+$'
)
ACTION = re.compile(r'^(\w+?)_(OnUpdate|OnLateUpdate|OnFixedUpdate|OnEnter|DoUpdate|OnExit)_m[0-9A-F]+$')


# The breakdown by kind of work: the first rule (in this order) with a frame anywhere in a sample's
# stack takes the sample. Names are IL2CPP's <Type>_<Method> without the _m<hash>, or Unity's own.
CATEGORIES = [
    ('loading (PreloadManager, left out of the shares)', ['PreloadManager::UpdatePreloading']),
    ('IMGUI: FullScreenColour fade loop', ['FullScreenColour_OnGUI']),
    ('IMGUI other', ['RenderOverlays']),
    ('camera effect (OnRenderImage)', ['CrowCountryCamEffect_OnRenderImage']),
    ('rendering: shadows', ['ForwardShaderRenderLoop::RenderLightShadowMaps']),
    ('rendering: culling', ['Camera::Cull']),
    ('rendering: forward draws', ['ForwardShaderRenderLoop::StartRenderJobs', 'DoForwardShaderRenderLoop']),
    ('rendering: other', ['PlayerRender']),
    (
        'PlayMaker actions (OnUpdate/OnEnter/state changes)',
        [
            'FsmState_OnUpdate',
            'FsmState_OnEnter',
            'FsmState_OnLateUpdate',
            'FsmState_OnFixedUpdate',
            'Fsm_SwitchState',
            'Fsm_Event',
        ],
    ),
    (
        'PlayMaker bookkeeping (UpdateStateChanges, stack, delayed events)',
        [
            'Fsm_UpdateStateChanges',
            'Stack_1_Pop',
            'Fsm_UpdateDelayedEvents',
            'FsmExecutionStack_PushFsm',
            'FsmExecutionStack_PopFsm',
            'FastStack_1_Pop',
            'FastStack_1_Push',
        ],
    ),
    (
        'PlayMaker other',
        ['Fsm_Update', 'PlayMakerFSM_Update', 'PlayMakerFixedUpdate_FixedUpdate', 'PlayMakerLateUpdate_LateUpdate'],
    ),
    (
        'Rewired',
        [
            'InputManager_Base_DoUpdate',
            'InputManager_Base_Update',
            'InputManager_Base_FixedUpdate',
            'InputManager_Base_LateUpdate',
            'ThreadSafeUnityInput_Update',
        ],
    ),
    ('DOTween', ['DOTweenComponent_Update', 'DOTweenComponent_FixedUpdate']),
    ('probe (Diag heartbeat)', ['Diag_Update']),
    (
        'scripts: other, and Unity calling them',
        [
            'BaseBehaviourManager::CommonUpdate<BehaviourManager>',
            'BaseBehaviourManager::CommonUpdate<FixedBehaviourManager>',
            'BaseBehaviourManager::CommonUpdate<LateBehaviourManager>',
        ],
    ),
    ('audio', ['AudioModule::Update', 'AudioManager::FixedUpdate', 'SoundManager::Update']),
    ('animation (Director/Animator)', ['DirectorManager::ExecuteStage']),
    ('renderer and transform updates', ['RenderManager::UpdateAllRenderers']),
    ('particles', ['ParticleSystem::EndUpdateAll', 'PreLateUpdateParticleSystemBeginUpdateAllRegistrator']),
    ('UI canvases', ['PostLateUpdateUpdateCanvasRectTransformRegistrator', 'UIEventsWillRenderCanvasesRegistrator']),
    ('Steam overlay, window messages (PC only)', ['gameoverlayrenderer64.dll', 'TranslateAndDispatchFilteredMessage']),
]
HASH = re.compile(r'_m\d+(_gshared)?$|_t\d+$')


def category(frames):
    plain = {HASH.sub('', f) for f in frames}
    for name, keys in CATEGORIES:
        if any(
            k in plain or any(k in f for f in plain if '::' in k or '.dll' in k or 'Registrator' in k) for k in keys
        ):
            return name
    return 'other engine'


def report(tag, top=60):
    out = OUT / tag
    named = out / 'named-stacks.txt'
    if named.exists() and named.stat().st_mtime >= (out / 'stacks.txt').stat().st_mtime:
        source, raw = named, False  # named with the player the samples came from
    else:
        source, raw = out / 'stacks.txt', True
    stacks = []
    for line in source.read_text(encoding='utf-8').splitlines():
        frames, _, count = line.rpartition(' ')
        stacks.append((frames.split(';'), int(count)))
    if raw:
        names = symbolize(sorted({f for frames, _ in stacks for f in frames}), vitalike.COPY)
        stacks = [([names[f] for f in frames], count) for frames, count in stacks]
        with open(named, 'w', encoding='utf-8') as file:
            for frames, count in stacks:
                file.write(';'.join(frames) + f' {count}\n')
    total = sum(c for _, c in stacks) or 1
    self_time, inclusive = collections.Counter(), collections.Counter()
    scripts, actions, steps, kinds = (collections.Counter() for _ in range(4))
    for frames, count in stacks:
        self_time[frames[-1]] += count
        for name in set(frames):
            inclusive[name] += count
        outer = next((f for f in frames if SCRIPT_ENTRY.match(f)), None)  # outermost
        if outer:
            scripts[outer] += count
        action = next((f for f in frames if ACTION.match(f)), None)
        if action:
            actions[action] += count
        # the player loop's step: the frame below the innermost ExecutePlayerLoop
        loops = [i for i, name in enumerate(frames) if name == 'ExecutePlayerLoop']
        steps[frames[loops[-1] + 1] if loops and loops[-1] + 1 < len(frames) else '(outside the player loop)'] += count
        kinds[category(frames)] += count

    def table(title, counter, n, of=total):
        lines = [f'\n== {title}']
        for name, count in counter.most_common(n):
            lines.append(f'{count / of * 100:6.2f}%  {count:7d}  {name}')
        return lines

    loading = sum(c for k, c in kinds.items() if k.startswith('loading'))
    lines = [f'{tag}: {total} samples of the main thread']
    lines += table(
        f'Kinds of work, % of the samples outside loading ({loading} loading samples)',
        collections.Counter({k: c for k, c in kinds.items() if not k.startswith('loading')}),
        40,
        max(total - loading, 1),
    )
    lines += table('Player loop steps (inclusive)', steps, 40)
    lines += table('Scripts: outermost Update/LateUpdate/FixedUpdate/... (inclusive)', scripts, top)
    lines += table('PlayMaker actions and the like: outermost OnUpdate/OnEnter/... (inclusive)', actions, top)
    lines += table('Inclusive', inclusive, top * 2)
    lines += table('Self', self_time, top * 2)
    text = '\n'.join(lines) + '\n'
    (out / 'report.txt').write_text(text, encoding='utf-8')
    print(text[:6000])


def main():
    if len(sys.argv) > 1 and sys.argv[1] == 'report':
        parser = argparse.ArgumentParser()
        parser.add_argument('tag')
        parser.add_argument('--top', type=int, default=60)
        args = parser.parse_args(sys.argv[2:])
        report(args.tag, args.top)
        return
    split = sys.argv.index('--') if '--' in sys.argv else len(sys.argv)
    own, flags = sys.argv[1:split], sys.argv[split + 1 :]
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument('tag')
    parser.add_argument('--scene')
    parser.add_argument('--skip', type=int, default=2, help='heartbeats in the scene before sampling starts')
    parser.add_argument('--beats', type=int, default=12, help='heartbeats to sample')
    parser.add_argument('--cap', type=float, default=vitalike.DEFAULT_CAP)
    parser.add_argument('--period', type=float, default=vitalike.DEFAULT_PERIOD_MS)
    parser.add_argument('--cores', type=int, default=3)
    parser.add_argument('--timeout', type=float, default=1500)
    parser.add_argument('--build', type=Path, default=vitalike.BUILD)
    parser.add_argument('--no-guard', dest='guard', action='store_false')
    parser.add_argument('--top', type=int, default=60)
    args = parser.parse_args(own)
    run(args, flags)


if __name__ == '__main__':
    main()
