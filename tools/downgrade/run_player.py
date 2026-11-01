"""Run a Windows player on a desktop of its own, so it never takes the screen, the keyboard or the
mouse from whoever uses the PC: Unity's players bring their window to the front whatever the
launcher asks, but a window on another desktop (CreateDesktop) cannot come in front of this one.
The player runs windowed at 960x544 (-crow-windowed) and muted: -crow-mute, and Windows' per-app
mute of its audio sessions for the whole run (mute_players.ps1). The project keeps
runInBackground on. The player's log goes to <log>: its standard output and error are a handle to
the file it inherits (standard handles don't depend on the desktop), so no shell sees the player's
arguments (cmd's redirection took the > of `-crow-scene Pool>Roadside` for its own).

    uv run tools/downgrade/run_player.py <exe> <log> [--timeout s] -- <player arguments...>

Exits with the player's exit code; a player still running after --timeout seconds (default 600) is
killed, with whatever it started, and the exit code is 124 (TIMED_OUT).

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
from pathlib import Path

from common import split_at_dashes

kernel32 = ctypes.WinDLL('kernel32', use_last_error=True)
user32 = ctypes.WinDLL('user32', use_last_error=True)
DESKTOP = 'crow-country-runs'
GENERIC_ALL = 0x10000000
CREATE_NO_WINDOW = 0x08000000
WAIT_TIMEOUT = 0x102
TIMED_OUT = 124  # run's code for a player killed at the timeout (as timeout(1) has it)


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


class StartupInfoEx(ctypes.Structure):
    _fields_ = [('StartupInfo', StartupInfo), ('lpAttributeList', ctypes.c_void_p)]


class ProcessInformation(ctypes.Structure):
    _fields_ = [('hProcess', wt.HANDLE), ('hThread', wt.HANDLE), ('dwProcessId', wt.DWORD), ('dwThreadId', wt.DWORD)]


class SecurityAttributes(ctypes.Structure):
    _fields_ = [('nLength', wt.DWORD), ('lpSecurityDescriptor', ctypes.c_void_p), ('bInheritHandle', wt.BOOL)]


GENERIC_WRITE = 0x40000000
FILE_SHARE_READ_WRITE = 0x3
CREATE_ALWAYS = 2
STARTF_USESTDHANDLES = 0x100
EXTENDED_STARTUPINFO_PRESENT = 0x80000
PROC_THREAD_ATTRIBUTE_HANDLE_LIST = 0x20002
INVALID_HANDLE = ctypes.c_void_p(-1).value

user32.CreateDesktopW.restype = wt.HANDLE
user32.CreateDesktopW.argtypes = [wt.LPCWSTR, wt.LPCWSTR, ctypes.c_void_p, wt.DWORD, wt.DWORD, ctypes.c_void_p]
kernel32.CreateFileW.restype = wt.HANDLE
kernel32.CreateFileW.argtypes = [
    wt.LPCWSTR,
    wt.DWORD,
    wt.DWORD,
    ctypes.POINTER(SecurityAttributes),
    wt.DWORD,
    wt.DWORD,
    wt.HANDLE,
]
kernel32.InitializeProcThreadAttributeList.argtypes = [
    ctypes.c_void_p,
    wt.DWORD,
    wt.DWORD,
    ctypes.POINTER(ctypes.c_size_t),
]
kernel32.UpdateProcThreadAttribute.argtypes = [
    ctypes.c_void_p,
    wt.DWORD,
    ctypes.c_size_t,
    ctypes.c_void_p,
    ctypes.c_size_t,
    ctypes.c_void_p,
    ctypes.c_void_p,
]
kernel32.DeleteProcThreadAttributeList.argtypes = [ctypes.c_void_p]
kernel32.CreateProcessW.argtypes = [
    wt.LPCWSTR,
    wt.LPWSTR,
    ctypes.c_void_p,
    ctypes.c_void_p,
    wt.BOOL,
    wt.DWORD,
    ctypes.c_void_p,
    wt.LPCWSTR,
    ctypes.POINTER(StartupInfoEx),
    ctypes.POINTER(ProcessInformation),
]
kernel32.WaitForSingleObject.argtypes = [wt.HANDLE, wt.DWORD]
kernel32.GetExitCodeProcess.argtypes = [wt.HANDLE, ctypes.POINTER(wt.DWORD)]
kernel32.TerminateProcess.argtypes = [wt.HANDLE, wt.UINT]
kernel32.CloseHandle.argtypes = [wt.HANDLE]


def run(exe: Path, log: Path, arguments, timeout=None, on_start=None):
    """Runs the player on DESKTOP; returns its exit code (TIMED_OUT if it was killed at the timeout).
    on_start(pid), if given, is called with the player's process id once it runs and may return a
    function to call when the run ends (fightbench.py's vitalike duty cycle)."""
    desktop = user32.CreateDesktopW(DESKTOP, None, None, 0, GENERIC_ALL, None)
    if not desktop:
        raise OSError(ctypes.get_last_error(), 'CreateDesktop failed')
    log.parent.mkdir(parents=True, exist_ok=True)
    command = subprocess.list2cmdline(
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
    # The log, as cmd's > opened it (readable while the player writes it), for the player's standard
    # output and error. The player inherits only this handle (the attribute list's handle list), not
    # whatever another thread of the caller has open to inherit at that moment.
    inherit = SecurityAttributes(ctypes.sizeof(SecurityAttributes), None, True)
    output = kernel32.CreateFileW(
        str(log.resolve()), GENERIC_WRITE, FILE_SHARE_READ_WRITE, ctypes.byref(inherit), CREATE_ALWAYS, 0, None
    )
    if output in (None, INVALID_HANDLE):
        raise OSError(ctypes.get_last_error(), f'CreateFile failed for {log}')
    size = ctypes.c_size_t()
    kernel32.InitializeProcThreadAttributeList(None, 1, 0, ctypes.byref(size))
    attributes = ctypes.create_string_buffer(size.value)
    handles = (wt.HANDLE * 1)(output)
    info = ProcessInformation()
    try:
        if not kernel32.InitializeProcThreadAttributeList(attributes, 1, 0, ctypes.byref(size)):
            raise OSError(ctypes.get_last_error(), 'InitializeProcThreadAttributeList failed')
        try:
            if not kernel32.UpdateProcThreadAttribute(
                attributes, 0, PROC_THREAD_ATTRIBUTE_HANDLE_LIST, handles, ctypes.sizeof(handles), None, None
            ):
                raise OSError(ctypes.get_last_error(), 'UpdateProcThreadAttribute failed')
            startup = StartupInfoEx()
            startup.StartupInfo.cb = ctypes.sizeof(StartupInfoEx)
            startup.StartupInfo.lpDesktop = DESKTOP
            startup.StartupInfo.dwFlags = STARTF_USESTDHANDLES
            startup.StartupInfo.hStdOutput = startup.StartupInfo.hStdError = output
            startup.lpAttributeList = ctypes.cast(attributes, ctypes.c_void_p)
            if not kernel32.CreateProcessW(
                None,
                command,
                None,
                None,
                True,
                CREATE_NO_WINDOW | EXTENDED_STARTUPINFO_PRESENT,
                None,
                str(exe.parent),
                ctypes.byref(startup),
                ctypes.byref(info),
            ):
                raise OSError(ctypes.get_last_error(), 'CreateProcess failed')
        finally:
            kernel32.DeleteProcThreadAttributeList(attributes)
    finally:
        kernel32.CloseHandle(output)  # the player has its own
    stop = None
    # -crow-mute only holds the listener's volume, from CrowCompat's start on: Windows' per-app mute
    # covers everything the player outputs, for as long as this run (mute_players.ps1).
    muter = subprocess.Popen(
        [
            'powershell',
            '-NoProfile',
            '-ExecutionPolicy',
            'Bypass',
            '-File',
            str(Path(__file__).with_name('mute_players.ps1')),
            '-Watch',
            '-Name',
            exe.stem,
            '-Until',
            str(os.getpid()),
        ],
        stdout=subprocess.DEVNULL,
        stderr=subprocess.DEVNULL,
        creationflags=CREATE_NO_WINDOW,
    )
    try:
        if on_start is not None:
            stop = on_start(info.dwProcessId)
        waited = kernel32.WaitForSingleObject(info.hProcess, 0xFFFFFFFF if timeout is None else int(timeout * 1000))
        if waited == WAIT_TIMEOUT:
            # The player and anything it started (a crash handler).
            subprocess.run(['taskkill', '/T', '/F', '/PID', str(info.dwProcessId)], capture_output=True, check=False)
            return TIMED_OUT
        code = wt.DWORD()
        kernel32.GetExitCodeProcess(info.hProcess, ctypes.byref(code))
        return code.value
    finally:
        if stop is not None:
            stop()
        muter.kill()
        kernel32.CloseHandle(info.hThread)
        kernel32.CloseHandle(info.hProcess)


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
