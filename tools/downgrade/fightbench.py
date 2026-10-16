"""The fight benchmark: one command that runs CrowCompat's scripted fight (compat/Fight.cs, -crow-fight)
on the console or a Windows player and prints where the frame goes.

    uv run tools/downgrade/fightbench.py vita [--modes real,stress] [--runs 2] [--tag T] [--no-sync]
                                         [--seconds 60] [--area NAME] [--wait S] [-- <more -crow-* flags>]
    uv run tools/downgrade/fightbench.py pc [--modes ...] [--runs 1] [--build DIR] [--vitalike [CAP]]
                                       [-- <more flags>]
    uv run tools/downgrade/fightbench.py report <tag or log>...

Each run launches the game with -crow-fight <mode>: the game loads the fight's area, puts Mara on a
fixed spot with a gun, ammo and the laser sight and makes her unhurtable, wakes the area's enemies
(stress mode adds copies of them), aims at the nearest and fires on a fixed schedule, logs a
CROW_FIGHT_WINDOW line per measured window and a CROW_FIGHT_SUMMARY line, and quits.

vita: takes the vita-device lock (waiting up to --wait seconds; inside `lock.py run` it uses the
caller's), syncs the build (unless --no-sync), and for each mode and run quits the game, writes
args.txt, launches, keeps the console awake, waits for the summary (or a failure, or --timeout),
pulls the log and samples vitacompanion's perf during the fight. However it ends, it quits the game
and clears args.txt. pc: runs a Windows player through run_player.py (its own desktop, windowed,
muted); --vitalike runs it at <CAP> percent of the wall time on three cores (vitalike.py's
calibration, made for the IL2CPP player), and the PC player copy gets the current CrowCompat.dll
(Mono builds) so the benchmark works without a new build.

Logs and the table go to artifacts/fightbench/<tag>/ (<mode>-<run>.log, perf-<mode>-<run>.txt,
summary.txt). `report` prints the table again from saved logs.
"""

import argparse
import os
import re
import shutil
import statistics
import sys
import time
from pathlib import Path

from common import ROOT, TARGET, WORK

OUT = ROOT / 'artifacts' / 'fightbench'
VITA_BUILD = ROOT / 'builds' / f'crow-{TARGET}-vita'
PC_BUILD = ROOT / 'builds' / f'crow-{TARGET}'
PC_COPY = WORK / 'fightbench' / 'player'
LINE = re.compile(r'(CROW_FIGHT_(?:WINDOW|SUMMARY|FAIL|SETUP|START))\s+(.*)$')
POLL_SECONDS = 10
# Columns of the table: (key in the log line, heading, format).
COLUMNS = [
    ('ms', 'frame', '{:.1f}'),
    ('p50', 'p50', '{:.1f}'),
    ('p95', 'p95', '{:.1f}'),
    ('p99', 'p99', '{:.1f}'),
    ('worst', 'worst', '{:.0f}'),
    ('fps', 'fps', '{:.1f}'),
    ('cpu', 'main', '{:.1f}'),
    ('scripts', 'scripts', '{:.1f}'),
    ('physics', 'phys', '{:.1f}'),
    ('steps', 'steps', '{:.1f}'),
    ('anim', 'anim', '{:.1f}'),
    ('skin', 'skin', '{:.1f}'),
    ('particles', 'part', '{:.1f}'),
    ('audio', 'audio', '{:.1f}'),
    ('render', 'render', '{:.1f}'),
    ('ui', 'ui', '{:.1f}'),
    ('wait', 'wait', '{:.1f}'),
    ('gc', 'gc', '{:.0f}'),
    ('gcms', 'gcms', '{:.0f}'),
    ('allocKBs', 'KB/s', '{:.0f}'),
    ('alive', 'alive', '{:.1f}'),
    ('shots', 'shots', '{:.0f}'),
    ('kills', 'kills', '{:.0f}'),
    ('freeMB', 'freeMB', '{:.0f}'),
]


def fields(text):
    """key=value pairs of a log line (values with a comma decimal read as numbers too)."""
    out = {}
    for pair in text.split():
        if '=' not in pair:
            continue
        key, value = pair.split('=', 1)
        try:
            out[key] = float(value.replace(',', '.'))
        except ValueError:
            out[key] = value
    return out


