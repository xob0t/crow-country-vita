"""Drive the PS Vita build remotely: install updates, launch, screenshots, logs.

    uv run tools/downgrade/vita.py <command> [options]

The console is at CROW_VITA_HOST (or DRIFTER_VITA_HOST). sync, log, args, files and crashes need
only an FTP server on port 1337: VitaShell's (press Select in VitaShell) or vitacompanion's; seed
needs no console. The other commands need vitacompanion's command server on port 1338. The app
must have been installed once from the VPK (install, or the VPK in VitaShell); after that:

  sync [--build dir]    upload the files that changed since the last sync into
                        ux0:app/<title> (the first sync uploads everything)
  seed [--build dir]    record that build as installed (e.g. from its VPK) without
                        uploading, so the next sync sends only differences
  install [--build dir] package the build as a VPK and stream it to the installer (also
                        what applies param.sfo changes such as the memory expansion)
  launch / quit         start the game, or quit all apps
  run                   quit, sync, launch
  do "<chain>"          run a vitacompanion command chain, e.g. synthetic input:
                        do "press up; wait 100ms; release up"
  shot [out.png]        system screenshot of whatever is displayed (any build)
  reboot                reboot the console
  perf                  clocks, CPU load per core, frame rate and battery (vitacompanion)
  log [--tail N] [--prev [N]]  print the player's log (ux0:data/crow/log.txt, CrowCompat Diag);
                        --prev the run before's (log.prev.txt), --prev 2 the one before that
  args [flags...]       set the probe's -crow-* flags for the next launch (none clears; a flag's
                        value with spaces, such as "Station Square", is quoted in the file)
  args --show           print the flags the next launch gets, changing nothing
  files [folder]        list the files under a console folder with their sizes and times (default
                        the game's saves and settings, ux0:data/CrowCountry, compat/Saves.cs)
  crashes [--pull] [--thread NAME]  list crash dumps in ux0:data, newest last; --pull fetches the
                        newest and prints the crashed thread's symbolized call chain
                        (needs the SDK's psp2bin and the build's SymbolFiles; sync, install and
                        seed keep a copy of the game module's symbols per build in
                        artifacts/vita/symbols, and the copy whose fingerprint the dump lists is
                        used); --dump FILE reads a local dump

The commands that change the console (sync, install, launch, quit, run, do, reboot, args) refuse
while someone else holds the vita-device lock, and sync, install and seed read the build under the
vita-build lock. docs/vita.md (vita.py) has the locks, the vitacompanion versions each command needs,
why not to kill vita.py while it transfers a file, and keeping the display on.
"""

import argparse
import contextlib
import ftplib
import hashlib
import io
import json
import os
import re
import shutil
import signal
import socket
import subprocess
import sys
import threading
import time
from pathlib import Path

import lock
import pipeline
from common import ARTIFACTS, EXTERNAL, ROOT, TARGET, VITA_HOST, build_folder, editor_data, log, setting, vita_host

REMOTE = 'ux0:/data/crow'  # CrowCompat Diag's folder on the console (args.txt, log.txt)
ARGS = f'{REMOTE}/args.txt'
SAVES = 'ux0:/data/CrowCountry'  # the game's saves and persistentDataPath on the console (compat/Saves.cs)
APP = f'ux0:/app/{pipeline.VITA_TITLE_ID}'
OUT = ROOT / 'artifacts' / 'vita'  # screenshots, crash dumps, the symbols kept for them
# What a dropped or stalled FTP session raises (TimeoutError is an OSError).
FTP_ERRORS = (OSError, EOFError, ftplib.Error)
FTP_SECONDS = 30  # connect, and any read or write of a transfer, before it counts as stalled
PART = '.part'  # an upload's name until it is complete (upload)
# The commands that change what the console runs or reads; they respect the vita-device lock.
CHANGING = ('sync', 'install', 'launch', 'quit', 'run', 'do', 'reboot', 'args')
# The commands that take words after them (the rest of the command line), and how many at most.
WORDS = {'do': None, 'args': None, 'shot': 1, 'files': 1}
# The vitacompanion chain that keeps the console awake, and how often it is repeated (keep_awake).
KEEP_AWAKE = 'nosleep on; screen on'
KEEP_AWAKE_SECONDS = 10
WAKE_SECONDS = 5  # how long wake waits for the screen to draw again after a "screen on"
BUILD_WAIT_SECONDS = 900  # how long sync, run, install and seed wait for a build holding vita-build (reading)


class Unreachable(OSError):
    """The console's FTP or command server does not answer (main prints the message)."""


