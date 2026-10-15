"""Who allocates: names and sums the stacks CrowCompat's allocation probe samples on a Windows IL2CPP
player (compat/Alloc.cs: -crow-alloc <s> -crow-alloc-stacks <KB>, written to alloc-stacks.txt beside
the player's executable, or -crow-alloc-file <path>).

    uv run tools/downgrade/allocreport.py <alloc-stacks.txt> [--build builds/crow-2018.2.19f1-il2cpp]
                                          [--top 40] [--depth 4]

Each line of the file is one distinct stack and class with its sample count (a sample per KB
allocated, so count * KB is the bytes it stands for). The report: KB a frame is left to the player's
ALLOC lines; here the share of the bytes per allocating method (the first frame outside the runtime's
allocator), per class, and per method with its callers (--depth frames).
"""

import argparse
import collections
import re
from pathlib import Path

import cpuprofile
from common import ROOT, TARGET

# Frames of the allocator itself and of the probe's callback, skipped to find the method that allocates.
RUNTIME = re.compile(
    r'^(il2cpp::|GC_|il2cpp_|Alloc_|ReversePInvokeWrapper|Profiler|String_|Array_|Object_|Box|SZArrayNew|'
    r'il2cpp_codegen|InterlockedAdd|Interlocked_|ntdll|KERNEL|kernel|KERNELBASE|RtlCapture|\?0x|'
    r'icalls::|vm::|utils::|NewObject|AllocateSpec|FastAllocateString|String__|ScriptingString|'
    r'Scripting::|scripting_|CreateScriptingArray|ScriptingArray)'
)


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument('stacks', type=Path)
    parser.add_argument('--build', type=Path, default=ROOT / 'builds' / f'crow-{TARGET}-il2cpp')
    parser.add_argument('--top', type=int, default=40)
    parser.add_argument('--depth', type=int, default=4)
    args = parser.parse_args()

    rows = []
    for line in args.stacks.read_text(encoding='utf-8').splitlines():
        count, kb, klass, frames = line.split('\t')
        rows.append((int(count) * int(kb), klass, frames.split(';') if frames else []))
    keys = {frame for _, _, frames in rows for frame in frames}
    names = cpuprofile.symbolize(sorted(keys), args.build.resolve())
    total = sum(kb for kb, _, _ in rows) or 1

    by_method = collections.Counter()
    by_class = collections.Counter()
    by_chain = collections.Counter()
    classes_of = collections.defaultdict(collections.Counter)
    for kb, klass, frames in rows:
        named = [names.get(f, f) for f in frames]
        start = next((i for i, n in enumerate(named) if not RUNTIME.match(n)), len(named))
        method = named[start] if start < len(named) else '(runtime)'
        by_method[method] += kb
        by_class[klass] += kb
        classes_of[method][klass] += kb
        by_chain[' <- '.join(named[start : start + args.depth])] += kb

    def table(title, counter, extra=None):
        print(f'\n== {title}')
        for key, kb in counter.most_common(args.top):
            more = ''
            if extra:
                more = '  [' + ', '.join(f'{c} {k}' for c, k in extra[key].most_common(3)) + ']'
            print(f'{100 * kb / total:6.2f}% {kb:9d} KB  {key}{more}')

    print(f'{args.stacks}: {total} KB sampled in {len(rows)} stacks')
    table('Allocating method (first frame outside the allocator)', by_method, classes_of)
    table('Class', by_class)
    table(f'Allocating method and its callers ({args.depth} frames)', by_chain)


if __name__ == '__main__':
    main()
