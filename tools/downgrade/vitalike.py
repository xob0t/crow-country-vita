"""Vita-like PC runs: the Windows IL2CPP player slowed down to about the PS Vita's CPU speed, for CPU
measurements while the console is away. The mechanics are The Drifter port's (its docs/vitalike.md
has the calibration, measured on this PC's CPU, a Ryzen 7 5800X3D).

    uv run tools/downgrade/vitalike.py <tag> [--cap 5.5] [--period 4] [--cores 3] [--timeout 900]
                            [--build dir] [--no-guard] -- <-crow-* flags...>
    uv run tools/downgrade/vitalike.py summary <tag>...

The player (a copy of builds/crow-<version>-il2cpp without Steam's files, refreshed when the build
changes) runs through run_player.py, on a desktop of its own, windowed and muted. Once it runs it is
limited to <cores> logical processors, one per physical core, core 0 left out, at high priority, and
a controller thread lets it run <cap> percent of the wall time in slices of <period> ms (all its
threads suspended in between), so each core acts about 100/<cap> times slower. Between slices a busy
loop on each game core (a guard) keeps other programs off them, so they don't empty its caches. The
GPU and file reads are not slowed: the runs predict script and game logic costs, not rendering.
--cap 100 gives a PC baseline with the same flags.

Output goes to artifacts/vitalike/<tag>/: player.log (the player's output, with the probe's
HEARTBEAT lines) and vitalike.txt (the setup and the measured duty). The player runs unsynchronized
(-crow-no-vsync). `summary` averages the heartbeats in the last scene, the first one after the load
left out. fightbench.py --vitalike and cpuprofile.py slow their players the same way (slow_down).
"""

import argparse
import ctypes
import ctypes.wintypes as wt
import subprocess
import sys
import threading
import time
from pathlib import Path

from common import ROOT, WORK, build_folder, split_at_dashes
from playerlog import heartbeats
from winapi import CREATE_SUSPENDED, HIGH_PRIORITY_CLASS, PROCESS_ALL_ACCESS, check, kernel32, kill_on_close_job

BUILD = build_folder('windows', 'il2cpp')
COPY = WORK / 'vitalike' / 'player'
OUT = ROOT / 'artifacts' / 'vitalike'
DEFAULT_CAP = 5.5
DEFAULT_PERIOD_MS = 4.0
DEFAULT_CORES = 3

ntdll = ctypes.WinDLL('ntdll')
kernel32.GetCurrentThread.restype = wt.HANDLE
kernel32.CreateWaitableTimerExW.restype = wt.HANDLE
kernel32.SetThreadAffinityMask.argtypes = [wt.HANDLE, ctypes.c_size_t]
kernel32.SetThreadAffinityMask.restype = ctypes.c_size_t
ntdll.NtSuspendProcess.argtypes = [wt.HANDLE]
ntdll.NtResumeProcess.argtypes = [wt.HANDLE]
kernel32.SetThreadPriority.argtypes = [wt.HANDLE, ctypes.c_int]
kernel32.SetWaitableTimer.argtypes = [
    wt.HANDLE,
    ctypes.POINTER(ctypes.c_longlong),
    wt.LONG,
    ctypes.c_void_p,
    ctypes.c_void_p,
    wt.BOOL,
]
kernel32.WaitForSingleObject.argtypes = [wt.HANDLE, wt.DWORD]
THREAD_PRIORITY_TIME_CRITICAL = 15
CREATE_WAITABLE_TIMER_HIGH_RESOLUTION = 0x2
TIMER_ALL_ACCESS = 0x1F0003


class ProcessorInfo(ctypes.Structure):  # SYSTEM_LOGICAL_PROCESSOR_INFORMATION
    _fields_ = [('ProcessorMask', ctypes.c_size_t), ('Relationship', wt.DWORD), ('Reserved', ctypes.c_ulonglong * 2)]


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


class Duty(threading.Thread):
    """Runs the process <fraction> of the wall time in slices of <period> seconds. The off time
    follows the measured run time, so timer and suspend overheads do not change the fraction.
    Subclasses do their own work in each pause (paused: cpuprofile.py's stack sample); that time
    counts as off time."""

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

    def paused(self):
        """Called in each pause, the process suspended, before the guards run."""

    def cycle(self):
        kernel32.SetThreadAffinityMask(kernel32.GetCurrentThread(), self.cpu_mask)
        kernel32.SetThreadPriority(kernel32.GetCurrentThread(), THREAD_PRIORITY_TIME_CRITICAL)
        self.timer = kernel32.CreateWaitableTimerExW(
            None, None, CREATE_WAITABLE_TIMER_HIGH_RESOLUTION, TIMER_ALL_ACCESS
        )
        run_slice = self.period * self.fraction
        ntdll.NtResumeProcess(self.handle)  # created suspended
        while not self.stop.is_set():
            start = time.perf_counter()
            self.wait(run_slice)
            if ntdll.NtSuspendProcess(self.handle) != 0:
                break
            suspended = time.perf_counter()
            self.ran += suspended - start
            self.paused()
            for guard in self.guards:
                ntdll.NtResumeProcess(guard)
            target = self.ran * (1 - self.fraction) / self.fraction - self.off - (time.perf_counter() - suspended)
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
    job = kill_on_close_job()
    interpreter = getattr(sys, '_base_executable', sys.executable)
    handles = []
    for bit in range(64):
        if mask >> bit & 1:
            guard = subprocess.Popen(
                [interpreter, '-I', '-c', 'while True: pass'], creationflags=HIGH_PRIORITY_CLASS | CREATE_SUSPENDED
            )
            handle = wt.HANDLE(int(guard._handle))
            check(kernel32.AssignProcessToJobObject(job, handle), 'AssignProcessToJobObject(guard)')
            check(kernel32.SetProcessAffinityMask(handle, ctypes.c_size_t(1 << bit)), 'SetProcessAffinityMask')
            handles.append(handle)
    return job, handles


