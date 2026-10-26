"""Scene sweep: start a Windows player in each build scene in turn (-crow-scene), take a screenshot and
sort what the log says, to find broken scenes before the console does.

    uv run tools/downgrade/sweep.py run <tag> [--build dir] [--scenes "A,B"] [--from name] [--shot 6] [--quit-after 30]
                                        [-- <more player flags...>]
    uv run tools/downgrade/sweep.py report <tag>

`run` runs a copy of the player without Steam's files (work/sweep/player, hard links) through
run_player.py (its own desktop, windowed, muted), one scene at a time,
with -crow-scene <name> -crow-shot <shot> <png> -crow-quit-after <s> -crow-heartbeat 2. The first build
scene (Logos) runs without -crow-scene. Output goes to artifacts/sweep/<tag>/: <scene>.log, <scene>.png
and, from `report` (which `run` ends with), summary.md and summary.json: whether the scene loaded, its
heartbeat numbers, and the log's messages grouped by kind (exception, error, missing script, shader,
Rewired, PlayMaker, AOT) with counts and the first stack lines.
"""

import argparse
import json
import re
import sys
import time
from pathlib import Path

import run_player
from common import ROOT, TARGET, WORK, build_folder, number, project_folder, split_at_dashes
from playerlog import heartbeats

OUT = ROOT / 'artifacts' / 'sweep'
COPY = WORK / 'sweep' / 'player'
BUILD_SETTINGS = project_folder(TARGET, 'windows') / 'ProjectSettings' / 'EditorBuildSettings.asset'

KINDS = [
    ('aot', re.compile(r'ExecutionEngineException|ahead of time|AOT code', re.I)),
    ('missing-method', re.compile(r'Missing(Method|Field|Member)Exception|TypeLoadException', re.I)),
    ('exception', re.compile(r'^\w[\w.`]*(Exception|Error)\b[^\n]*:|Exception:', re.M)),
    ('missing-script', re.compile(r'referenced script.*missing|script.*could not be loaded|missing script', re.I)),
    ('shader', re.compile(r'shader|material doesn\'t have', re.I)),
    ('rewired', re.compile(r'Rewired', re.I)),
    ('playmaker', re.compile(r'PlayMaker|\bFsm\w*\b|Action\b.*not found', re.I)),
    ('error', re.compile(r'\berror\b|\bfailed\b|could not|can\'t|cannot|not found|invalid', re.I)),
]
# A stack frame: Mono's "  at ...", Unity's "Type:Method(args)" (and " (at file:line)"), "[ line n]".
STACK = re.compile(
    r'^(\s+at |\[[^\]]*line \d+\]|\(wrapper |[\w.`<>$|\[\],+]+:[\w.`<>$|\[\],]+ ?\(.*\)( \(at .*\))?\s*$)'
)
# Lines of a normal start and run that the kinds above would count: Unity 2018.2's and the game's own
# (a baseline: add to it when a new harmless line shows up in every scene). CrowCompat's CROW_* lines
# are left out wherever they are (analyse).
NOISE = re.compile(
    r'^(HEARTBEAT|Map: |Change fullscreen|Steam |Setting language|Language |Lang Value|Load slot|'
    r'Create new API|Setting breakpad|SteamInternal|Unloading \d+|UnloadTime|Total: |Setting up \d+ worker|'
    r'  Thread ->|Mono path|Mono config|Initialize engine|GfxDevice|Direct3D|    \w+:|Begin MonoManager|'
    r'- Completed reload|<RI>|Property \(lut\) exceeds|'
    # The players run without Steam (steam_api64.dll disabled); the game carries on without it.
    r'Fallback handler could not load library.*steam_api|Unable to load DLL .steam_api)'
)
TRAILER = re.compile(r'\n \n\(Filename:[^\n]*\)\n')


def scenes():
    """The build scenes, in order, as SceneManager names them."""
    paths = re.findall(r'path: (\S.*\.unity)', BUILD_SETTINGS.read_text(encoding='utf-8'))
    return [Path(p).stem for p in paths]


def messages(text):
    """The log's messages as (text lines, stack lines). A release player ends a message with a blank line
    and '(Filename: ...)'; IL2CPP players and engine messages add stack frames after the text, and
    some engine lines come with no trailer, so each run of text lines followed by frames is one."""
    found = []
    for block in TRAILER.split(text):
        lines, stack = [], []
        for line in block.splitlines():
            if not line.strip():
                continue
            if STACK.match(line):
                stack.append(line)
                continue
            if stack:
                found.append((lines, stack))
                lines, stack = [], []
            lines.append(line)
        if lines or stack:
            found.append((lines, stack))
    return found


def classify(message):
    for kind, pattern in KINDS:
        if pattern.search(message):
            return kind
    return None


