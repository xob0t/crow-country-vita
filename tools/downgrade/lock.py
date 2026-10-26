"""Take, release or hold a lock in work/ atomically, so jobs that run at the same time (builds, console runs)
don't collide.

    uv run tools/downgrade/lock.py take <name> "<holder: purpose>" [--wait SECONDS]
    uv run tools/downgrade/lock.py release <name> "<holder>"                         # removes it only if you hold it
    uv run tools/downgrade/lock.py run <name> "<holder: purpose>" [--wait SECONDS] -- <command...>
    uv run tools/downgrade/lock.py wait <name> [--wait SECONDS]   # exit 0 once it is free (not taken)
    uv run tools/downgrade/lock.py status

<name> is vita-device, vita-build or pc-build. --wait is how long take, run and wait wait for the lock;
the default, 0, fails at once and says who holds it and since when. All three exit 75 when the lock
stayed held. Taking creates work/<name>.lock with O_EXCL, so the check and the write are one step and
two jobs can never both get it. Release checks that the file still names you before removing it.

Only one pipeline.py runs at a time, whatever the platform: a build lock (vita-build, pc-build)
counts as taken only while the other one is free; if the other appears right after, ours is
released again and the wait goes on. vita.py's sync, install and seed (and run, before it quits the
game) hold vita-build too while they read the PS Vita build, so no build rewrites the files they
upload. They hold it as a reader (a "reader" line in the lock file): a reader and pc-build don't wait
for each other, since a PC build never writes the PS Vita build; a vita-build taker (a build or
quickcode) waits for the reader as for any holder, and wait vita-build too. So a job that writes the
PS Vita build (pipeline.py build --platform vita, package, quickcode) runs under vita-build, never
pc-build: pipeline.py stops otherwise. "Reader" is only about pc-build: two readers still exclude
each other, as two holders of one lock do (a seed during a 30-minute install waits, then stops).

`run` takes the lock, runs the command and releases the lock however the command ends; it exits
with the command's code, 130 after Ctrl+C, or 75 if the lock was not taken in time. Ctrl+C reaches
the command too, and run waits for it to end its own way (vita.py finishes the file it is sending:
an FTP transfer cut off mid-way hangs the console's FTP server); Ctrl+Break ends both at once. The
command and everything it starts run in a Windows job that ends with lock.py: if lock.py is killed
(a shell or an agent's tool stopping it), the Unity editor it started doesn't go on building
unsupervised, and the lock, which names lock.py's process, counts as free once that process is gone
(take removes it). The command sees CROW_LOCKS, the names of all the runs it is in, which
pipeline.py, vita.py and fightbench.py check (held_here, ours: a sync inside `lock.py run vita-build`
is the build's own), and CROW_LOCK=<name>, the innermost run's, which build.py checks. For example, a
Windows Mono player (pipeline.py's default platform is the PS Vita, whose stages run the same way
under vita-build):

    uv run tools/downgrade/lock.py run pc-build "user: mono build" --wait 3600 -- \\
        bash -c "uv run tools/downgrade/pipeline.py project --platform windows && \\
                 uv run tools/downgrade/pipeline.py build --platform windows"

The time spent waiting counts against whatever limits the caller's own run time; docs/vita.md ("Code
changes without a full build") says how to queue in short steps with wait.

A person at the console holds vita-device under the name "user" ("user: <purpose>", any case);
jobs use their own names and wait for a person as for any holder.
"""

import argparse
import ctypes
import os
import re
import subprocess
import sys
import time
from pathlib import Path

import winapi

WORK = Path(__file__).resolve().parents[2] / 'work'
LOCKS = ('vita-device', 'vita-build', 'pc-build')
BUILD_LOCKS = ('vita-build', 'pc-build')
NOT_TAKEN = 75  # exit code of take, run and wait when the lock stayed held (EX_TEMPFAIL)
INTERRUPTED = 130  # exit code of `run` after Ctrl+C
POLL_SECONDS = 5


def path(name):
    if name not in LOCKS:
        sys.exit(f'unknown lock {name!r}; one of {", ".join(LOCKS)}')
    return WORK / f'{name}.lock'