def ftp():
    vita_host()
    connection = ftplib.FTP()
    try:
        connection.connect(*pipeline.VITA_FTP, timeout=FTP_SECONDS)
    except OSError as error:
        raise Unreachable(
            f"No FTP server at {VITA_HOST}:{pipeline.VITA_FTP[1]} ({error}). Start VitaShell's FTP "
            f'server (press Select in VitaShell), or run vitacompanion.'
        ) from error
    connection.login()
    return connection


def connect_commands(timeout=10):
    """A connection to vitacompanion's command server, or Unreachable."""
    vita_host()
    try:
        return socket.create_connection(pipeline.VITA_COMMANDS, timeout=timeout)
    except OSError as error:
        raise Unreachable(
            f"vitacompanion's command server does not answer at {VITA_HOST}:{pipeline.VITA_COMMANDS[1]} "
            f'({error}). This needs vitacompanion running on the console. Without it, install '
            f"the VPK in VitaShell, update with VitaShell's FTP server and `vita.py sync`, and "
            f'start the game from the LiveArea.'
        ) from error


def command(chain, timeout=10):
    """Runs a vitacompanion command chain ("quit all; wait 2s; launch ...") and returns its output."""
    with connect_commands() as connection:
        connection.sendall((chain + '\n').encode())
        connection.settimeout(timeout)
        output = b''
        try:
            while chunk := connection.recv(4096):
                output += chunk
        except TimeoutError:
            pass
    return output.decode(errors='replace').strip()


# Commands of the released vitacompanion: help, launch, nosleep, press, promote, quit, release,
# reboot, screen, version, wait. install, perf and screenshot come from an extended build.
EXTENDED = ('install', 'perf', 'screenshot')


def require(name):
    """Stops with a message if the console's vitacompanion lacks the command `name`. An empty answer
    to help (the connection closed early) can't tell: the command goes ahead, with a note."""
    listed = command('help', timeout=4)
    names = {line.split()[0] for line in listed.splitlines() if line.strip()}
    if not listed:
        log(f"vitacompanion's help came back empty: can't tell whether it has `{name}`; trying it")
    elif name not in names:
        hint = (
            ' To install, make the VPK with `pipeline.py package`, copy it to the console and install it in VitaShell.'
            if name == 'install'
            else ''
        )
        sys.exit(
            f'The vitacompanion on the console has no `{name}` command (the released vitacompanion '
            f'lacks {", ".join(EXTENDED)}).{hint}'
        )


_awake = None


def nudge():
    """Sends KEEP_AWAKE once; a console that does not answer is left alone."""
    with contextlib.suppress(OSError):
        command(KEEP_AWAKE, timeout=2)


def wake():
    """KEEP_AWAKE, then waits (at most WAKE_SECONDS) until something on screen draws again. When the
    console's display goes off on its idle timer, the system pauses the game (perf: "app: none", app
    and shell at 0 fps, the GPU at 41 MHz) while FTP and the command server stay up, since
    vitacompanion's "nosleep" holds off standby but not the display's timeout; "screen on" resumes it
    a moment later, and nothing is lost (the game's clock leaves the pause out). A screenshot taken
    right after "screen on" is black, and a script that quits a paused game ends its log with no
    dump, as if it had died (17 Oct: render r3's Pool, r4's Roadside)."""
    nudge()
    deadline = time.time() + WAKE_SECONDS
    while time.time() < deadline:
        try:
            out = command('perf', timeout=3)
        except OSError:
            return
        fps = re.search(r'fps: app=([\d.]+) shell=([\d.]+)', out)
        if not fps:
            if out and 'fps' in out:
                log(f"wake: perf's answer has no frame rates it can read, so it doesn't wait: {out[:200]}")
            return  # no perf command
        if float(fps.group(1)) > 0 or float(fps.group(2)) > 0:
            return  # drawing


def keep_awake():
    """Keeps the console from sleeping while this script runs. vitacompanion's "nosleep on"
    holds off standby but not the display's idle timeout, which pauses the game (see wake);
    repeating "screen on" from the host keeps the display on."""
    global _awake
    if _awake:
        return

    def tick():
        while True:
            nudge()
            time.sleep(KEEP_AWAKE_SECONDS)

    _awake = threading.Thread(target=tick, daemon=True)
    _awake.start()


def shot(out: Path):
    """Saves the displayed frame through vitacompanion's screenshot command."""
    from PIL import Image

    require('screenshot')
    remote = 'ux0:data/vitacompanion_shot.bmp'
    # With the display off (its idle timeout) the screenshot was black: wake waits until it draws.
    wake()
    print((command(f'{KEEP_AWAKE}; screenshot {remote}').splitlines() or [''])[-1])
    data = read(remote)
    with ftp() as connection:
        connection.delete(remote)
    out.parent.mkdir(parents=True, exist_ok=True)
    Image.open(io.BytesIO(data)).convert('RGB').save(out)
    print(f'saved {out}')


