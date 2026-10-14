"""Vita-like PC runs: the Windows IL2CPP player slowed down to about the PS Vita's CPU speed, for CPU
measurements while the console is away. The mechanics are The Drifter port's (its docs/vitalike.md
has the calibration, measured on this PC's CPU, a Ryzen 7 5800X3D).

    uv run tools/downgrade/vitalike.py <tag> [--cap 5.5] [--period 4] [--cores 3] [--timeout 600]
                            [--build dir] [--no-guard] -- <-crow-* flags...>
    uv run tools/downgrade/vitalike.py summary <tag>...

The player (a copy of builds/crow-<version>-il2cpp, refreshed when the build changes) starts
suspended inside a Job Object that limits it to <cores> logical processors, one per physical core,
core 0 left out, at high priority. A controller thread then lets it run <cap> percent of the wall
time in slices of <period> ms (all its threads suspended in between), so each core acts about
100/<cap> times slower. Between slices a busy loop on each game core (a guard) keeps other programs
off them, so they don't empty its caches. The GPU and file reads are not slowed: the runs predict
script and game logic costs, not rendering. --cap 100 gives a PC baseline with the same flags.

Output goes to artifacts/vitalike/<tag>/: player.log (the player's output, with the probe's
HEARTBEAT lines) and vitalike.txt (the setup and the measured duty). The player runs unsynchronized
(-crow-no-vsync) with its window off-screen. `summary` averages the heartbeats in the last scene,
the first one after the load left out.
"""

import argparse
import ctypes
import ctypes.wintypes as wt
import re
import shutil
import subprocess
import sys
import threading
import time
from pathlib import Path

from common import ROOT, TARGET, WORK

BUILD = ROOT / 'builds' / f'crow-{TARGET}-il2cpp'
COPY = WORK / 'vitalike' / 'player'
OUT = ROOT / 'artifacts' / 'vitalike'
DEFAULT_CAP = 5.5
DEFAULT_PERIOD_MS = 4.0
WINDOW_SECONDS = 600  # how long the slowed player gets to open its window

kernel32 = ctypes.WinDLL('kernel32', use_last_error=True)
user32 = ctypes.WinDLL('user32', use_last_error=True)
ntdll = ctypes.WinDLL('ntdll')
kernel32.CreateJobObjectW.restype = wt.HANDLE
kernel32.GetCurrentThread.restype = wt.HANDLE
kernel32.CreateWaitableTimerExW.restype = wt.HANDLE
kernel32.SetThreadAffinityMask.argtypes = [wt.HANDLE, ctypes.c_size_t]
kernel32.SetThreadAffinityMask.restype = ctypes.c_size_t
ntdll.NtSuspendProcess.argtypes = [wt.HANDLE]
ntdll.NtResumeProcess.argtypes = [wt.HANDLE]
kernel32.SetThreadPriority.argtypes = [wt.HANDLE, ctypes.c_int]
kernel32.SetInformationJobObject.argtypes = [wt.HANDLE, ctypes.c_int, ctypes.c_void_p, wt.DWORD]
kernel32.AssignProcessToJobObject.argtypes = [wt.HANDLE, wt.HANDLE]
kernel32.TerminateJobObject.argtypes = [wt.HANDLE, wt.UINT]
kernel32.CloseHandle.argtypes = [wt.HANDLE]
kernel32.SetProcessAffinityMask.argtypes = [wt.HANDLE, ctypes.c_size_t]
kernel32.SetPriorityClass.argtypes = [wt.HANDLE, wt.DWORD]
kernel32.SetWaitableTimer.argtypes = [
    wt.HANDLE,
    ctypes.POINTER(ctypes.c_longlong),
    wt.LONG,
    ctypes.c_void_p,
    ctypes.c_void_p,
    wt.BOOL,
]
kernel32.WaitForSingleObject.argtypes = [wt.HANDLE, wt.DWORD]


class IoCounters(ctypes.Structure):
    _fields_ = [
        (name, ctypes.c_ulonglong)
        for name in (
            'ReadOperationCount',
            'WriteOperationCount',
            'OtherOperationCount',
            'ReadTransferCount',
            'WriteTransferCount',
            'OtherTransferCount',
        )
    ]