def text_of(name):
    """The lock file's text, or None when it is free. UTF-8, as take writes it (the locale's code page
    before 21 Oct: a holder with a non-ASCII name never matched in release). PermissionError: a lock
    file being deleted (clear_stale) can't be opened for that moment."""
    try:
        return path(name).read_text(encoding='utf-8', errors='replace')
    except (FileNotFoundError, PermissionError):
        return None


def first_line(text):
    lines = (text or '').strip().splitlines()
    return lines[0] if lines else None


def holder_of(name):
    """The lock's first line ("<holder: purpose> HH:MM"), or None when it is free."""
    return first_line(text_of(name))


def pid_line(text):
    found = re.search(r'^pid (\d+) (\d+)$', text or '', re.M)
    return (int(found[1]), int(found[2])) if found else None


def process_of(name):
    """(pid, start time) of the lock.py process holding the lock with run (its second line), or None."""
    return pid_line(text_of(name))


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


def stale_text(text):
    """Whether a lock's text names a run whose lock.py process has ended (killed before it could
    release the lock). Its command ended with it (run's job)."""
    holder = pid_line(text)
    return holder is not None and process_start(holder[0]) != holder[1]


def stale(name):
    return stale_text(text_of(name))


GENERIC_READ, DELETE = 0x80000000, 0x00010000
SHARE_ALL = 0x7  # FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE
SHARE_READ_DELETE = 0x5
OPEN_EXISTING = 3
FILE_DISPOSITION_INFO = 4  # FileDispositionInfo
INVALID_HANDLE = ctypes.c_void_p(-1).value


def clear_stale(name):
    """Remove the lock if it is stale. Only the file that was judged stale goes: it is opened, read and
    judged through one handle, and deleted through a second one ReOpenFile makes from it (the same
    file, whatever the name has become meanwhile). Before (21 Oct) it was read, then unlinked by name:
    two waiters could both find the same stale lock, the first remove it and take the lock, and the
    second then remove that new lock and take it too. The first handle shares everything, so a reader
    (holder_of) never fails on it; the second exists only for a stale lock."""
    kernel32 = ctypes.windll.kernel32
    kernel32.CreateFileW.restype = ctypes.c_void_p
    uint, pointer = ctypes.c_uint32, ctypes.c_void_p
    kernel32.CreateFileW.argtypes = [ctypes.c_wchar_p, uint, uint, pointer, uint, uint, pointer]
    kernel32.ReOpenFile.restype = pointer
    kernel32.ReOpenFile.argtypes = [pointer, uint, uint, uint]
    kernel32.ReadFile.argtypes = [pointer, pointer, uint, pointer, pointer]
    kernel32.SetFileInformationByHandle.argtypes = [pointer, ctypes.c_int, pointer, uint]
    kernel32.CloseHandle.argtypes = [pointer]
    handle = kernel32.CreateFileW(str(path(name)), GENERIC_READ, SHARE_ALL, None, OPEN_EXISTING, 0, None)
    if handle in (None, INVALID_HANDLE):
        return  # free, or being deleted
    try:
        buffer, size = ctypes.create_string_buffer(4096), ctypes.c_ulong()
        if not kernel32.ReadFile(handle, buffer, len(buffer), ctypes.byref(size), None):
            return
        text = buffer.raw[: size.value].decode('utf-8', errors='replace').replace('\r\n', '\n')  # as read_text
        if not stale_text(text):
            return
        # Refused while another job reads the file (it opens it without delete sharing): the next poll
        # tries again.
        deleting = kernel32.ReOpenFile(handle, GENERIC_READ | DELETE, SHARE_READ_DELETE, 0)
        if deleting in (None, INVALID_HANDLE):
            return
        try:
            delete = ctypes.c_ubyte(1)  # FILE_DISPOSITION_INFO.DeleteFile: the file goes when the handles close
            if kernel32.SetFileInformationByHandle(deleting, FILE_DISPOSITION_INFO, ctypes.byref(delete), 1):
                print(f'{name}: removed the lock of {first_line(text)}, whose lock.py process has ended', flush=True)
        finally:
            kernel32.CloseHandle(deleting)
    finally:
        kernel32.CloseHandle(handle)