def install(build: Path, build_wait=BUILD_WAIT_SECONDS):
    """Packages the build's VPK and streams it to vitacompanion's install command
    ("install - <size>", then the VPK; the Vita extracts it as it arrives), then records
    the build as synced."""
    require('install')
    with reading(build, build_wait):
        vpk = pipeline.package_vita(build)
        size = vpk.stat().st_size
        with (
            connect_commands(timeout=30) as connection,
            vpk.open('rb') as handle,
            transferring(f'sending and installing {vpk.name}'),
        ):
            connection.sendall(f'install - {size}\n'.encode())
            connection.settimeout(300)  # the Vita stops reading while it writes out large files
            sent, started, shown = 0, time.time(), 0
            while chunk := handle.read(1 << 20):
                connection.sendall(chunk)
                sent += len(chunk)
                if sent - shown >= 100 << 20 or sent == size:
                    shown = sent
                    log(f'Sent {sent >> 20} of {size >> 20} MB ({sent / 2**20 / (time.time() - started):.1f} MB/s)')
            connection.settimeout(1800)  # promoting a large package takes a while after the last byte
            reply = b''
            while not reply.endswith(b'\n') and (chunk := connection.recv(4096)):
                reply += chunk
        reply = reply.decode(errors='replace').strip()
        print(reply)
        if not reply.startswith('Installed'):
            sys.exit(f'Installation failed: {reply}')
        seed(build)


@contextlib.contextmanager
def transferring(what):
    """Holds back Ctrl+C and Ctrl+Break while an FTP transfer runs: the console's FTP server can hang
    when a transfer is cut off mid-way, until the console reboots. The first one lets the transfer
    finish and then stops (KeyboardInterrupt); a second one stops it at once (KeyboardInterrupt in
    the transfer, whose data connection then closes cleanly: the server takes it as a short file).
    Off the main thread, where Python takes no signals, it does nothing."""
    if threading.current_thread() is not threading.main_thread():
        yield
        return
    caught = []

    def held(number, frame):
        caught.append(number)
        if len(caught) > 1:
            raise KeyboardInterrupt
        print(f'\nvita.py: finishing {what} first (again to cut it short)', file=sys.stderr, flush=True)

    kinds = [signal.SIGINT, *([signal.SIGBREAK] if hasattr(signal, 'SIGBREAK') else [])]
    before = {kind: signal.signal(kind, held) for kind in kinds}
    try:
        yield
    finally:
        for kind, handler in before.items():
            signal.signal(kind, handler)
    if caught:
        raise KeyboardInterrupt


def read(path):
    buffer = io.BytesIO()
    with ftp() as connection, transferring(path):
        try:
            connection.retrbinary(f'RETR {path}', buffer.write)
        except KeyboardInterrupt:
            with contextlib.suppress(*FTP_ERRORS):
                connection.voidresp()  # the cut transfer's reply, before QUIT's
            raise
    return buffer.getvalue()


def write(path, data: bytes):
    with ftp() as connection:
        upload(connection, path, data)


def upload(connection, target, data: bytes):
    """Sends data to target: into <target>.part, then renamed over target, so an upload that is
    stopped never leaves target cut short (one stopped with a second Ctrl+C also deletes its part).
    Where the part can't be written or renamed (a full memory card, an FTP server without RNFR),
    target is written in place, as before."""
    part = target + PART
    with transferring(target):
        try:
            connection.storbinary(f'STOR {part}', io.BytesIO(data), blocksize=1 << 16)
        except KeyboardInterrupt:
            discard_part(connection, part)
            raise
        except ftplib.error_perm as error:
            discard_part(connection, part, reply=False)
            log(f'{target}: {error}; writing it in place')
            connection.storbinary(f'STOR {target}', io.BytesIO(data), blocksize=1 << 16)
            return
        with contextlib.suppress(ftplib.error_perm):  # not there yet
            connection.delete(target)
        try:
            connection.rename(part, target)
        except ftplib.error_perm as error:
            log(f'{target}: renaming {PART} failed ({error}); writing it in place')
            connection.storbinary(f'STOR {target}', io.BytesIO(data), blocksize=1 << 16)
            discard_part(connection, part, reply=False)


def discard_part(connection, part, reply=True):
    """Deletes an upload's part file; reply: first read the reply of the transfer just cut short."""
    if reply:
        with contextlib.suppress(*FTP_ERRORS):
            connection.voidresp()
    with contextlib.suppress(*FTP_ERRORS):
        connection.delete(part)


def makedirs(connection, directory, known):
    parts = directory.split('/')
    for i in range(2, len(parts) + 1):
        partial = '/'.join(parts[:i])
        if partial in known:
            continue
        with contextlib.suppress(ftplib.error_perm):  # exists
            connection.mkd(partial)
        known.add(partial)


