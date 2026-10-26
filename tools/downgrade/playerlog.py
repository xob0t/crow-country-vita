"""The player log lines CrowCompat writes as key=value pairs: compat/Diag.cs's HEARTBEAT, the fight's
CROW_FIGHT_* (compat/Fight.cs). vitalike.py, cpuprofile.py, sweep.py and fightbench.py read them here."""

import re

from common import number

HEARTBEAT = re.compile(r'^HEARTBEAT (.*)$', re.M)


def fields(text):
    """The key=value pairs of a log line; values that are numbers (a decimal comma too) as floats."""
    out = {}
    for pair in text.split():
        if '=' not in pair:
            continue
        key, value = pair.split('=', 1)
        try:
            out[key] = number(value)
        except ValueError:
            out[key] = value
    return out


def heartbeats(text):
    """The log's HEARTBEAT lines, oldest first, as fields: t, scene (spaces as _), fps, ms, worst, gc,
    heapMB, renderers, visible, lights and a count per light type. A scene name stays a string."""
    for found in HEARTBEAT.finditer(text):
        beat = fields(found[1])
        if 'scene' in beat:
            beat['scene'] = re.search(r'(?:^| )scene=(\S*)', found[1])[1]
        yield beat