def remove(lock):
    """Delete our own lock file. Windows refuses while another job reads it (holder_of, for a moment):
    try again for a second or two."""
    for attempt in range(40):
        try:
            lock.unlink(missing_ok=True)
            return
        except PermissionError:
            if attempt == 39:
                raise
            time.sleep(0.05)


def other_build_locks(name):
    """The locks that must be free for name to count as taken: the other build lock of a build lock."""
    return [other for other in BUILD_LOCKS if other != name] if name in BUILD_LOCKS else []


READER = 'reader'  # the lock file's line of a reader's lock (take's reader)


def building(name):
    """Whether name is held by anything but a reader: the other build lock as take and wait see it. A
    file just created and not written yet, or being deleted, counts as held (the next poll looks again)."""
    return path(name).exists() and not re.search(rf'^{READER}$', text_of(name) or '', re.M)


def holding(name):
    """Who holds the lock, since when and for how long ("<holder: purpose> HH:MM (N min)"), or "free"."""
    text = holder_of(name)
    try:
        minutes = f' ({(time.time() - path(name).stat().st_mtime) / 60:.0f} min)'
    except FileNotFoundError:
        minutes = ''
    return f'{text}{minutes}' if text else 'free'


def held_by(name):
    return f'{name} held by: {holding(name)}'


def take(name, holder, wait, process=False, reader=False):
    """process: name this process in the lock (run), so a lock it can no longer release counts as free.
    reader: a job that only reads what the build lock guards (vita.py's sync of the PS Vita build): it
    doesn't wait for the other build lock, and the other doesn't wait for it (building)."""
    lock = path(name)
    others = [] if reader else other_build_locks(name)
    end = time.time() + wait
    own = f'pid {os.getpid()} {process_start(os.getpid())}\n' if process else ''
    own += f'{READER}\n' if reader else ''
    waiting = False
    while True:
        for other in [name, *others]:
            clear_stale(other)
        busy = next((other for other in others if building(other)), None)
        if busy is None:
            try:
                fd = os.open(lock, os.O_CREAT | os.O_EXCL | os.O_WRONLY)
            except (FileExistsError, PermissionError):  # PermissionError: the old one is being deleted
                busy = name
            else:
                os.write(fd, f'{holder} {time.strftime("%H:%M")}\n{own}'.encode())
                os.close(fd)
                busy = next((other for other in others if building(other)), None)
                if busy is None:
                    return 0
                remove(lock)  # the other build lock was taken in between: one pipeline at a time
        if time.time() >= end:
            print(held_by(busy), flush=True)
            return NOT_TAKEN
        if not waiting:
            waiting = True
            print(f'{held_by(busy)}; waiting up to {wait:.0f} s for {name}', flush=True)
        time.sleep(POLL_SECONDS)


def wait_free(name, wait):
    """Wait until name could be taken (it and, for a build lock, the other one free) without taking
    it: 0, or NOT_TAKEN after wait seconds. Another job may take it before the caller does. A reader
    holding the other build lock doesn't count (take's reader), one holding name does."""
    end = time.time() + wait
    others = other_build_locks(name)
    waiting = False
    while True:
        for other in [name, *others]:
            clear_stale(other)
        busy = name if path(name).exists() else next((other for other in others if building(other)), None)
        if busy is None:
            return 0
        if time.time() >= end:
            print(held_by(busy), flush=True)
            return NOT_TAKEN
        if not waiting:
            waiting = True
            print(f'{held_by(busy)}; waiting up to {wait:.0f} s', flush=True)
        time.sleep(max(0.0, min(POLL_SECONDS, end - time.time())))


def owner(text):
    """The name before the purpose in a holder ("me: purpose") or a lock's text ("me: purpose 16:09")."""
    return re.sub(r' \d{1,2}:\d\d$', '', text.strip()).split(':')[0].strip()