MANIFEST = ARTIFACTS / f'vita-synced-{pipeline.VITA_TITLE_ID}.json'


def digests(build: Path):
    for name, source in pipeline.vita_app_files(build):
        data = source if isinstance(source, bytes) else source.read_bytes()
        yield name, data, hashlib.sha1(data).hexdigest()


def seed(build: Path):
    MANIFEST.parent.mkdir(parents=True, exist_ok=True)
    MANIFEST.write_text(json.dumps({name: digest for name, _, digest in digests(build)}, indent=0))
    log(f'Recorded {build} as installed')
    keep_symbols(build)


@contextlib.contextmanager
def reading(build: Path, wait=BUILD_WAIT_SECONDS):
    """Holds the vita-build lock while the build's files are read, unless the caller's job holds it
    (lock.ours), as a reader: a PC build neither waits for it nor holds it up (docs/vita.md)."""
    if lock.ours('vita-build'):
        yield
        return
    holder = f'vita.py {os.getpid()}: reading {build.name} for the console'
    if lock.take('vita-build', holder, wait, process=True, reader=True) != 0:
        print(
            f'vita.py: a build still holds vita-build after {wait:.0f} s and may be writing {build}; '
            'try again later (--build-wait SECONDS waits longer)',
            file=sys.stderr,
        )
        sys.exit(lock.NOT_TAKEN)
    try:
        yield
    finally:
        lock.release('vita-build', holder)


def sync(build: Path, build_wait=BUILD_WAIT_SECONDS):
    """Uploads the build's app files whose content changed since the last sync."""
    with reading(build, build_wait):
        sync_files(build)


def sync_files(build: Path):
    synced = json.loads(MANIFEST.read_text()) if MANIFEST.exists() else {}
    connection = ftp()
    try:
        try:
            connection.cwd(APP)
        except ftplib.error_perm:
            sys.exit(
                f'{APP} does not exist: install the VPK once (vita.py install, or pipeline.py package and VitaShell)'
            )
        changed, total, known = 0, 0, set()
        for name, data, digest in digests(build):
            total += 1
            if synced.get(name) == digest:
                continue
            # Out of the record until it is sent whole: a sync stopped while sending it sends it again
            # next time, even for a build that has the recorded file back.
            if synced.pop(name, None) is not None:
                MANIFEST.write_text(json.dumps(synced, indent=0))
            target = f'{APP}/{name}'
            # The console's FTP server sometimes stops answering mid-transfer; a new
            # connection usually carries on.
            for attempt in range(3):
                try:
                    makedirs(connection, target.rsplit('/', 1)[0], known)
                    upload(connection, target, data)
                    break
                except FTP_ERRORS as error:
                    if attempt == 2:
                        raise
                    log(f'{name}: {error!r}; reconnecting')
                    with contextlib.suppress(OSError):
                        connection.close()
                    time.sleep(5)
                    connection = ftp()
            synced[name] = digest
            changed += 1
            MANIFEST.write_text(json.dumps(synced, indent=0))  # progress survives an interrupted sync
            if name == MODULE:
                keep_symbols(build)
    finally:
        connection.close()
    log(f'Synced {changed} of {total} files to {APP}')


# Copies of the IL2CPP module's symbols, one per build sent to the console, named by the module's
# fingerprint (its NID, which crash dumps list), so `crashes --pull` reads a dump with the symbols of
# the build that crashed even after the next quickcode has replaced SymbolFiles. About 55 MB each.
MODULE = 'Media/Modules/il2CppAssemblies.suprx'
SYMBOLS = OUT / 'symbols'
KEEP_SYMBOLS = 5
SYNCED = 'synced.txt'  # in each copy's folder: when it was last sent (seconds since the epoch)


def fingerprint(data: bytes):
    """The IL2CPP module's fingerprint from its sce_module_info (attributes, version, the name
    LIBIL2CPP_PRX at +4, the fingerprint at +0x34), or None."""
    at = data.find(b'LIBIL2CPP_PRX\0')
    return int.from_bytes(data[at + 0x30 : at + 0x34], 'little') if at >= 4 else None


def keep_symbols(build: Path):
    """Copies the build's SymbolFiles module into SYMBOLS/<fingerprint> when it matches the module
    sent, and drops all but the newest KEEP_SYMBOLS copies."""
    source = build / 'SymbolFiles' / 'il2CppAssemblies.suprx'
    if not source.exists() or not (build / MODULE).exists():
        return
    data = source.read_bytes()
    kept, sent = fingerprint(data), fingerprint((build / MODULE).read_bytes())
    if kept is None or kept != sent:
        log(f'SymbolFiles do not match the module sent ({kept} vs {sent}): no copy kept for crash dumps')
        return
    folder = SYMBOLS / f'{kept:08x}'
    folder.mkdir(parents=True, exist_ok=True)
    target = folder / source.name
    if not target.exists() or target.stat().st_size != len(data):
        part = target.with_name(target.name + '.part')
        part.write_bytes(data)
        part.replace(target)
    (folder / SYNCED).write_text(f'{time.time():.0f}\n')
    copies = sorted((p for p in SYMBOLS.iterdir() if (p / SYNCED).exists()), key=synced_at)
    for old in copies[:-KEEP_SYMBOLS]:
        shutil.rmtree(old, ignore_errors=True)
    log(f'Kept the symbols of module {kept:08x} in {folder.relative_to(ROOT)}')


