"""Take, release or hold a lock in work/ atomically, so jobs that run at the same time (builds, console runs)
don't collide.

    uv run tools/downgrade/lock.py take <name> "<holder: purpose>" [--wait SECONDS]  # exit 0 when taken, 1 on timeout
    uv run tools/downgrade/lock.py release <name> "<holder>"                         # removes it only if you hold it
    uv run tools/downgrade/lock.py run <name> "<holder: purpose>" [--wait SECONDS] -- <command...>
    uv run tools/downgrade/lock.py status

<name> is vita-device, vita-build or pc-build. Taking creates work/<name>.lock with O_EXCL, so the
check and the write are one step and two jobs can never both get it. Release checks that the
file still names you before removing it.

Only one pipeline.py runs at a time, whatever the platform: a build lock (vita-build, pc-build)
counts as taken only while the other one is free; if the other appears right after, ours is
released again and the wait goes on.

`run` takes the lock, runs the command and releases the lock however the command ends (also on
Ctrl+C); it exits with the command's code, or 75 if the lock was not taken in time. The command and
everything it starts run in a Windows job that ends with lock.py: if lock.py is killed (a shell or an
agent's tool stopping it), the Unity editor it started doesn't go on building unsupervised, and the
lock, which names lock.py's process, counts as free once that process is gone (take removes it).
The command sees CROW_LOCK=<name>, which pipeline.py checks. For example:

    uv run tools/downgrade/lock.py run pc-build "user: mono build" --wait 3600 -- \\
        bash -c "uv run tools/downgrade/pipeline.py project && uv run tools/downgrade/pipeline.py build"

A person at the console holds vita-device under the name "user" ("user: <purpose>", any case);
jobs use their own names. keep_awake.py never wakes the console while a person holds it.
"""

import argparse
import ctypes
import os
import re
import subprocess
import sys
import time
from pathlib import Path

WORK = Path(__file__).resolve().parents[2] / 'work'
LOCKS = ('vita-device', 'vita-build', 'pc-build')
BUILD_LOCKS = ('vita-build', 'pc-build')
NOT_TAKEN = 75  # exit code of `run` when the lock stayed held (EX_TEMPFAIL)
POLL_SECONDS = 5
PERSON = 'user'  # the holder name of a person, as opposed to a job


def path(name):
    if name not in LOCKS:
        sys.exit(f'unknown lock {name!r}; one of {", ".join(LOCKS)}')
    return WORK / f'{name}.lock'


def holder_of(name):
    """The lock's first line ("<holder: purpose> HH:MM"), or None when it is free."""
    try:
        return path(name).read_text(errors='replace').strip().splitlines()[0]
    except (FileNotFoundError, IndexError):
        return None


def process_of(name):
    """(pid, start time) of the lock.py process holding the lock with run (its second line), or None."""
    try:
        found = re.search(r'^pid (\d+) (\d+)$', path(name).read_text(errors='replace'), re.M)
    except FileNotFoundError:
        return None
    return (int(found[1]), int(found[2])) if found else None


def process_start(pid):
    """A running process's start time (a Windows FILETIME), or None when no process has that pid."""
    kernel32 = ctypes.windll.kernel32
    kernel32.OpenProcess.restype = ctypes.c_void_p
    kernel32.GetExitCodeProcess.argtypes = [ctypes.c_void_p, ctypes.c_void_p]
    kernel32.GetProcessTimes.argtypes = [ctypes.c_void_p, *[ctypes.c_void_p] * 4]
    kernel32.CloseHandle.argtypes = [ctypes.c_void_p]
    handle = kernel32.OpenProcess(0x1000, False, pid)  # PROCESS_QUERY_LIMITED_INFORMATION
    if not handle:
        return None
    try:
        code, times = ctypes.c_ulong(), [ctypes.c_ulonglong() for _ in range(4)]
        if not kernel32.GetExitCodeProcess(handle, ctypes.byref(code)) or code.value != 259:  # STILL_ACTIVE
            return None
        if not kernel32.GetProcessTimes(handle, *(ctypes.byref(t) for t in times)):
            return None
        return times[0].value
    finally:
        kernel32.CloseHandle(handle)


def stale(name):
    """Whether the lock names a run whose lock.py process has ended (killed before it could release
    the lock). Its command ended with it (run's job)."""
    holder = process_of(name)
    return holder is not None and process_start(holder[0]) != holder[1]


def clear_stale(name):
    if stale(name):
        print(f'{name}: removing the lock of {holder_of(name)}, whose lock.py process has ended')
        path(name).unlink(missing_ok=True)


def take(name, holder, wait, process=False):
    """process: name this process in the lock (run), so a lock it can no longer release counts as free."""
    lock = path(name)
    others = [other for other in BUILD_LOCKS if other != name] if name in BUILD_LOCKS else []
    end = time.time() + wait
    own = f'pid {os.getpid()} {process_start(os.getpid())}\n' if process else ''
    while True:
        for other in [name, *others]:
            clear_stale(other)
        busy = next((other for other in others if path(other).exists()), None)
        if busy is None:
            try:
                fd = os.open(lock, os.O_CREAT | os.O_EXCL | os.O_WRONLY)
            except FileExistsError:
                busy = name
            else:
                os.write(fd, f'{holder} {time.strftime("%H:%M")}\n{own}'.encode())
                os.close(fd)
                busy = next((other for other in others if path(other).exists()), None)
                if busy is None:
                    return 0
                lock.unlink()  # the other build lock was taken in between: one pipeline at a time
        if time.time() >= end:
            print(f'{busy} held by: {holder_of(busy)}')
            return 1
        time.sleep(POLL_SECONDS)


