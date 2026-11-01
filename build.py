"""Build Crow Country for the PS Vita and/or Windows from your copy of the game: set up, then run the
pipeline's stages in order.

    uv run build.py                      # the VPK: builds/crow-2018.2.19f1-vita.vpk
    uv run build.py --windows            # a Windows (Mono) player: builds/crow-2018.2.19f1/CrowCountry.exe
    uv run build.py --vita --il2cpp      # the VPK and a Windows IL2CPP player
    uv run build.py --dry-run            # list the stages, run nothing

Runs tools/setup.py for the targets (it installs what is missing and skips what is there), then
tools/downgrade/pipeline.py: rip (skipped while the export matches the game, AssetRipper and its
settings), project, build and, for the PS Vita, package. project and the editor's import only redo
what changed, so a rerun is a rebuild. The stages run under the vita-build or pc-build lock
(tools/downgrade/lock.py), waiting for another job's pipeline to finish first. docs/build.md describes
the requirements, the times and the other options.
"""

import argparse
import os
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent
sys.path.insert(0, str(ROOT / 'tools' / 'downgrade'))
import progress  # noqa: E402
from common import TARGET, WORK, default_game, registry_env  # noqa: E402

PIPELINE = ROOT / 'tools' / 'downgrade' / 'pipeline.py'
LOCK = ROOT / 'tools' / 'downgrade' / 'lock.py'
STAGES = ('rip', 'project', 'build', 'package')
RETRY = 'Fix what it says above and run uv run build.py again: it picks up where it stopped.'
# lock.py run's exit code when the lock stayed held.
LOCK_NOT_TAKEN = 75


def environment():
    """This process's variables, with what setup has just set for the user winning over an older value
    this shell started with."""
    env = dict(os.environ)
    for name in ('SCE_PSP2_SDK_DIR', 'SCE_ROOT_DIR', 'CROW_GAME'):
        value = registry_env(name, 'user')
        if value:
            env[name] = value
    return env


def run(failed, command, env=None):
    """Run a command; stop with failed (what to tell the user) when it fails."""
    code = subprocess.run([str(c) for c in command], env=env or environment(), check=False).returncode
    if code:
        progress.fail(f'\nbuild.py: {failed} (exit code {code}).')


def plan(args):
    """[(stage, pipeline.py arguments, what it makes)] in order, from --from on."""
    game = ['--game', args.game] if args.game else []
    steps = []
    if not rip_current(args):
        steps.append(('rip', ['rip', *game], f'work/rip-{TARGET}'))
    if args.vita:
        steps += [
            ('project', ['project', '--platform', 'vita', *game], f'work/crow-{TARGET}-vita'),
            ('build', ['build', '--platform', 'vita', *game], f'builds/crow-{TARGET}-vita'),
            ('package', ['package', *game], f'builds/crow-{TARGET}-vita.vpk'),
        ]
    if args.windows or args.il2cpp:
        steps.append(('project', ['project', '--platform', 'windows', *game], f'work/crow-{TARGET}'))
        if args.windows:
            steps.append(('build', ['build', '--platform', 'windows', *game], f'builds/crow-{TARGET}/CrowCountry.exe'))
        if args.il2cpp:
            steps.append(
                (
                    'build',
                    ['build', '--platform', 'windows', '--backend', 'il2cpp', *game],
                    f'builds/crow-{TARGET}-il2cpp/CrowCountry.exe',
                )
            )
    first = STAGES.index(args.start)
    return [step for step in steps if STAGES.index(step[0]) >= first]


def rip_current(args):
    """Whether work/rip-<version> is the export of this game with this AssetRipper and its settings
    (pipeline.rip_is_current); the rip stage itself always exports again."""
    if args.start != 'rip':
        return True
    import pipeline

    if not (WORK / f'rip-{TARGET}' / pipeline.RIP_STAMP).exists():  # no export yet
        return False
    game = Path(args.game) if args.game else default_game()
    return pipeline.rip_is_current(game, WORK / f'rip-{TARGET}', TARGET)