def synced_at(folder: Path):
    try:
        return float((folder / SYNCED).read_text().strip())
    except (OSError, ValueError):
        return 0.0


PARSE_CORE_COMMIT = '644b5f081c5f3c9b205180793ab8f4209dfd9d97'
PARSE_CORE_SHA256 = '5b81bf0d6e05df282e1189b7c0e7bc9ffd3005655b2d2d1d6787747ac66bc96a'  # its GitHub zip
ENGINE_BASE = 0x81000000  # link address of the first segment of the player and of the IL2CPP module
STACK_WORDS = 700  # words of the crashed thread's stack scanned for return addresses
ADDR2LINE_BATCH = 40  # addresses per psp2bin call
PSP2BIN = 'host_tools/build/bin/psp2bin.exe'  # in the PS Vita SDK


def parse_core(dump: Path):
    """A crash dump read with vita-parse-core, which is downloaded on first use."""
    parser_dir = EXTERNAL / 'vita-parse-core'
    if not (parser_dir / 'util.py').exists():
        from common import github_source

        github_source('xyzz/vita-parse-core', PARSE_CORE_COMMIT, PARSE_CORE_SHA256, parser_dir)
    sys.path.insert(0, str(parser_dir))
    import util

    def c_str(buf, off):  # the parser is Python 2 code
        end = buf.find(b'\0', off)
        return bytes(buf[off : end if end >= 0 else len(buf)]).decode('latin-1')

    util.c_str = c_str
    import core as core_module

    core_module.c_str = c_str
    return core_module.CoreParser(str(dump))


def addr2line(binary: Path, addresses):
    """{address: symbol name} of addresses in a binary, from the SDK's psp2bin."""
    if not addresses:
        return {}
    sdk = setting('SCE_PSP2_SDK_DIR')
    psp2bin = Path(sdk) / PSP2BIN if sdk and (Path(sdk) / PSP2BIN).exists() else shutil.which('psp2bin')
    if psp2bin is None:
        sys.exit('No psp2bin.exe to resolve the addresses: point SCE_PSP2_SDK_DIR at the PS Vita SDK')
    names = {}
    for i in range(0, len(addresses), ADDR2LINE_BATCH):
        batch = ','.join(hex(a) for a in addresses[i : i + ADDR2LINE_BATCH])
        output = subprocess.run(
            [str(psp2bin), '-i', str(binary), '--addr2line=' + batch], capture_output=True, text=True
        ).stdout
        for block in output.split('Address:')[1:]:
            found = re.search(r'Symbol:[ \t]*(.*)', block)
            names[int(block.split()[0], 16)] = found.group(1).strip() if found else '?'
    return names


def module_fingerprints(core):
    """{module name: fingerprint} from the dump's MODULE_INFO (each module: a 0x50-byte head with
    the name at +0x24, the fingerprint at +0x48 and the segment count at +0x4c, its segments, a foot)."""
    data, found = core.notes['MODULE_INFO'], {}
    offset = 8
    for _ in range(int.from_bytes(data[4:8], 'little')):
        head = bytes(data[offset : offset + 0x50])
        name = head[0x24:0x44].split(b'\0')[0].decode('latin-1')
        found[name] = int.from_bytes(head[0x48:0x4C], 'little')
        offset += 0x50 + 0x14 * int.from_bytes(head[0x4C:0x50], 'little') + 0x10
    return found


def game_symbols(build: Path, wanted: int | None, dump: Path):
    """The symbols of the IL2CPP module the dump ran: the build's when its fingerprint matches, else a
    copy kept at a sync (keep_symbols), else the newest copy sent before the dump, with a warning."""
    current = build / 'SymbolFiles' / 'il2CppAssemblies.suprx'
    if wanted is None or (current.exists() and fingerprint(current.read_bytes()) == wanted):
        return current, True
    kept = SYMBOLS / f'{wanted:08x}' / current.name
    if kept.exists():
        print(f"game symbols: {kept.relative_to(ROOT)} (the dump's module {wanted:08x})")
        return kept, True
    try:
        crashed = int(dump.name.split('-')[1])  # psp2core-<seconds since the epoch>-...
    except (IndexError, ValueError):
        crashed = dump.stat().st_mtime
    copies = SYMBOLS.glob('*') if SYMBOLS.exists() else []
    before = [p for p in copies if (p / current.name).exists() and synced_at(p) <= crashed]
    chosen = max(before, key=synced_at) / current.name if before else current
    print(
        f"WARNING: no symbols of the dump's module {wanted:08x}; game frames are mapped onto "
        f'{chosen.relative_to(ROOT)} through the two exception indexes (marked ~)'
    )
    return chosen, False