class BasicLimits(ctypes.Structure):
    _fields_ = [
        ('PerProcessUserTimeLimit', ctypes.c_longlong),
        ('PerJobUserTimeLimit', ctypes.c_longlong),
        ('LimitFlags', wt.DWORD),
        ('MinimumWorkingSetSize', ctypes.c_size_t),
        ('MaximumWorkingSetSize', ctypes.c_size_t),
        ('ActiveProcessLimit', wt.DWORD),
        ('Affinity', ctypes.c_size_t),
        ('PriorityClass', wt.DWORD),
        ('SchedulingClass', wt.DWORD),
    ]


class ExtendedLimits(ctypes.Structure):
    _fields_ = [
        ('BasicLimitInformation', BasicLimits),
        ('IoInfo', IoCounters),
        ('ProcessMemoryLimit', ctypes.c_size_t),
        ('JobMemoryLimit', ctypes.c_size_t),
        ('PeakProcessMemoryUsed', ctypes.c_size_t),
        ('PeakJobMemoryUsed', ctypes.c_size_t),
    ]


class ProcessorInfo(ctypes.Structure):  # SYSTEM_LOGICAL_PROCESSOR_INFORMATION
    _fields_ = [('ProcessorMask', ctypes.c_size_t), ('Relationship', wt.DWORD), ('Reserved', ctypes.c_ulonglong * 2)]


def check(ok, what):
    if not ok:
        raise OSError(ctypes.get_last_error(), f'{what} failed')
    return ok