def parse(log: str):
    """(setup fields, window lines, summary fields or None, failure text or None)."""
    setup, windows, summary, failure = {}, [], None, None
    for line in log.splitlines():
        found = LINE.search(line)
        if not found:
            continue
        kind, rest = found[1], found[2].strip()
        if kind == 'CROW_FIGHT_SETUP':
            setup.update(fields(rest))
        elif kind == 'CROW_FIGHT_WINDOW':
            windows.append(fields(rest))
        elif kind == 'CROW_FIGHT_SUMMARY':
            summary = fields(rest)
        elif kind == 'CROW_FIGHT_FAIL':
            failure = rest
    return setup, windows, summary, failure


def table(rows):
    """rows: (label, fields). A fixed-width table of COLUMNS."""
    width = max([len(label) for label, _ in rows] + [5])
    lines = [f'{"run":<{width}} ' + ' '.join(f'{heading:>7}' for _, heading, _ in COLUMNS)]
    for label, row in rows:
        cells = []
        for key, _, form in COLUMNS:
            value = row.get(key)
            cells.append(f'{form.format(value) if isinstance(value, float) else "-":>7}')
        lines.append(f'{label:<{width}} ' + ' '.join(cells))
    return '\n'.join(lines)


def mean_row(summaries):
    """The mean of several runs' summaries, per column."""
    row = {}
    for key, _, _ in COLUMNS:
        values = [s[key] for s in summaries if isinstance(s.get(key), float)]
        if values:
            row[key] = max(values) if key == 'worst' else statistics.fmean(values)
    return row


def report(logs):
    """The table for a set of run logs: one row per run, a mean per mode, the failures."""
    rows, by_mode, notes = [], {}, []
    for log in logs:
        setup, windows, summary, failure = parse(log.read_text(encoding='utf-8', errors='replace'))
        label = log.stem
        if failure:
            notes.append(f'{label}: FAILED {failure}')
        if summary is None:
            notes.append(f'{label}: no summary ({len(windows)} windows)')
            continue
        rows.append((label, summary))
        by_mode.setdefault(str(summary.get('mode', '?')), []).append(summary)
        if setup:
            pairs = (f'{k}={v:g}' if isinstance(v, float) else f'{k}={v}' for k, v in setup.items())
            notes.append(f'{label}: ' + ' '.join(pairs))
    for mode, summaries in by_mode.items():
        if len(summaries) > 1:
            rows.append((f'{mode} mean', mean_row(summaries)))
    text = table(rows) if rows else 'no runs with a summary'
    legend = (
        'ms per frame. frame: mean frame time; p50/p95/p99/worst: frame time percentiles; main: the main\n'
        "thread's own run time per frame (PS Vita, CrowClock); scripts..wait: player loop steps (scripts:\n"
        'Update/LateUpdate/FixedUpdate scripts incl. PlayMaker; phys: physics; steps: physics steps a frame;\n'
        'anim: Animator/Director; '
        'skin: skinned meshes; part: particle systems; render: culling and draw submission, renderers;\n'
        'ui: canvases; wait: vsync, present and frame start); gc/gcms: collections and their pause in the\n'
        'measured windows; KB/s: allocation rate; alive: enemies alive (mean); freeMB: lowest free main memory.'
    )
    return '\n'.join([text, '', *notes, '', legend])


# ---------------------------------------------------------------------------------------------- vita


def flags_for(mode, args, run):
    """The run's flags. -crow-fight-run names the run: the log's args line then tells this run's log from
    the last one's, which stays on the card until the game starts writing the new one."""
    flags = ['-crow-fight', mode, '-crow-fight-run', f'{args.tag}/{mode}-{run}']
    flags += ['-crow-loop-profile', '100000', '-crow-loop-cpu', '-crow-gc-log']
    if args.area:
        flags += ['-crow-fight-area', args.area]
    if args.seconds:
        flags += ['-crow-fight-seconds', str(args.seconds)]
    return flags + list(args.extra)


def quoted(flags):
    """Flags as args.txt takes them (ProbeFiles splits on spaces; quotes keep "Station Square" whole)."""
    return ' '.join(f'"{f}"' if ' ' in f else f for f in flags)


def vita_runs(args):
    import vita

    tag_dir = OUT / args.tag
    tag_dir.mkdir(parents=True, exist_ok=True)
    logs = []
    vita.keep_awake()
    try:
        if not args.no_sync:
            vita.sync(args.build)
        logs.extend(vita_run(vita, mode, run, args, tag_dir) for mode in args.modes for run in range(1, args.runs + 1))
    finally:
        # Whatever happened: the game closed and the flags cleared, so the next launch is the game's own.
        for step in ('quit', 'args'):
            try:
                if step == 'quit':
                    vita.command(f'{vita.KEEP_AWAKE}; quit all; wait 2s')
                else:
                    vita.write(f'{vita.REMOTE}/args.txt', b'')
            except Exception as error:
                print(f'cleanup {step} failed: {error}')
        print('quit the game, cleared args.txt')
    return [log for log in logs if log is not None]