def analyse(scene, log: Path, png: Path, first_scene):
    text = log.read_text(encoding='utf-8', errors='replace') if log.exists() else ''
    loaded = re.findall(r'CROW_DIAG scene loaded (.+?) \(\w+\) at ([\d,.]+) s', text)
    loaded_names = [name for name, _ in loaded]
    load_time = next((number(t) for name, t in loaded if name == scene), None)
    beats = list(heartbeats(text))
    last = beats[-1] if beats else {}
    groups = {}
    for lines, stack in messages(text):
        # CrowCompat's lines (they report, and say "not found" or "failed" in normal cases), then noise
        # lines at the start (several Unity lines can come without a Filename trailer).
        lines = [line for line in lines if not line.startswith('CROW_')]
        while lines and NOISE.match(lines[0]):
            lines.pop(0)
        if not lines:
            continue
        kind = classify('\n'.join(lines))
        if kind is None:
            continue
        key = re.sub(r'\d+', '#', lines[0])[:200]
        group = groups.setdefault(key, {'kind': kind, 'count': 0, 'first': '\n'.join([*lines[:4], *stack[:6]])})
        group['count'] += 1
    return {
        'scene': scene,
        'loaded': scene in loaded_names or (scene == first_scene and 'CROW_DIAG started' in text),
        'scenes_loaded': loaded_names,
        'load_time': load_time,
        'quit': 'CROW_DIAG quitting' in text,
        'screenshot': str(png) if png.exists() else None,
        'fps': last.get('fps'),
        'visible': int(last['visible']) if isinstance(last.get('visible'), float) else None,
        'last_beat_scene': last.get('scene'),
        'messages': sorted(groups.values(), key=lambda g: (g['kind'], -g['count'])),
        'crashed': bool(re.search(r'Crash!!!|Received signal|access violation', text, re.I)),
    }


def report(tag):
    folder = OUT / tag
    names = scenes()
    done = [s for s in names if (folder / f'{s}.log').exists()]
    results = [analyse(s, folder / f'{s}.log', folder / f'{s}.png', names[0]) for s in done]
    (folder / 'summary.json').write_text(json.dumps(results, indent=1), encoding='utf-8')
    lines = [f'# Sweep {tag}', '', '| scene | loaded | load s | fps | visible | messages |', '|' + '---|' * 6]
    for r in results:
        kinds = {}
        for m in r['messages']:
            kinds[m['kind']] = kinds.get(m['kind'], 0) + m['count']
        summary = ', '.join(f'{k} {n}' for k, n in sorted(kinds.items())) or '-'
        state = 'yes' if r['loaded'] else 'NO'
        if r['crashed']:
            state += ' (crash)'
        elif not r['quit']:
            state += ' (no quit)'
        cells = [r['scene'], state, r['load_time'] or '', r['fps'] or '', r['visible'] or '', summary]
        lines.append('| ' + ' | '.join(str(c) for c in cells) + ' |')
    lines += ['', '## Messages by text', '']
    by_key = {}
    for r in results:
        for m in r['messages']:
            key = m['first'].splitlines()[0][:200]
            entry = by_key.setdefault(key, {'kind': m['kind'], 'first': m['first'], 'scenes': []})
            entry['scenes'].append(f'{r["scene"]} x{m["count"]}')
    for key, entry in sorted(by_key.items(), key=lambda kv: (kv[1]['kind'], -len(kv[1]['scenes']))):
        where = f'Scenes ({len(entry["scenes"])}): ' + ', '.join(entry['scenes'])
        lines += [f'### [{entry["kind"]}] {key}', '', where, '']
        lines += ['```', entry['first'], '```', '']
    (folder / 'summary.md').write_text('\n'.join(lines), encoding='utf-8')
    print(f'{folder / "summary.md"}: {len(results)} scenes, {sum(not r["loaded"] for r in results)} not loaded')


def run(tag, build: Path, only, start, shot, quit_after, timeout, extra=()):
    folder = OUT / tag
    folder.mkdir(parents=True, exist_ok=True)
    exe = run_player.player_copy(build, COPY)
    names = scenes()
    todo = [s for s in names if not only or s in only]
    if start:
        todo = todo[todo.index(start) :]
    for scene in todo:
        log, png = folder / f'{scene}.log', folder / f'{scene}.png'
        png.unlink(missing_ok=True)
        flags = [] if scene == names[0] else ['-crow-scene', scene]
        flags += ['-crow-shot', str(shot), str(png.resolve()), '-crow-quit-after', str(quit_after)]
        flags += ['-crow-heartbeat', '2', *extra]
        began = time.time()
        code = run_player.run(exe, log, flags, timeout)
        result = analyse(scene, log, png, names[0])
        print(
            f'{scene:24} exit={code} {time.time() - began:5.1f}s loaded={result["loaded"]} '
            f'messages={sum(m["count"] for m in result["messages"])}',
            flush=True,
        )
    report(tag)


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = parser.add_subparsers(dest='command', required=True)
    r = sub.add_parser('run')
    r.add_argument('tag')
    r.add_argument('--build', type=Path, default=build_folder('windows'))
    r.add_argument('--scenes', help='comma-separated scene names (default: all build scenes)')
    r.add_argument('--from', dest='start', help='start at this scene')
    r.add_argument('--shot', type=float, default=6)
    r.add_argument('--quit-after', type=float, default=30)
    r.add_argument('--timeout', type=float, default=120)
    p = sub.add_parser('report')
    p.add_argument('tag')
    own, extra = split_at_dashes(sys.argv[1:])
    args = parser.parse_args(own)
    if args.command == 'report':
        report(args.tag)
        return
    only = {s.strip() for s in args.scenes.split(',')} if args.scenes else None
    run(args.tag, args.build, only, args.start, args.shot, args.quit_after, args.timeout, extra)


if __name__ == '__main__':
    sys.exit(main())