def prel31(word, at):
    """The address an .ARM.exidx entry's first word points at."""
    offset = word & 0x7FFFFFFF
    return at + (offset - 0x80000000 if offset & 0x40000000 else offset)


def function_starts(data: bytes, base: int, start: int = 0):
    """The function starts (module offsets) in the .ARM.exidx table at or after data[start:], with
    data at module offset base: its first entry points at the module's first function, and the
    functions ascend."""
    for k in range(start, len(data) - 8, 4):
        word = int.from_bytes(data[k : k + 4], 'little')
        if word & 0x80000000 or not 0 <= prel31(word, base + k) < 0x10:
            continue
        starts = []
        while k + 8 <= len(data):
            address = prel31(int.from_bytes(data[k : k + 4], 'little'), base + k)
            if starts and address <= starts[-1]:
                break
            starts.append(address)
            k += 8
        if len(starts) > 1000:
            return starts
    return None


def exidx_remap(core, module, symbols: Path):
    """For a dump whose IL2CPP module has no symbols: a function from the dump's module offsets to
    those of the module in `symbols`, or None. The dump keeps the module's .ARM.exidx (the stack
    unwinder's table), which lists every function's start; aligning the two lists by function sizes
    (difflib) matches the functions that didn't change, and the PC's offset into its function carries
    over. On the 17:24 crash it put module_start and module_stop exactly on __module_start/stop."""
    import difflib
    import itertools

    segment = module.segments[0]
    dumped = [s for s in core.segments if segment.start <= s.vaddr < segment.start + segment.size]
    old = None
    for chunk in sorted(dumped, key=lambda s: -s.size):
        old = function_starts(bytes(chunk.data), chunk.vaddr - segment.start)
        if old:
            break
    data = symbols.read_bytes()
    at = data.find(b'LIBIL2CPP_PRX\0')
    if not old or at < 4:
        return None
    # The fself's segment 0: after the SCE header (its length at +0x10), at the first program
    # header's p_offset (the program headers' place at +0x48).
    phdrs = int.from_bytes(data[0x48:0x50], 'little')
    text = int.from_bytes(data[0x10:0x18], 'little') + int.from_bytes(data[phdrs + 4 : phdrs + 8], 'little')
    top, end = (int.from_bytes(data[at + o : at + o + 4], 'little') for o in (0x48, 0x4C))  # exidx_top, exidx_end
    new = function_starts(data[text + top : text + end], top)
    if not new:
        return None
    sizes = [[b - a for a, b in itertools.pairwise(starts)] for starts in (old, new)]
    matched = {}
    for block in difflib.SequenceMatcher(None, *sizes, autojunk=False).get_matching_blocks():
        for k in range(block.size):
            matched[block.a + k] = block.b + k
    print(f'  {len(matched)} of {len(old)} functions matched')

    def remap(offset):
        import bisect

        k = bisect.bisect_right(old, offset) - 1
        return new[matched[k]] + offset - old[k] if k in matched else None

    return remap


