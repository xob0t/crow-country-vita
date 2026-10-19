"""Run a Windows player on a desktop of its own, so it never takes the screen, the keyboard or the
mouse from whoever uses the PC: Unity's players bring their window to the front whatever the
launcher asks, but a window on another desktop (CreateDesktop) cannot come in front of this one.
The player runs windowed at 960x544 (-crow-windowed) and muted (-crow-mute). The project keeps
runInBackground on. The player's log goes to <log> (through cmd's redirection,
since a process on another desktop gets its standard handles from its command line).

    uv run tools/downgrade/run_player.py <exe> <log> [--timeout s] -- <player arguments...>

Exits with the player's exit code; a player still running after --timeout seconds is killed.

The test tools run a copy of a build (player_copy) without Steam's steam_api library and
steam_appid.txt.
"""

import argparse
import ctypes
import ctypes.wintypes as wt
import os
import shutil
import subprocess
import sys
import time
from pathlib import Path

from common import split_at_dashes

kernel32 = ctypes.WinDLL('kernel32', use_last_error=True)
user32 = ctypes.WinDLL('user32', use_last_error=True)
DESKTOP = 'crow-country-runs'
GENERIC_ALL = 0x10000000
CREATE_NO_WINDOW = 0x08000000
WAIT_TIMEOUT = 0x102


class StartupInfo(ctypes.Structure):
    _fields_ = [
        ('cb', wt.DWORD),
        ('lpReserved', wt.LPWSTR),
        ('lpDesktop', wt.LPWSTR),
        ('lpTitle', wt.LPWSTR),
        ('dwX', wt.DWORD),
        ('dwY', wt.DWORD),
        ('dwXSize', wt.DWORD),
        ('dwYSize', wt.DWORD),
        ('dwXCountChars', wt.DWORD),
        ('dwYCountChars', wt.DWORD),
        ('dwFillAttribute', wt.DWORD),
        ('dwFlags', wt.DWORD),
        ('wShowWindow', wt.WORD),
        ('cbReserved2', wt.WORD),
        ('lpReserved2', ctypes.c_void_p),
        ('hStdInput', wt.HANDLE),
        ('hStdOutput', wt.HANDLE),
        ('hStdError', wt.HANDLE),
    ]


class ProcessInformation(ctypes.Structure):
    _fields_ = [('hProcess', wt.HANDLE), ('hThread', wt.HANDLE), ('dwProcessId', wt.DWORD), ('dwThreadId', wt.DWORD)]


user32.CreateDesktopW.restype = wt.HANDLE
user32.CreateDesktopW.argtypes = [wt.LPCWSTR, wt.LPCWSTR, ctypes.c_void_p, wt.DWORD, wt.DWORD, ctypes.c_void_p]
kernel32.CreateProcessW.argtypes = [
    wt.LPCWSTR,
    wt.LPWSTR,
    ctypes.c_void_p,
    ctypes.c_void_p,
    wt.BOOL,
    wt.DWORD,
    ctypes.c_void_p,
    wt.LPCWSTR,
    ctypes.POINTER(StartupInfo),
    ctypes.POINTER(ProcessInformation),
]
kernel32.WaitForSingleObject.argtypes = [wt.HANDLE, wt.DWORD]
kernel32.GetExitCodeProcess.argtypes = [wt.HANDLE, ctypes.POINTER(wt.DWORD)]
kernel32.TerminateProcess.argtypes = [wt.HANDLE, wt.UINT]
kernel32.CloseHandle.argtypes = [wt.HANDLE]


def run(exe: Path, log: Path, arguments, timeout=None, on_start=None):
    """Runs the player on DESKTOP; returns its exit code (1 if it was killed at the timeout).
    on_start(pid), if given, is called with the player's process id once it runs (cmd starts it) and
    may return a function to call when the run ends (fightbench.py's vitalike duty cycle)."""
    desktop = user32.CreateDesktopW(DESKTOP, None, None, 0, GENERIC_ALL, None)
    if not desktop:
        raise OSError(ctypes.get_last_error(), 'CreateDesktop failed')
    log.parent.mkdir(parents=True, exist_ok=True)
    player = subprocess.list2cmdline(
        [
            str(exe.resolve()),
            '-screen-fullscreen',
            '0',
            '-screen-width',
            '960',
            '-screen-height',
            '544',
            '-logFile',
            '-',
            '-crow-windowed',
            '-crow-mute',
            *arguments,
        ]
    )
    command = f'cmd.exe /d /s /c "{player} > "{log.resolve()}" 2>&1"'
    startup = StartupInfo()
    startup.cb = ctypes.sizeof(StartupInfo)
    startup.lpDesktop = DESKTOP
    info = ProcessInformation()
    if not kernel32.CreateProcessW(
        None,
        command,
        None,
        None,
        False,
        CREATE_NO_WINDOW,
        None,
        str(exe.parent),
        ctypes.byref(startup),
        ctypes.byref(info),
    ):
        raise OSError(ctypes.get_last_error(), 'CreateProcess failed')
    stop = None
    try:
        if on_start is not None:
            player_pid = child_process(info.dwProcessId, exe.name)
            stop = on_start(player_pid) if player_pid else None
        waited = kernel32.WaitForSingleObject(info.hProcess, 0xFFFFFFFF if timeout is None else int(timeout * 1000))
        if waited == WAIT_TIMEOUT:
            # cmd and the player: the player is cmd's child; taskkill takes the tree.
            subprocess.run(['taskkill', '/T', '/F', '/PID', str(info.dwProcessId)], capture_output=True)
            return 1
        code = wt.DWORD()
        kernel32.GetExitCodeProcess(info.hProcess, ctypes.byref(code))
        return code.value
    finally:
        if stop is not None:
            stop()
        kernel32.CloseHandle(info.hThread)
        kernel32.CloseHandle(info.hProcess)


