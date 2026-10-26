"""The fight benchmark: one command that runs CrowCompat's scripted fight (compat/Fight.cs, -crow-fight)
on the console or a Windows player and prints where the frame goes.

    uv run tools/downgrade/fightbench.py vita [--modes real,stress] [--runs 2] [--tag T] [--no-sync]
                                         [--seconds 60] [--area NAME] [--wait S] [--input CHAIN]
                                         [-- <more -crow-* flags>]
    uv run tools/downgrade/fightbench.py pc [--modes ...] [--runs 1] [--build DIR] [--vitalike [CAP]]
                                       [-- <more flags>]
    uv run tools/downgrade/fightbench.py report <tag or log>...

Each run launches the game with -crow-fight <mode>: the game loads the fight's area, puts Mara on a
fixed spot with a gun, ammo and the laser sight and makes her unhurtable, wakes the area's enemies
(stress mode adds copies of them), aims at the nearest and fires on a fixed schedule, logs a
CROW_FIGHT_WINDOW line per measured window and a CROW_FIGHT_SUMMARY line, and quits.

vita: takes the vita-device lock (waiting up to --wait seconds, by default not at all; inside
`lock.py run` it uses the caller's), syncs the build (unless --no-sync), and for each mode and run
quits the game, writes args.txt, launches, keeps the console awake, waits for the summary (or a
failure, or --timeout), pulls the log and samples vitacompanion's perf during the fight. A run stops
early when the game starts no log whose head names the run's flags (compat/ProbeFiles.cs's args
line) within 2 minutes, and prints the head it found. However it ends, it quits the game and clears
args.txt. pc: runs a Windows player through run_player.py (its own desktop, windowed, muted);
--vitalike runs it at <CAP> percent of the wall time on three cores (vitalike.py's calibration, made
for the IL2CPP player), and the PC player copy gets the current CrowCompat.dll (Mono builds) so the
benchmark works without a new build.

Logs and the table go to artifacts/fightbench/<tag>/ (<mode>-<run>.log, perf-<mode>-<run>.txt,
summary.txt). `report` prints the table again from saved logs.
"""

import argparse
import re
import shutil
import statistics
import sys
import time
from pathlib import Path

from common import ROOT, TARGET, WORK, build_folder, split_at_dashes
from playerlog import fields

OUT = ROOT / 'artifacts' / 'fightbench'
VITA_BUILD = build_folder('vita')
PC_BUILD = build_folder('windows')
PC_COPY = WORK / 'fightbench' / 'player'
LINE = re.compile(r'(CROW_FIGHT_(?:WINDOW|SUMMARY|FAIL|SETUP|START))\s+(.*)$')
POLL_SECONDS = 10
# How long after the launch the game has to start this run's log (it writes its first lines in seconds).
START_SECONDS = 120
HEAD_LINES = 5  # where the log's args line is looked for
# -crow-loop-profile's period: long enough that only the fight's per-window CROW_LOOP lines appear.
LOOP_PROFILE_WINDOWS_ONLY = '100000'
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
    ('rate', 'rate', '{:.2f}'),
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
    ('hits', 'hits', '{:.0f}'),
    ('shotkills', 'shotk', '{:.0f}'),
    ('freeMB', 'freeMB', '{:.0f}'),
]


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


def outcome(failure, summary):
    return 'FAILED ' + failure if failure else 'done' if summary is not None else 'no summary'


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
        'rate: game time per wall-clock second (under 1 when -crow-physics-cap clamps slow frames);\n'
        'anim: Animator/Director; '
        'skin: skinned meshes; part: particle systems; render: culling and draw submission, renderers;\n'
        'ui: canvases; wait: vsync, present and frame start); gc/gcms: collections and their pause in the\n'
        'measured windows; KB/s: allocation rate; alive: enemies alive (mean); hits: health drops by shots;\n'
        "shotk: kills by shots (not the fight's own); freeMB: lowest free main memory."
    )
    return '\n'.join([text, '', *notes, '', legend])


# ---------------------------------------------------------------------------------------------- vita


def flags_for(mode, args, run):
    """The run's flags. -crow-fight-run names the run: the log's args line then tells this run's log from
    the last one's, which stays on the card until the game starts writing the new one."""
    flags = ['-crow-fight', mode, '-crow-fight-run', f'{args.tag}/{mode}-{run}']
    flags += ['-crow-loop-profile', LOOP_PROFILE_WINDOWS_ONLY, '-crow-loop-cpu', '-crow-gc-log']
    if args.area:
        flags += ['-crow-fight-area', args.area]
    if args.seconds:
        flags += ['-crow-fight-seconds', str(args.seconds)]
    return flags + list(args.extra)


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
        for step, clean in (('quit', vita.quit_game), ('args', lambda: vita.set_args([]))):
            try:
                clean()
            except Exception as error:
                print(f'cleanup {step} failed: {error}')
        print('quit the game, cleared args.txt')
    return [log for log in logs if log is not None]