def vita_run(vita, mode, run, args, tag_dir):
    flags = flags_for(mode, args, run)
    line = quoted(flags)
    print(f'--- {mode} run {run}: {line}')
    vita.command(f'{vita.KEEP_AWAKE}; quit all; wait 2s')
    vita.write(f'{vita.REMOTE}/args.txt', line.encode())
    vita.command(f'{vita.KEEP_AWAKE}; launch {vita.pipeline.VITA_TITLE_ID}')
    started = time.time()
    out = tag_dir / f'{mode}-{run}.log'
    perf = tag_dir / f'perf-{mode}-{run}.txt'
    perf.write_text('')
    text, fighting, samples = '', False, 0
    while time.time() - started < args.timeout:
        time.sleep(POLL_SECONDS)
        try:
            text = vita.read(f'{vita.REMOTE}/log.txt').decode(errors='replace')
        except Exception as error:
            print(f'  log read failed: {error}')
            continue
        head = [line.strip() for line in text.split('\n', 3)[:3]]
        if f'args: {" ".join(flags)}' not in head:
            continue  # the last run's log: the game hasn't started writing this one
        _, windows, summary, failure = parse(text)
        if 'CROW_FIGHT_START' in text and not fighting:
            fighting = True
            print(f'  fight started after {time.time() - started:.0f} s')
        if fighting and summary is None and samples < 3 and len(windows) >= samples + 1:
            samples += 1
            try:
                with perf.open('a') as file:
                    file.write(f'--- window {len(windows)}\n{vita.command("perf", timeout=5)}\n')
            except Exception as error:
                print(f'  perf failed: {error}')
        if summary is not None or failure:
            break
    else:
        print(f'  timed out after {args.timeout} s')
    out.write_text(text, encoding='utf-8')
    _, windows, summary, failure = parse(text)
    state = 'FAILED ' + failure if failure else 'done' if summary is not None else 'no summary'
    print(f'  {state}: {len(windows)} windows, {time.time() - started:.0f} s; log {out}')
    if summary is not None:
        print(table([(f'{mode}-{run}', summary)]))
    return out


# ------------------------------------------------------------------------------------------------ pc


def pc_player(build: Path):
    """A copy of the Windows build (hard links, so it costs nothing) whose CrowCompat.dll is the one
    compiled from today's sources (Mono builds), so the benchmark runs without a new build. Steam's
    steam_api library and steam_appid.txt are left out (the build's Steam wrappers are stubs anyway)."""
    exe = build / 'CrowCountry.exe'
    if not exe.exists():
        sys.exit(f'no Windows player in {build}')
    if PC_COPY.exists():
        shutil.rmtree(PC_COPY)
    for source in build.rglob('*'):
        if source.name.lower().startswith(('steam_api', 'steam_appid')):
            continue  # Steam's library and app id: with them a player once signed in and unlocked achievements
        target = PC_COPY / source.relative_to(build)
        if source.is_dir():
            target.mkdir(parents=True, exist_ok=True)
            continue
        target.parent.mkdir(parents=True, exist_ok=True)
        try:
            os.link(source, target)
        except OSError:
            shutil.copy2(source, target)
    managed = PC_COPY / 'CrowCountry_Data' / 'Managed' / 'CrowCompat.dll'
    if managed.exists():  # a Mono player: today's CrowCompat in place of the build's
        import contextlib
        import io

        import pipeline

        compat = WORK / 'fightbench' / 'compat' / 'CrowCompat.dll'
        compat.parent.mkdir(parents=True, exist_ok=True)
        with contextlib.redirect_stdout(io.StringIO()):  # csc's command line
            pipeline.engine_library(TARGET, compat, pipeline.COMPAT_SOURCES, pipeline.COMPAT_MODULES)
        managed.unlink()
        shutil.copy2(compat, managed)
        print(f'player copy {PC_COPY} with {compat}')
    else:
        print(f'player copy {PC_COPY} (IL2CPP: the build has its CrowCompat compiled in)')
    return PC_COPY / 'CrowCountry.exe'