class ProcessEntry(ctypes.Structure):  # PROCESSENTRY32W
    _fields_ = [
        ('dwSize', wt.DWORD),
        ('cntUsage', wt.DWORD),
        ('th32ProcessID', wt.DWORD),
        ('th32DefaultHeapID', ctypes.c_size_t),
        ('th32ModuleID', wt.DWORD),
        ('cntThreads', wt.DWORD),
        ('th32ParentProcessID', wt.DWORD),
        ('pcPriClassBase', wt.LONG),
        ('dwFlags', wt.DWORD),
        ('szExeFile', wt.WCHAR * 260),
    ]


def child_process(parent: int, name: str, wait=30.0):
    """The id of the process called name that parent started (cmd's player), or None after wait seconds."""
    kernel32.CreateToolhelp32Snapshot.restype = wt.HANDLE
    kernel32.Process32FirstW.argtypes = [wt.HANDLE, ctypes.POINTER(ProcessEntry)]
    kernel32.Process32NextW.argtypes = [wt.HANDLE, ctypes.POINTER(ProcessEntry)]
    deadline = time.time() + wait
    while time.time() < deadline:
        snapshot = kernel32.CreateToolhelp32Snapshot(0x2, 0)  # TH32CS_SNAPPROCESS
        entry = ProcessEntry()
        entry.dwSize = ctypes.sizeof(ProcessEntry)
        try:
            more = kernel32.Process32FirstW(snapshot, ctypes.byref(entry))
            while more:
                if entry.th32ParentProcessID == parent and entry.szExeFile.lower() == name.lower():
                    return entry.th32ProcessID
                more = kernel32.Process32NextW(snapshot, ctypes.byref(entry))
        finally:
            kernel32.CloseHandle(snapshot)
        time.sleep(0.1)
    return None


# Steam's files, left out of every player copy: with them a test player once signed in to the user's
# Steam account (play time, achievements). The build's Steam wrappers are stubs anyway (pipeline.py's
# STUBBED_ON).
STEAM_FILES = ('steam_api', 'steam_appid')
COPY_SOURCE = 'crow-copy-source.txt'  # in a copy: the sizes and times of the build files it was made from


def player_copy(build: Path, copy: Path, link=True, sources=()):
    """A copy of the Windows player in build, in copy, without STEAM_FILES; its executable.
    link: hard links (nothing copied, but a file the build rewrites in place changes in the copy as
    well), else real copies (a run then never holds the build's files open while pipeline.py rebuilds
    it). sources: build files (relative paths) whose sizes and times decide when an existing copy
    is made again; without them it is made again every time."""
    exe = build / 'CrowCountry.exe'
    if not exe.exists():
        sys.exit(f'No Windows player in {build}')
    missing = [name for name in sources if not (build / name).exists()]
    if missing:
        sys.exit(f'No {", ".join(missing)} in {build}')
    stamp = ' '.join(f'{(build / n).stat().st_size}:{(build / n).stat().st_mtime_ns}' for n in sources)
    marker = copy / COPY_SOURCE
    if sources and marker.exists() and marker.read_text() == stamp:
        return copy / exe.name
    if copy.exists():
        shutil.rmtree(copy)
    for source in build.rglob('*'):
        if source.name.lower().startswith(STEAM_FILES):
            continue
        target = copy / source.relative_to(build)
        if source.is_dir():
            target.mkdir(parents=True, exist_ok=True)
            continue
        target.parent.mkdir(parents=True, exist_ok=True)
        if link:
            try:
                os.link(source, target)
                continue
            except OSError:
                pass
        shutil.copy2(source, target)
    if sources:
        marker.write_text(stamp)
    return copy / exe.name


def main():
    own, arguments = split_at_dashes(sys.argv[1:])
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument('exe', type=Path)
    parser.add_argument('log', type=Path)
    parser.add_argument('--timeout', type=float, default=600)
    args = parser.parse_args(own)
    sys.exit(run(args.exe, args.log, arguments, args.timeout))


if __name__ == '__main__':
    main()