def release(name, holder):
    text = holder_of(name)
    if text is None:
        return 0
    # The whole name must match: "a" must not release "ab: ..." (or "" anyone's lock).
    if not owner(holder) or owner(text) != owner(holder):
        print(f'{name} is held by someone else: {text}')
        return 1
    remove(path(name))
    return 0


def held_here():
    """The locks the runs this process is in hold: CROW_LOCKS, which each run extends (`lock.py run
    vita-build -- ... lock.py run vita-device -- vita.py sync` holds both)."""
    return [name for name in dict.fromkeys(os.environ.get('CROW_LOCKS', '').split(',')) if name]


def ours(name):
    """Whether this process may act as the lock's holder: a run it is in holds it (held_here), or
    CROW_HOLDER names the holder of a lock taken with take."""
    if name in held_here():
        return True
    text, mine = holder_of(name), os.environ.get('CROW_HOLDER', '')
    return bool(text and mine and owner(mine) == owner(text))


def run(name, holder, wait, command):
    if not command:
        sys.exit('run: no command after --')
    join_job_ending_with_this_process()
    if take(name, holder, wait, process=True) != 0:
        return NOT_TAKEN
    try:
        locks = ','.join(dict.fromkeys([*held_here(), name]))
        return run_to_end(command, {**os.environ, 'CROW_LOCK': name, 'CROW_LOCKS': locks})
    finally:
        release(name, holder)


def run_to_end(command, env):
    """Run the command and wait for it, also after Ctrl+C: the console sends Ctrl+C to the command as
    well, which ends it in its own time (subprocess.run would kill it a quarter of a second later, in
    the middle of a console transfer). The command's exit code, or INTERRUPTED after a Ctrl+C."""
    process = subprocess.Popen(command, env=env)
    interrupted = False
    while True:
        try:
            code = process.wait()
            return INTERRUPTED if interrupted else code
        except KeyboardInterrupt:
            interrupted = True
            print('lock.py: waiting for the command to end (Ctrl+Break stops both at once)', flush=True)


def join_job_ending_with_this_process():
    """Put this process in a Windows job that kills its processes when its last handle closes. The
    only handle is this process's, never closed, so whatever the command starts (the pipeline, the
    Unity editor it runs) ends when lock.py ends, however that happens: a killed lock.py would leave
    its Unity editor building."""
    kernel32 = winapi.kernel32
    try:
        job = winapi.kill_on_close_job()
        winapi.check(kernel32.AssignProcessToJobObject(job, kernel32.GetCurrentProcess()), 'AssignProcessToJobObject')
    except OSError as error:
        print(f'lock.py: could not put the command in a job ({error}); it can outlive lock.py')


def main(argv):
    sys.stdout.reconfigure(errors='replace')  # a holder's name the console's code page lacks
    command = argv[argv.index('--') + 1 :] if '--' in argv else []
    argv = argv[: argv.index('--')] if '--' in argv else argv
    parser = argparse.ArgumentParser(
        prog='lock.py', description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter
    )
    parser.add_argument('action', choices=['take', 'release', 'run', 'wait', 'status'])
    parser.add_argument('name', nargs='?', choices=LOCKS)
    parser.add_argument('holder', nargs='?', help='"<name>: <purpose>"; release needs only the name')
    parser.add_argument(
        '--wait',
        dest='wait',
        type=float,
        default=0,
        metavar='SECONDS',
        help='take, run, wait: how long to wait for the lock (default 0: fail at once)',
    )
    args = parser.parse_args(argv)
    if args.action == 'status':
        for name in LOCKS:
            note = ' (stale: its lock.py has ended; the next take removes it)' if stale(name) else ''
            print(f'{name:12} {holding(name)}{note}')
        return 0
    if args.action == 'wait':
        if args.name is None:
            parser.error('wait needs a lock name')
        return wait_free(args.name, args.wait)
    if args.holder is None:
        parser.error(f'{args.action} needs a lock name and a holder')
    if args.action == 'take':
        return take(args.name, args.holder, args.wait)
    if args.action == 'release':
        return release(args.name, args.holder)
    return run(args.name, args.holder, args.wait, command)


if __name__ == '__main__':
    sys.exit(main(sys.argv[1:]))