def build(args):
    """The pipeline's stages, in this process, under a build lock."""
    steps = plan(args)
    if not steps:
        progress.done('Nothing to run from that stage for these targets.')
        return
    env = environment()
    for stage, arguments, output in steps:
        progress.console.print(f'\nbuild.py: pipeline.py {" ".join(arguments)}  ->  {output}')
        run(f'pipeline.py {stage} failed. {RETRY}', [sys.executable, PIPELINE, *arguments], env)
    made = [ROOT / output for _, _, output in steps if output.startswith('builds/')]
    progress.done('\nDone:\n' + '\n'.join(f'  {path}' for path in made))


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument('--vita', action='store_true', help='the PS Vita VPK (the default without --windows/--il2cpp)')
    parser.add_argument('--windows', action='store_true', help='a Windows player with Mono')
    parser.add_argument('--il2cpp', action='store_true', help='a Windows player with IL2CPP (as the Vita runs code)')
    parser.add_argument('--game', metavar='FOLDER', help="Crow Country, if not in Steam's or GOG's default folder")
    parser.add_argument('--vita-sdk', metavar='PATH', help='your own PS Vita SDK, an archive or a folder')
    parser.add_argument('--skip-setup', action='store_true', help='go straight to the pipeline')
    parser.add_argument(
        '--no-unigate', action='store_true', help="keep the editor's license checks (your editor has a license)"
    )
    parser.add_argument(
        '--from',
        dest='start',
        choices=STAGES,
        default='rip',
        help='start at this stage (the earlier ones are done); rip is skipped anyway while the export is current',
    )
    parser.add_argument(
        '--wait',
        type=float,
        default=6 * 3600,
        metavar='SECONDS',
        help="how long to wait for another job's pipeline (the build lock); default 6 hours",
    )
    parser.add_argument('--dry-run', action='store_true', help='list the setup command and the stages, run nothing')
    parser.add_argument('--locked', action='store_true', help=argparse.SUPPRESS)  # build.py under lock.py run
    args = parser.parse_args()
    if not (args.vita or args.windows or args.il2cpp):
        args.vita = True
    if args.locked or os.environ.get('CROW_LOCK'):
        build(args)
        return

    flags = [f'--{t}' for t, on in (('vita', args.vita), ('il2cpp', args.il2cpp)) if on]
    setup = [sys.executable, ROOT / 'tools' / 'setup.py', *flags, '--then-build']
    setup += ['--game', args.game] if args.game else []
    setup += ['--vita-sdk', args.vita_sdk] if args.vita_sdk else []
    setup += ['--no-unigate'] if args.no_unigate else []
    name = 'vita-build' if args.vita else 'pc-build'
    if args.dry_run:
        if not args.skip_setup:
            print('setup: ' + ' '.join(map(str, ['uv run tools/setup.py', *setup[2:]])))
        print(f'then, under the {name} lock:')
        for stage, arguments, output in plan(args):
            print(f'  {stage:8} uv run tools/downgrade/pipeline.py {" ".join(arguments)}  ->  {output}')
        return
    if not args.skip_setup:
        run(f'setup stopped. {RETRY}', setup)
    # The stages under one build lock, so no other job's pipeline runs between them. lock.py run waits
    # for the lock, ends the editor with build.py if it is stopped, and releases the lock.
    holder = 'build.py: ' + ', '.join(t for t in ('vita', 'windows', 'il2cpp') if getattr(args, t))
    rerun = [sys.executable, ROOT / 'build.py', '--locked', *sys.argv[1:]]
    command = [sys.executable, LOCK, 'run', name, holder, '--wait', str(args.wait), '--', *rerun]
    code = subprocess.run([str(c) for c in command], env=environment(), check=False).returncode
    if code == LOCK_NOT_TAKEN:
        progress.fail(f'\nbuild.py: another job held the {name} lock for {args.wait:.0f} s; try again later.')
    sys.exit(code)


if __name__ == '__main__':
    try:
        main()
    except KeyboardInterrupt:
        progress.fail('\nStopped. Run uv run build.py again to continue.')