def symbolize(dump: Path, build: Path, thread_name: str | None = None):
    """Prints the crashed thread's PC, LR and the return addresses on its stack, resolved
    against the Unity player's symbols and the build's IL2CPP module. Stack scanning also
    finds stale return addresses; read the chain from the top."""
    core = parse_core(dump)
    player = editor_data(TARGET) / 'PlaybackEngines' / 'PSP2Player' / 'PSP2Player_Il2cpp.self'
    game, exact = game_symbols(build, module_fingerprints(core).get('LIBIL2CPP_PRX'), dump)
    symbols = {'LIBIL2CPP_PRX': game}
    modules = {m.name: m for m in core.modules}
    engine = next((name for name in modules if name.startswith('UnityPlayer')), None)
    if 'LIBIL2CPP_PRX' not in modules or engine is None:
        sys.exit(f'The dump has no game module or no Unity player loaded; its modules: {", ".join(modules)}')
    remap = None if exact else exidx_remap(core, modules['LIBIL2CPP_PRX'], game)
    symbols[engine] = player

    def locate(address):
        for name in symbols:
            segment = modules[name].segments[0]
            if segment.start <= address < segment.start + segment.size:
                offset = address - segment.start
                if name == 'LIBIL2CPP_PRX' and remap:
                    offset = remap(offset)
                    if offset is None:
                        return name, None
                return name, ENGINE_BASE + offset
        return None, None

    if thread_name:
        # A thread by name, e.g. the main thread a -crow-stall-dump crash interrupted.
        matching = [t for t in core.threads if thread_name.lower() in t.name.lower()]
        if not matching:
            sys.exit('No thread named like ' + thread_name + '; threads: ' + ', '.join(t.name for t in core.threads))
        thread = matching[0]
    else:
        thread = next((t for t in core.threads if t.stop_reason), None)
        if thread is None:
            sys.exit(
                'No thread of the dump stopped (a dump taken by hand?); pick one with --thread: '
                + ', '.join(t.name for t in core.threads)
            )
    print(
        f'thread {thread.name}, stop reason 0x{thread.stop_reason:x}, registers '
        + ' '.join(f'r{i}={v:x}' for i, v in enumerate(thread.regs.gpr[:13]))
    )
    frames = [('PC', *locate(thread.pc)), ('LR', *locate(thread.regs.gpr[14]))]
    sp = thread.regs.gpr[13]
    for k in range(STACK_WORDS):
        try:
            value = int.from_bytes(core.read_vaddr(sp + 4 * k, 4), 'little')
        except Exception:
            break
        name, link = locate(value)
        if name and value & 1:  # Thumb return addresses
            frames.append((f'sp+{4 * k:x}', name, link))
    names = {}
    for name in symbols:
        wanted = sorted({link & ~1 for _, n, link in frames if n == name and link is not None})
        for address, symbol in addr2line(symbols[name], wanted).items():
            names[name, address] = symbol
    for where, name, link in frames:
        if name:
            kind = 'engine' if name == engine else 'game~' if remap else 'game'
            symbol = '? (a function the symbols lack)' if link is None else names.get((name, link & ~1), '?')
            print(f'{where:>8} {kind:6} {symbol[:160]}')


def crash_dumps(connection):
    """The console's crash dumps, oldest first: (modified, name, size)."""
    return sorted(
        (facts.get('modify', ''), name, int(facts.get('size', 0)))
        for name, facts in connection.mlsd('ux0:/data/', facts=['modify', 'size'])
        if name.endswith('.psp2dmp')
    )


def crashes(pull: bool, build: Path, thread_name: str | None = None):
    with ftp() as connection:
        dumps = crash_dumps(connection)
        for modify, name, size in dumps[-10:]:
            print(f'{modify}  {size:>10}  {name}')
        if pull and dumps:
            out = OUT / dumps[-1][1]
            out.parent.mkdir(parents=True, exist_ok=True)
            # Into a part file first: a failed transfer used to leave an earlier copy empty.
            part = out.with_name(out.name + PART)
            with part.open('wb') as handle, transferring(dumps[-1][1]):
                connection.retrbinary(f'RETR ux0:/data/{dumps[-1][1]}', handle.write)
            part.replace(out)
            print(f'saved {out}')
    if pull and dumps:
        symbolize(out, build, thread_name)


def files(folder: str):
    """Prints every file under folder on the console: modified (UTC), size, path."""
    with ftp() as connection:

        def walk(directory):
            try:
                # The console's FTP server lists a folder only with the trailing slash.
                entries = sorted(connection.mlsd(directory.rstrip('/') + '/', facts=['type', 'modify', 'size']))
            except ftplib.error_perm as error:
                print(f'{directory}: {error}')
                return
            for name, facts in entries:
                if name in ('.', '..'):
                    continue
                path = f'{directory.rstrip("/")}/{name}'
                if facts.get('type') == 'dir':
                    walk(path)
                else:
                    print(f'{facts.get("modify", ""):14}  {int(facts.get("size", 0)):>9}  {path}')

        walk(folder)


def check_lock(name):
    """Stop a changing command while the vita-device lock is someone else's (lock.ours: any run this
    process is in, or CROW_HOLDER): a job whose lock take failed would otherwise quit another job's run
    and overwrite its flags."""
    lock.clear_stale('vita-device')
    text = lock.holder_of('vita-device')
    if text is None or lock.ours('vita-device'):
        return
    sys.exit(
        f'vita.py {name}: the console is held by "{text}" (vita-device lock). Run inside '
        '`lock.py run vita-device ...`, or set CROW_HOLDER=<your holder name> after taking it.'
    )


def quit_game():
    return command(f'{KEEP_AWAKE}; quit all; wait 2s')


def launch_game():
    return command(f'{KEEP_AWAKE}; launch {pipeline.VITA_TITLE_ID}')


def args_line(flags):
    """Flags as args.txt takes them: ProbeFiles splits the file at spaces and keeps "quoted words"
    whole, so a value with spaces ("Station Square", "A>B") is quoted. A word that starts with a dash
    stays as it is: `args "-crow-heartbeat 5"` is still two words."""
    return ' '.join(f'"{f}"' if ' ' in f and not f.startswith('-') and '"' not in f else f for f in flags)


