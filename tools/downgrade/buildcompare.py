"""Check that two builds of the same tree are the same: hash what a build makes, then compare two such
snapshots file by file (sha256).

    uv run tools/downgrade/buildcompare.py snapshot A                 # the PS Vita project, build and VPK
    uv run tools/downgrade/buildcompare.py snapshot A --what project  # after the project stage only
    uv run tools/downgrade/buildcompare.py compare A B
    uv run tools/downgrade/buildcompare.py snapshot W1 --platform windows [--backend il2cpp]

A snapshot is work/buildcompare/<label>/<part>.sha256, one line per file (sha256, size, path, and tab-separated
notes), sorted by path:
  project  the Unity project's Assets, ProjectSettings and Packages (work/crow-<version>[-vita]), as the
           project stage leaves them; take it before the build, since the editor writes in Assets as it
           builds (docs/build.md)
  build    the player folder (builds/crow-<version>-vita, or the Windows player's folder; --folder another)
  vpk      the PS Vita's VPK (--vpk another), and each file inside it with its zip header's date and
           attributes
compare prints the files that differ or that only one snapshot has, by part, and exits 1 if any do, apart
from what Unity's own tools write differently on every build: the PS Vita's IL2CPP module
(il2CppAssemblies.suprx) is compared with the path of IL2CPP's link cache folder and the module's
fingerprint left out (masked=), and the VPK by its files. docs/build.md, "Checking that builds are
reproducible", has the whole run.
"""

import argparse
import concurrent.futures
import hashlib
import re
import sys
import zipfile
from pathlib import Path

from common import TARGET, WORK, build_folder, log, project_folder

SNAPSHOTS = WORK / 'buildcompare'
PROJECT_PARTS = ('Assets', 'ProjectSettings', 'Packages')
THREADS = 8
CHUNK = 1 << 20

# The IL2CPP module of a PS Vita build embeds the path of the folder IL2CPP linked it in, named after a
# hash that changes on every build (Library/il2cpp_cache/linkresult_<32 hex digits>), and the linker's
# fingerprint of the module (sce_module_info's NID, at +0x34 from it; vita.py's fingerprint) changes with
# it. Nothing else in it differed between two builds of one tree.
LINK_FOLDER = re.compile(rb'linkresult_[0-9A-F]{32}')
MODULE_NAME = b'LIBIL2CPP_PRX\0'


def masked_digest(data: bytes):
    """sha256 of an IL2CPP module without its link folder's name and its fingerprint; None if it has neither."""
    data = bytearray(data)
    found = False
    for match in LINK_FOLDER.finditer(bytes(data)):
        data[match.start() : match.end()] = b'0' * (match.end() - match.start())
        found = True
    at = data.find(MODULE_NAME)
    if at >= 4:
        data[at + 0x30 : at + 0x34] = bytes(4)
        found = True
    return hashlib.sha256(data).hexdigest() if found else None


def file_digest(path: Path):
    digest = hashlib.sha256()
    with path.open('rb') as handle:
        while chunk := handle.read(CHUNK):
            digest.update(chunk)
    return digest.hexdigest()


def notes_of(name: str, read):
    """The tab-separated notes of a file: masked=<digest> for an IL2CPP module."""
    if name.endswith('.suprx') and 'il2cpp' in name.lower():
        masked = masked_digest(read())
        return f'\tmasked={masked}' if masked else ''
    return ''


def folder_lines(folder: Path, prefix=''):
    """The lines of every file under folder, sorted by path (posix, relative, with prefix)."""
    files = sorted((prefix + p.relative_to(folder).as_posix(), p) for p in folder.rglob('*') if p.is_file())

    def line(item):
        name, path = item
        return f'{file_digest(path)} {path.stat().st_size} {name}' + notes_of(name, path.read_bytes)

    with concurrent.futures.ThreadPoolExecutor(THREADS) as pool:
        return list(pool.map(line, files))


def vpk_lines(vpk: Path):
    """The VPK's own line, then each member's (the hash of its bytes) with its zip header's fields that can
    carry a build's time or the file system's attributes."""
    lines = [f'{file_digest(vpk)} {vpk.stat().st_size} {vpk.name}\tvpk']
    with zipfile.ZipFile(vpk) as archive:
        for info in sorted(archive.infolist(), key=lambda i: i.filename):
            data = archive.read(info)
            header = f'date={"-".join(map(str, info.date_time))} attr={info.external_attr:x} type={info.compress_type}'
            lines.append(
                f'{hashlib.sha256(data).hexdigest()} {info.file_size} {info.filename}\t{header}'
                + notes_of(info.filename, lambda data=data: data)
            )
    return lines