def physical_cores():
    """Logical processor masks per physical core, in order."""
    size = wt.DWORD(0)
    kernel32.GetLogicalProcessorInformation(None, ctypes.byref(size))
    buffer = (ProcessorInfo * (size.value // ctypes.sizeof(ProcessorInfo)))()
    check(kernel32.GetLogicalProcessorInformation(buffer, ctypes.byref(size)), 'GetLogicalProcessorInformation')
    return [entry.ProcessorMask for entry in buffer if entry.Relationship == 0]  # RelationProcessorCore


def pick_cpus(count):
    """One logical processor on each of <count> physical cores after core 0 (where most interrupts
    land), and one for the duty controller on the last core."""
    cores = physical_cores()
    if len(cores) < count + 2:
        raise SystemExit(f'{len(cores)} physical cores: not enough for {count} game cores and the controller')
    game = 0
    for mask in cores[1 : count + 1]:
        game |= mask & -mask
    return game, cores[-1] & -cores[-1]


JOB_LIMIT_AFFINITY, JOB_KILL_ON_CLOSE = 0x10, 0x2000  # JOB_OBJECT_LIMIT_*
EXTENDED_LIMITS = 9  # JOBOBJECTINFOCLASS


def job_object(limit_flags, affinity=0):
    """A new Job Object with these basic limits."""
    job = check(kernel32.CreateJobObjectW(None, None), 'CreateJobObject')
    limits = ExtendedLimits()
    limits.BasicLimitInformation.LimitFlags = limit_flags
    limits.BasicLimitInformation.Affinity = affinity
    check(
        kernel32.SetInformationJobObject(job, EXTENDED_LIMITS, ctypes.byref(limits), ctypes.sizeof(limits)),
        'SetInformationJobObject(limits)',
    )
    return job


class Duty(threading.Thread):
    """Runs the process <fraction> of the wall time in slices of <period> seconds. The off time
    follows the measured run time, so timer and suspend overheads do not change the fraction."""

    def __init__(self, handle, fraction, period, cpu_mask, guards=()):
        super().__init__(daemon=True)
        self.handle, self.fraction, self.period, self.cpu_mask = handle, fraction, period, cpu_mask
        self.guards = list(guards)  # processes that run only while the game is suspended
        self.stop = threading.Event()
        self.ran = self.off = 0.0
        self.cycles = 0

    def wait(self, seconds):
        deadline = time.perf_counter() + seconds
        if seconds > 0.0015:
            due = ctypes.c_longlong(-int((seconds - 0.001) * 1e7))
            kernel32.SetWaitableTimer(self.timer, ctypes.byref(due), 0, None, None, False)
            kernel32.WaitForSingleObject(self.timer, 0xFFFFFFFF)
        while time.perf_counter() < deadline:
            pass

    def run(self):
        try:
            self.cycle()
        finally:
            ntdll.NtResumeProcess(self.handle)

    def cycle(self):
        kernel32.SetThreadAffinityMask(kernel32.GetCurrentThread(), self.cpu_mask)
        kernel32.SetThreadPriority(kernel32.GetCurrentThread(), 15)  # THREAD_PRIORITY_TIME_CRITICAL
        self.timer = kernel32.CreateWaitableTimerExW(None, None, 0x2, 0x1F0003)  # HIGH_RESOLUTION
        run_slice = self.period * self.fraction
        ntdll.NtResumeProcess(self.handle)  # created suspended
        while not self.stop.is_set():
            start = time.perf_counter()
            self.wait(run_slice)
            if ntdll.NtSuspendProcess(self.handle) != 0:
                break
            for guard in self.guards:
                ntdll.NtResumeProcess(guard)
            suspended = time.perf_counter()
            self.ran += suspended - start
            target = self.ran * (1 - self.fraction) / self.fraction - self.off
            self.wait(min(max(target, 0.0), 4 * self.period))
            for guard in self.guards:
                ntdll.NtSuspendProcess(guard)
            ntdll.NtResumeProcess(self.handle)
            self.off += time.perf_counter() - suspended
            self.cycles += 1

    def report(self):
        total = self.ran + self.off
        if not total:
            return 'duty: no cycles'
        return f'duty {self.ran / total * 100:.2f}% of {total:.0f} s, {self.cycles} cycles'


def start_guards(mask):
    """One busy loop per game processor, pinned there at high priority, that the controller runs only
    while the game is suspended. Returns (job, process handles); the job kills them with this script."""
    job = job_object(JOB_KILL_ON_CLOSE)
    interpreter = getattr(sys, '_base_executable', sys.executable)
    handles = []
    for bit in range(64):
        if mask >> bit & 1:
            guard = subprocess.Popen([interpreter, '-I', '-c', 'while True: pass'], creationflags=0x80 | 0x4)
            handle = wt.HANDLE(int(guard._handle))
            check(kernel32.AssignProcessToJobObject(job, handle), 'AssignProcessToJobObject(guard)')
            check(kernel32.SetProcessAffinityMask(handle, ctypes.c_size_t(1 << bit)), 'SetProcessAffinityMask')
            handles.append(handle)
    return job, handles


def prepare_player(build: Path):
    """A real copy of the build, so runs never hold its files open while pipeline.py rebuilds it.
    Refreshed when the build's GameAssembly.dll or globalgamemanagers changes."""
    sources = [build / 'GameAssembly.dll', build / 'CrowCountry_Data' / 'globalgamemanagers']
    if not all(s.exists() for s in sources):
        sys.exit(f'No IL2CPP player in {build}: pipeline.py build --platform windows --backend il2cpp')
    stamp = ' '.join(f'{s.stat().st_size}:{s.stat().st_mtime_ns}' for s in sources)
    marker = COPY / 'vitalike-source.txt'
    if marker.exists() and marker.read_text() == stamp:
        return
    if COPY.exists():
        shutil.rmtree(COPY)
    shutil.copytree(build, COPY)
    marker.write_text(stamp)


def offscreen(pid):
    """Moves the player's window past the right edge of the desktop, so the cursor never hovers it."""
    found = []
    callback_type = ctypes.WINFUNCTYPE(wt.BOOL, wt.HWND, wt.LPARAM)

    def callback(hwnd, _):
        owner = wt.DWORD()
        user32.GetWindowThreadProcessId(hwnd, ctypes.byref(owner))
        if owner.value == pid and user32.IsWindowVisible(hwnd):
            found.append(hwnd)
        return True

    deadline = time.time() + WINDOW_SECONDS
    while time.time() < deadline and not found:
        user32.EnumWindows(callback_type(callback), 0)
        time.sleep(0.5)
    right = user32.GetSystemMetrics(76) + user32.GetSystemMetrics(78)  # SM_XVIRTUALSCREEN + SM_CXVIRTUALSCREEN
    for hwnd in found:
        user32.SetWindowPos(hwnd, 0, right + 200, 0, 0, 0, 0x0001 | 0x0004 | 0x0010)  # NOSIZE|NOZORDER|NOACTIVATE


def launch(args, flags):
    prepare_player(args.build.resolve())
    out = (OUT / args.tag).resolve()
    out.mkdir(parents=True, exist_ok=True)
    for stale in out.glob('*'):
        if stale.is_file():
            stale.unlink()
    if '-crow-quit-after' not in flags:
        flags += ['-crow-quit-after', '120']
    if '-crow-heartbeat' not in flags:
        flags += ['-crow-heartbeat', '5']
    command = [
        str(COPY / 'CrowCountry.exe'),
        '-screen-fullscreen',
        '0',
        '-screen-width',
        '960',
        '-screen-height',
        '544',
        '-logFile',
        '-',
        '-crow-no-vsync',
        '-crow-windowed',
        '-crow-mute',
        *flags,
    ]
    mode = 'none' if args.cap >= 100 else 'duty'
    game_mask, controller_mask = pick_cpus(args.cores)
    (out / 'vitalike.txt').write_text(
        f'vitalike cap {args.cap}% period {args.period} ms cores {args.cores} (mask {game_mask:#x}) '
        f'guard {args.guard}\nargs {" ".join(command[1:])}\n'
    )
    job = job_object(JOB_LIMIT_AFFINITY | JOB_KILL_ON_CLOSE, game_mask)
    guards = start_guards(game_mask) if args.guard and mode == 'duty' else None
    log = (out / 'player.log').open('w', encoding='utf-8', errors='replace')
    process = subprocess.Popen(command, cwd=COPY, stdout=log, stderr=subprocess.STDOUT, creationflags=0x4)
    handle = wt.HANDLE(int(process._handle))
    duty = None
    started = time.time()
    try:
        check(kernel32.AssignProcessToJobObject(job, handle), 'AssignProcessToJobObject')
        check(kernel32.SetPriorityClass(handle, 0x80), 'SetPriorityClass')  # HIGH_PRIORITY_CLASS
        if mode == 'duty':
            duty = Duty(handle, args.cap / 100, args.period / 1000, controller_mask, guards[1] if guards else ())
            duty.start()
        else:
            ntdll.NtResumeProcess(handle)
        offscreen(process.pid)
        process.wait(timeout=args.timeout)
    except subprocess.TimeoutExpired:
        print(f'timed out after {args.timeout} s')
    finally:
        if duty is not None:
            duty.stop.set()
            duty.join(2)
        process.kill()
        process.wait()
        log.close()
        kernel32.CloseHandle(job)
        if guards is not None:
            kernel32.TerminateJobObject(guards[0], 0)
            kernel32.CloseHandle(guards[0])
    note = f'run {time.time() - started:.0f} s' + (f'; {duty.report()}' if duty is not None else '')
    with open(out / 'vitalike.txt', 'a') as file:
        file.write(note + '\n')
    print(note)
    return out


HEARTBEAT = re.compile(r'^HEARTBEAT (.*)$', re.M)


def summary(tag):
    """The mean of the heartbeats in the last scene, the first after its load left out."""
    out = OUT / tag
    beats = [
        dict(pair.split('=', 1) for pair in m[1].split() if '=' in pair)
        for m in HEARTBEAT.finditer((out / 'player.log').read_text(encoding='utf-8', errors='replace'))
    ]
    if not beats:
        return f'{tag}: no heartbeats'
    scene = beats[-1]['scene']
    last = [b for b in beats if b['scene'] == scene][1:]
    if not last:
        return f'{tag}: one heartbeat in {scene}'

    def mean(key):
        return sum(float(b[key].replace(',', '.')) for b in last) / len(last)

    return (
        f'{tag}: {scene}, {len(last)} heartbeats: fps {mean("fps"):.1f}, ms {mean("ms"):.1f}, '
        f'worst {max(float(b["worst"].replace(",", ".")) for b in last):.0f}, gc {mean("gc"):.2f} per beat, '
        f'visible {mean("visible"):.0f}'
    )


def main():
    if len(sys.argv) > 1 and sys.argv[1] == 'summary':
        for tag in sys.argv[2:]:
            print(summary(tag))
        return
    split = sys.argv.index('--') if '--' in sys.argv else len(sys.argv)
    own, flags = sys.argv[1:split], sys.argv[split + 1 :]
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument('tag')
    parser.add_argument('--cap', type=float, default=DEFAULT_CAP, help='percent of the wall time the player runs')
    parser.add_argument('--period', type=float, default=DEFAULT_PERIOD_MS, help='slice length in ms')
    parser.add_argument('--cores', type=int, default=3)
    parser.add_argument('--timeout', type=float, default=900)
    parser.add_argument('--build', type=Path, default=BUILD)
    parser.add_argument('--no-guard', dest='guard', action='store_false')
    args = parser.parse_args(own)
    launch(args, flags)
    print(summary(args.tag))


if __name__ == '__main__':
    main()