def vita_run(vita, mode, run, args, tag_dir):
    flags = flags_for(mode, args, run)
    print(f'--- {mode} run {run}: {vita.args_line(flags)}')
    vita.quit_game()
    vita.set_args(flags)
    vita.launch_game()
    started = time.time()
    out = tag_dir / f'{mode}-{run}.log'
    perf = tag_dir / f'perf-{mode}-{run}.txt'
    perf.write_text('')
    text, fighting, samples, sent = '', False, 0, False
    wanted, before, stray = f'args: {" ".join(flags)}', None, None
    while time.time() - started < args.timeout:
        time.sleep(POLL_SECONDS)
        try:
            text = vita.read(f'{vita.REMOTE}/log.txt').decode(errors='replace')
        except Exception as error:
            print(f'  log read failed: {error}')
            continue
        head = [line.strip() for line in text.split('\n', HEAD_LINES)[:HEAD_LINES]]
        if wanted not in head:
            # The last run's log stays until the game starts this one's. A new log whose head is
            # written (two lines) but names other flags means the args line changed (ProbeFiles.cs).
            if before is None:
                before = text
            elif text != before and text.count('\n') >= 2:
                stray = f'the new log has no "{wanted}" line in its head: {" | ".join(head)}'
            elif time.time() - started > START_SECONDS:
                stray = f'the game started no new log in {START_SECONDS} s (did it launch?)'
            if stray:
                break
            continue
        _, windows, summary, failure = parse(text)
        if 'CROW_FIGHT_START' in text and not fighting:
            fighting = True
            print(f'  fight started after {time.time() - started:.0f} s')
        if fighting and args.input and not sent and len(windows) >= 1:
            sent = True  # in the second window, the first having measured the start
            print(f'  input: {vita.command(f"{vita.KEEP_AWAKE}; {args.input}", timeout=60)}')
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
    if stray and text == before:  # the last run's log: nothing of this run's to keep
        print(f'  FAILED: {stray}')
        return None
    out.write_text(text, encoding='utf-8')
    if stray:
        print(f'  FAILED: {stray}; log {out}')
        return out
    _, windows, summary, failure = parse(text)
    print(f'  {outcome(failure, summary)}: {len(windows)} windows, {time.time() - started:.0f} s; log {out}')
    if summary is not None:
        print(table([(f'{mode}-{run}', summary)]))
    return out


# ------------------------------------------------------------------------------------------------ pc


def pc_player(build: Path):
    """A copy of the Windows build (hard links, so it costs nothing) whose CrowCompat.dll is the one
    compiled from today's sources (Mono builds), so the benchmark runs without a new build. Steam's
    steam_api library and steam_appid.txt are left out (the build's Steam wrappers are stubs anyway)."""
    import run_player

    exe = run_player.player_copy(build, PC_COPY)  # made again every time: its CrowCompat.dll changes
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
    return exe


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
            print(
                f'  {outcome(failure, summary)} (exit {code}): {len(windows)} windows, '
                f'{time.time() - started:.0f} s; log {out}'
            )
            logs.append(out)
    return logs


def vitalike_slowdown(cap):
    """run_player's on_start: vitalike.py's slow-down (cap percent of the wall time, three cores
    after core 0, busy guards in between) on the player process."""
    import vitalike

    def start(pid):
        slowed = vitalike.slow_down(pid, cap)
        if slowed is None:
            return None
        duty, stop_duty = slowed

        def stop():
            stop_duty()
            print(duty.report())

        return stop

    return start


# ---------------------------------------------------------------------------------------------- main


def main():
    own, extra = split_at_dashes(sys.argv[1:])
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument('target', choices=['vita', 'pc', 'report'])
    parser.add_argument('paths', nargs='*', help='report: tags (artifacts/fightbench/<tag>) or log files')
    parser.add_argument(
        '--modes',
        default='real,stress',
        help='comma-separated -crow-fight modes: real, stress, idle, stand, walk, physics, discover (Fight.cs)',
    )
    parser.add_argument('--runs', type=int, default=2)
    parser.add_argument('--tag', default=time.strftime('%m%d-%H%M'))
    parser.add_argument('--area', help='the fight scene (default: Fight.cs picks per mode)')
    parser.add_argument('--seconds', type=int, default=0, help='measured seconds per run (default: Fight.cs)')
    parser.add_argument('--timeout', type=int, default=420, help='seconds per run before giving up')
    parser.add_argument('--build', type=Path)
    parser.add_argument('--no-sync', action='store_true', help='vita: skip the sync')
    parser.add_argument(
        '--wait',
        type=int,
        default=0,
        help='vita: seconds to wait for the vita-device lock (default 0: stop at once while another job has it)',
    )
    parser.add_argument('--vitalike', type=float, nargs='?', const=5.5, help='pc: run at CAP percent (5.5)')
    parser.add_argument('--input', help='vita: a vitacompanion chain sent once in the second window (walk mode)')
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
        import lock

        # Any run this process is in, or CROW_HOLDER naming the holder (lock.ours).
        if not lock.ours('vita-device'):
            holder = f'fightbench: {",".join(args.modes)} x{args.runs}'
            print(f'waiting for the vita-device lock as "{holder}"')
            code = lock.run('vita-device', holder, args.wait, [sys.executable, __file__, *sys.argv[1:]])
            if code == lock.NOT_TAKEN:
                print('the vita-device lock stayed held (--wait SECONDS waits for it)')
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