def set_args(flags):
    """Writes the probe's flags for the next launch into args.txt (none: an empty file); the line."""
    line = args_line(flags)
    with ftp() as connection:
        makedirs(connection, REMOTE, set())
        upload(connection, ARGS, line.encode())
    return line


def show_args():
    try:
        return read(ARGS).decode(errors='replace').strip()
    except ftplib.error_perm:
        return ''  # no args.txt: none


def main():
    try:
        run_command()
    except Unreachable as error:
        sys.exit(str(error))


def run_command():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument(
        'command',
        choices=[
            'sync',
            'seed',
            'install',
            'launch',
            'quit',
            'run',
            'do',
            'shot',
            'reboot',
            'perf',
            'log',
            'args',
            'crashes',
            'files',
        ],
    )
    parser.add_argument('--build', type=Path, help='Vita build folder (default: builds/crow-<version>-vita)')
    parser.add_argument('--tail', type=int, default=0, metavar='N', help="log: only the log's last N lines")
    parser.add_argument('--pull', action='store_true', help='crashes: fetch and symbolize the newest dump')
    parser.add_argument('--thread', help='crashes --pull: symbolize this thread (part of its name), not the crashed')
    parser.add_argument(
        '--prev', type=int, nargs='?', const=1, default=0, metavar='N', help='log: the log of the run N before (1 or 2)'
    )
    parser.add_argument('--dump', type=Path, help='crashes: symbolize this local dump instead (no console needed)')
    parser.add_argument('--show', action='store_true', help='args: print the flags of the next launch, change nothing')
    parser.add_argument(
        '--build-wait',
        type=float,
        default=BUILD_WAIT_SECONDS,
        metavar='SECONDS',
        help=f'sync, run, install, seed: seconds to wait for a build holding vita-build (default {BUILD_WAIT_SECONDS})',
    )
    # The options may come anywhere; everything else after the command is its words, in order (the
    # probe flags of args, the chain of do, the file of shot, the folder of files). The probe's flags
    # start with one dash; a word with two is an option this script doesn't have (it used to go into
    # args.txt as a flag).
    args, args.rest = parser.parse_known_args()
    unknown = [word for word in args.rest if word.startswith('--')]
    if unknown:
        parser.error(f'unknown option {unknown[0]}')
    most = WORDS.get(args.command, 0)
    if most is not None and len(args.rest) > most:
        parser.error(
            f'{args.command} takes {f"at most {most} word" if most else "no words"} after it: {" ".join(args.rest)}'
        )
    if args.show and args.command != 'args':
        parser.error('--show goes with args')
    if args.show and args.rest:
        parser.error('args --show changes nothing: give it no flags')
    if args.command in CHANGING and not args.show:
        check_lock(args.command)
    build = args.build or build_folder('vita')

    if args.command == 'quit':
        print(quit_game())
    if args.command == 'run':
        # The build lock before the quit: before (21 Oct) run quit the game, then waited for a build
        # holding vita-build and could give up after --build-wait, leaving the console on its home screen.
        with reading(build, args.build_wait):
            print(quit_game())
            sync_files(build)
    if args.command == 'seed':
        with reading(build, args.build_wait):
            seed(build)
    if args.command == 'sync':
        sync(build, args.build_wait)
    if args.command in ('launch', 'run'):
        print(launch_game())
    if args.command == 'install':
        install(build, args.build_wait)
    if args.command == 'do':
        print(command(' '.join(args.rest), timeout=30))
    if args.command == 'shot':
        shot(Path(args.rest[0]) if args.rest else OUT / 'shot.png')
    if args.command == 'perf':
        require('perf')
        print(command('perf', timeout=5))
    if args.command == 'reboot':
        print(command('reboot', timeout=2))
    if args.command == 'log':
        # Scripts poll the log while a run goes on: each read keeps the display on, and wakes a game the
        # display's idle timeout paused (see wake).
        nudge()
        name = 'log.txt' if not args.prev else 'log.prev.txt' if args.prev == 1 else f'log.prev{args.prev}.txt'
        lines = read(f'{REMOTE}/{name}').decode(errors='replace').splitlines()
        print('\n'.join(lines[-args.tail :] if args.tail else lines))
    if args.command == 'args':
        line = show_args() if args.show else set_args(args.rest)
        print('args.txt: ' + (line or '(empty)'))
    if args.command == 'crashes' and args.dump:
        symbolize(args.dump, build, args.thread)
    elif args.command == 'crashes':
        crashes(args.pull, build, args.thread)
    if args.command == 'files':
        files(args.rest[0] if args.rest else SAVES)


if __name__ == '__main__':
    main()