def pc_runs(args):
    import run_player

    tag_dir = OUT / args.tag
    tag_dir.mkdir(parents=True, exist_ok=True)
    exe = pc_player(args.build)
    logs = []
    for mode in args.modes:
        for run in range(1, args.runs + 1):
            flags = [*flags_for(mode, args, run), '-crow-quit-after', str(args.timeout)]
            out = tag_dir / f'{mode}-{run}.log'
            print(f'--- {mode} run {run}: {" ".join(flags)}')
            slow = vitalike_slowdown(args.vitalike) if args.vitalike else None
            started = time.time()
            code = run_player.run(exe, out, flags, timeout=args.timeout + 60, on_start=slow)
            _, windows, summary, failure = parse(out.read_text(encoding='utf-8', errors='replace'))
            state = 'FAILED ' + failure if failure else 'done' if summary is not None else 'no summary'
            print(f'  {state} (exit {code}): {len(windows)} windows, {time.time() - started:.0f} s; log {out}')
            logs.append(out)
    return logs


def vitalike_slowdown(cap):
    """run_player's on_start: vitalike.py's duty cycle (cap percent of the wall time, three cores
    after core 0, busy guards in between) on the player process."""
    import ctypes

    import vitalike

    def start(pid):
        handle = ctypes.windll.kernel32.OpenProcess(0x1F0FFF, False, pid)  # PROCESS_ALL_ACCESS
        if not handle:
            print(f'vitalike: cannot open the player ({pid})')
            return None
        handle = vitalike.wt.HANDLE(handle)
        game_mask, controller_mask = vitalike.pick_cpus(3)
        vitalike.kernel32.SetProcessAffinityMask(handle, ctypes.c_size_t(game_mask))
        vitalike.kernel32.SetPriorityClass(handle, 0x80)  # HIGH_PRIORITY_CLASS
        guards = vitalike.start_guards(game_mask)
        duty = vitalike.Duty(handle, cap / 100, vitalike.DEFAULT_PERIOD_MS / 1000, controller_mask, guards[1])
        duty.start()

        def stop():
            duty.stop.set()
            duty.join(2)
            vitalike.kernel32.TerminateJobObject(guards[0], 0)
            vitalike.kernel32.CloseHandle(guards[0])
            print(duty.report())

        return stop

    return start


# ---------------------------------------------------------------------------------------------- main


def main():
    split = sys.argv.index('--') if '--' in sys.argv else len(sys.argv)
    own, extra = sys.argv[1:split], sys.argv[split + 1 :]
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument('target', choices=['vita', 'pc', 'report'])
    parser.add_argument('paths', nargs='*', help='report: tags (artifacts/fightbench/<tag>) or log files')
    parser.add_argument('--modes', default='real,stress', help='comma-separated: real, stress')
    parser.add_argument('--runs', type=int, default=2)
    parser.add_argument('--tag', default=time.strftime('%m%d-%H%M'))
    parser.add_argument('--area', help='the fight scene (default: Fight.cs picks per mode)')
    parser.add_argument('--seconds', type=int, default=0, help='measured seconds per run (default: Fight.cs)')
    parser.add_argument('--timeout', type=int, default=420, help='seconds per run before giving up')
    parser.add_argument('--build', type=Path)
    parser.add_argument('--no-sync', action='store_true', help='vita: skip the sync')
    parser.add_argument('--wait', type=int, default=7200, help='vita: seconds to wait for the vita-device lock')
    parser.add_argument('--vitalike', type=float, nargs='?', const=5.5, help='pc: run at CAP percent (5.5)')
    args = parser.parse_args(own)
    args.extra = extra
    args.modes = [m for m in args.modes.split(',') if m]

    if args.target == 'report':
        logs = []
        for name in args.paths:
            path = Path(name)
            if not path.exists():
                path = OUT / name
            logs += sorted(path.glob('*-[0-9]*.log')) if path.is_dir() else [path]
        print(report(logs))
        return 0

    if args.target == 'vita':
        args.build = args.build or VITA_BUILD
        if os.environ.get('CROW_LOCK') != 'vita-device':
            import lock

            holder = f'fightbench: {",".join(args.modes)} x{args.runs}'
            print(f'waiting for the vita-device lock as "{holder}"')
            code = lock.run('vita-device', holder, args.wait, [sys.executable, __file__, *sys.argv[1:]])
            if code == lock.NOT_TAKEN:
                print('the vita-device lock stayed held')
            return code
        logs = vita_runs(args)
    else:
        args.build = args.build or PC_BUILD
        logs = pc_runs(args)

    text = report(logs)
    (OUT / args.tag / 'summary.txt').write_text(text + '\n', encoding='utf-8')
    print()
    print(text)
    return 0


if __name__ == '__main__':
    sys.exit(main())