def owner(text):
    """The name before the purpose in a holder ("me: purpose") or a lock's text ("me: purpose 16:09")."""
    return re.sub(r' \d{1,2}:\d\d$', '', text.strip()).split(':')[0].strip()


def is_person(text):
    """Whether a holder or a lock's text names a person (PERSON), not a job."""
    return owner(text).lower() == PERSON


def release(name, holder):
    text = holder_of(name)
    if text is None:
        return 0
    # The whole name must match: "a" must not release "ab: ..." (or "" anyone's lock).
    if not owner(holder) or owner(text) != owner(holder):
        print(f'{name} is held by someone else: {text}')
        return 1
    path(name).unlink()
    return 0


def run(name, holder, wait, command):
    if not command:
        sys.exit('run: no command after --')
    join_job_ending_with_this_process()
    if take(name, holder, wait, process=True) != 0:
        return NOT_TAKEN
    try:
        return subprocess.run(command, env={**os.environ, 'CROW_LOCK': name}).returncode
    except KeyboardInterrupt:
        return 130
    finally:
        release(name, holder)


class JobLimits(ctypes.Structure):  # JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    _fields_ = [
        ('PerProcessUserTimeLimit', ctypes.c_int64),
        ('PerJobUserTimeLimit', ctypes.c_int64),
        ('LimitFlags', ctypes.c_uint32),
        ('MinimumWorkingSetSize', ctypes.c_size_t),
        ('MaximumWorkingSetSize', ctypes.c_size_t),
        ('ActiveProcessLimit', ctypes.c_uint32),
        ('Affinity', ctypes.c_size_t),
        ('PriorityClass', ctypes.c_uint32),
        ('SchedulingClass', ctypes.c_uint32),
        ('IoCounters', ctypes.c_uint64 * 6),
        ('ProcessMemoryLimit', ctypes.c_size_t),
        ('JobMemoryLimit', ctypes.c_size_t),
        ('PeakProcessMemoryUsed', ctypes.c_size_t),
        ('PeakJobMemoryUsed', ctypes.c_size_t),
    ]


KILL_ON_JOB_CLOSE = 0x2000
EXTENDED_LIMIT_INFORMATION = 9


def join_job_ending_with_this_process():
    """Put this process in a Windows job that kills its processes when its last handle closes. The
    only handle is this process's, never closed, so whatever the command starts (the pipeline, the
    Unity editor it runs) ends when lock.py ends, however that happens. Before, a killed lock.py left
    its lock held and its Unity editor building."""
    kernel32 = ctypes.windll.kernel32
    kernel32.CreateJobObjectW.restype = ctypes.c_void_p
    kernel32.GetCurrentProcess.restype = ctypes.c_void_p
    kernel32.SetInformationJobObject.argtypes = [ctypes.c_void_p, ctypes.c_int, ctypes.c_void_p, ctypes.c_uint32]
    kernel32.AssignProcessToJobObject.argtypes = [ctypes.c_void_p, ctypes.c_void_p]
    job = kernel32.CreateJobObjectW(None, None)
    limits = JobLimits(LimitFlags=KILL_ON_JOB_CLOSE)
    size = ctypes.sizeof(limits)
    if not (
        job
        and kernel32.SetInformationJobObject(job, EXTENDED_LIMIT_INFORMATION, ctypes.byref(limits), size)
        and kernel32.AssignProcessToJobObject(job, kernel32.GetCurrentProcess())
    ):
        print(f'lock.py: could not put the command in a job (error {kernel32.GetLastError()}); it can outlive lock.py')


def main(argv):
    command = argv[argv.index('--') + 1 :] if '--' in argv else []
    argv = argv[: argv.index('--')] if '--' in argv else argv
    parser = argparse.ArgumentParser(
        prog='lock.py', description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter
    )
    parser.add_argument('action', choices=['take', 'release', 'run', 'status'])
    parser.add_argument('name', nargs='?', choices=LOCKS)
    parser.add_argument('holder', nargs='?', help='"<name>: <purpose>"; release needs only the name')
    parser.add_argument('--wait', type=float, default=0, metavar='SECONDS', help='take, run: how long to wait')
    args = parser.parse_args(argv)
    if args.action == 'status':
        for name in LOCKS:
            note = ' (stale: its lock.py has ended; the next take removes it)' if stale(name) else ''
            print(f'{name:12} {holder_of(name) or "free"}{note}')
        return 0
    if args.holder is None:
        parser.error(f'{args.action} needs a lock name and a holder')
    if args.action == 'take':
        return take(args.name, args.holder, args.wait)
    if args.action == 'release':
        return release(args.name, args.holder)
    return run(args.name, args.holder, args.wait, command)


if __name__ == '__main__':
    sys.exit(main(sys.argv[1:]))