def slow_down(pid, cap, period_ms=DEFAULT_PERIOD_MS, cores=DEFAULT_CORES, guard=True, duty_class=Duty, **duty_kwargs):
    """Slows a running player (run_player's on_start): <cores> processors after core 0, high
    priority, and at a cap under 100 the duty cycle (cap percent of the wall time, busy guards between
    slices). duty_class: a Duty subclass with work of its own in the pauses (given duty_kwargs), which
    runs at any cap. Returns (the Duty, a function that stops it and the guards), or None when the
    player can't be opened."""
    handle = kernel32.OpenProcess(PROCESS_ALL_ACCESS, False, pid)
    if not handle:
        print(f'vitalike: cannot open the player ({pid})')
        return None
    handle = wt.HANDLE(handle)
    game_mask, controller_mask = pick_cpus(cores)
    kernel32.SetProcessAffinityMask(handle, ctypes.c_size_t(game_mask))
    kernel32.SetPriorityClass(handle, HIGH_PRIORITY_CLASS)
    cycling = cap < 100 or duty_class is not Duty
    guards = start_guards(game_mask) if guard and cycling else None
    duty = duty_class(handle, cap / 100, period_ms / 1000, controller_mask, guards[1] if guards else (), **duty_kwargs)
    if cycling:
        duty.start()

    def stop():
        duty.stop.set()
        if duty.is_alive():
            duty.join(5)
        if guards is not None:
            kernel32.TerminateJobObject(guards[0], 0)
            kernel32.CloseHandle(guards[0])

    return duty, stop


def launch(args, flags):
    import run_player

    exe = run_player.player_copy(
        args.build.resolve(), COPY, link=False, sources=['GameAssembly.dll', 'CrowCountry_Data/globalgamemanagers']
    )
    out = (OUT / args.tag).resolve()
    out.mkdir(parents=True, exist_ok=True)
    for stale in out.glob('*'):
        if stale.is_file():
            stale.unlink()
    if '-crow-quit-after' not in flags:
        flags += ['-crow-quit-after', '120']
    if '-crow-heartbeat' not in flags:
        flags += ['-crow-heartbeat', '5']
    flags = ['-crow-no-vsync', *flags]
    game_mask, _ = pick_cpus(args.cores)
    (out / 'vitalike.txt').write_text(
        f'vitalike cap {args.cap}% period {args.period} ms cores {args.cores} (mask {game_mask:#x}) '
        f'guard {args.guard}\nargs {" ".join(flags)} (through run_player.py)\n'
    )
    duties = []

    def on_start(pid):
        slowed = slow_down(pid, args.cap, args.period, args.cores, args.guard)
        if slowed is None:
            return None
        duties.append(slowed[0])
        return slowed[1]

    started = time.time()
    code = run_player.run(exe, out / 'player.log', flags, timeout=args.timeout, on_start=on_start)
    note = f'run {time.time() - started:.0f} s, exit {code}'
    if duties and args.cap < 100:
        note += f'; {duties[0].report()}'
    with open(out / 'vitalike.txt', 'a') as file:
        file.write(note + '\n')
    print(note)
    return out


def summary(tag):
    """The mean of the heartbeats in the last scene, the first after its load left out."""
    out = OUT / tag
    beats = list(heartbeats((out / 'player.log').read_text(encoding='utf-8', errors='replace')))
    if not beats:
        return f'{tag}: no heartbeats'
    scene = beats[-1].get('scene')
    last = [b for b in beats if b.get('scene') == scene][1:]
    if not last:
        return f'{tag}: one heartbeat in {scene}'

    def mean(key, form):
        values = [b[key] for b in last if isinstance(b.get(key), float)]
        return format(sum(values) / len(values), form) if values else '-'

    worst = max((b['worst'] for b in last if isinstance(b.get('worst'), float)), default=None)
    return (
        f'{tag}: {scene}, {len(last)} heartbeats: fps {mean("fps", ".1f")}, ms {mean("ms", ".1f")}, '
        f'worst {"-" if worst is None else f"{worst:.0f}"}, gc {mean("gc", ".2f")} per beat, '
        f'visible {mean("visible", ".0f")}'
    )


def main():
    if len(sys.argv) > 1 and sys.argv[1] == 'summary':
        for tag in sys.argv[2:]:
            print(summary(tag))
        return
    own, flags = split_at_dashes(sys.argv[1:])
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument('tag')
    parser.add_argument('--cap', type=float, default=DEFAULT_CAP, help='percent of the wall time the player runs')
    parser.add_argument('--period', type=float, default=DEFAULT_PERIOD_MS, help='slice length in ms')
    parser.add_argument('--cores', type=int, default=DEFAULT_CORES)
    parser.add_argument('--timeout', type=float, default=900)
    parser.add_argument('--build', type=Path, default=BUILD)
    parser.add_argument('--no-guard', dest='guard', action='store_false')
    args = parser.parse_args(own)
    launch(args, flags)
    print(summary(args.tag))


if __name__ == '__main__':
    main()