def snapshot(label: str, platform: str, backend: str, what: list, folder: Path | None, vpk: Path | None):
    out = SNAPSHOTS / label
    out.mkdir(parents=True, exist_ok=True)
    for part in what:
        if part == 'project':
            project = project_folder(TARGET, platform)
            lines = [line for name in PROJECT_PARTS for line in folder_lines(project / name, name + '/')]
        elif part == 'build':
            lines = folder_lines(folder or build_folder(platform, backend))
        elif platform == 'vita':
            lines = vpk_lines(vpk or build_folder('vita').with_name(build_folder('vita').name + '.vpk'))
        else:
            continue
        (out / f'{part}.sha256').write_text('\n'.join(lines) + '\n', encoding='utf-8')
        log(f'{label} {part}: {len(lines)} files -> {out / (part + ".sha256")}')


def read(path: Path):
    """{path: (digest, size, {note: value})} of a snapshot part."""
    entries = {}
    for line in path.read_text(encoding='utf-8').splitlines():
        head, *notes = line.split('\t')
        digest, size, name = head.split(' ', 2)
        entries[name] = (digest, int(size), dict(n.partition('=')[::2] if '=' in n else (n, '') for n in notes))
    return entries


def why_known(x, y):
    """Why two entries that differ are as expected (Unity's own output), or None."""
    (_, _, nx), (_, _, ny) = x, y
    if 'masked' in nx and nx.get('masked') == ny.get('masked'):
        return 'IL2CPP link folder and module fingerprint'
    if 'vpk' in nx:
        return 'the VPK (its files are compared below)'
    return None


def compare(a: str, b: str, limit: int):
    failed = False
    for part in ('project', 'build', 'vpk'):
        left, right = SNAPSHOTS / a / f'{part}.sha256', SNAPSHOTS / b / f'{part}.sha256'
        if not left.exists() or not right.exists():
            continue
        x, y = read(left), read(right)
        differ = sorted(n for n in x.keys() & y.keys() if x[n] != y[n])
        only = [(a, n) for n in sorted(x.keys() - y.keys())] + [(b, n) for n in sorted(y.keys() - x.keys())]
        expected = [(n, why_known(x[n], y[n])) for n in differ if why_known(x[n], y[n])]
        unexpected = [n for n in differ if not why_known(x[n], y[n])]
        same = len(x.keys() & y.keys()) - len(differ)
        print(
            f'{part}: {same} of {len(x.keys() | y.keys())} files the same, {len(unexpected)} differ, '
            f"{len(expected)} differ as Unity's tools do, {len(only)} in one only"
        )
        for name in unexpected[:limit]:
            (dx, sx, hx), (dy, sy, hy) = x[name], y[name]
            what = 'size ' + (f'{sx} -> {sy}' if sx != sy else f'{sx}') + (', bytes' if dx != dy else '')
            if hx != hy:
                changed = [k for k in sorted(hx.keys() | hy.keys()) if hx.get(k) != hy.get(k)]
                what += ', notes ' + ' '.join(f'{k}={hx.get(k)}->{hy.get(k)}' for k in changed)
            print(f'  differs  {name}  ({what})')
        if len(unexpected) > limit:
            print(f'  ... and {len(unexpected) - limit} more')
        for name, why in expected[:limit]:
            print(f'  known    {name}  ({why})')
        for which, name in only[:limit]:
            print(f'  only in {which}  {name}')
        failed |= bool(unexpected or only)
    return 1 if failed else 0


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    commands = parser.add_subparsers(dest='command', required=True)
    take = commands.add_parser('snapshot', help='hash a build into work/buildcompare/<label>')
    take.add_argument('label')
    take.add_argument('--platform', choices=['vita', 'windows'], default='vita')
    take.add_argument('--backend', choices=['mono', 'il2cpp'], default='mono')
    take.add_argument('--what', default='project,build,vpk', help='comma-separated: project, build, vpk')
    take.add_argument('--folder', type=Path, help="build: this player folder instead of the build's")
    take.add_argument('--vpk', type=Path, help="vpk: this VPK instead of the build's")
    check = commands.add_parser('compare', help='compare two snapshots')
    check.add_argument('a')
    check.add_argument('b')
    check.add_argument('--limit', type=int, default=40, help='files listed per part and kind')
    args = parser.parse_args()
    if args.command == 'snapshot':
        what = [w.strip() for w in args.what.split(',') if w.strip()]
        if bad := [w for w in what if w not in ('project', 'build', 'vpk')]:
            parser.error(f'unknown parts: {", ".join(bad)}')
        snapshot(args.label, args.platform, args.backend, what, args.folder, args.vpk)
    else:
        sys.exit(compare(args.a, args.b, args.limit))


if __name__ == '__main__':
    main()
