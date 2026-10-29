# Status

Where the port stands now: what works, the PS Vita's defaults, the open issues and the next steps.
[status-log.md](status-log.md) is the dated record behind it, with every measurement, oldest first.
New entries go at the end of status-log.md, one per change; the coordinator appends them and updates this file where a change moves the current state.
This file has one sentence per line, so a change shows as the lines it touches.

## What works

The pipeline builds the PS Vita VPK and the Windows Mono and IL2CPP players from the Steam game ([build.md](build.md)).
On the console the game boots to the title, starts a new game and plays through the areas tried so far: every one of the 30 areas with the most data ran in chains of 29-38 loads without an exception or a crash (status-log: Every big area in long chains, 23 Oct).
Saves are written to `ux0:data/CrowCountry`, and a save loads from the title, after Quit to Title and after a relaunch (Black screen after Quit to Title, 21 Oct; Long session on the combined build, 22 Oct).
Numbers and dates are written in the invariant culture whatever the console's system language is (25 Oct).
Underground's elevator rides free Mara as the game does, and the areas' pre-combined floors and walls are drawn on every visit, not only the first (28 Oct).
The pad drives the game through CrowCompat's pad reader, with PlayStation prompts and the console's menus (no Quit on the title, no Screen entry).
Audio keeps up with real time: the DSP clock ran at 0.99-1.01 of the wall clock in the 22 Oct soak's fights, with short dips after some loads.
The Windows players boot, load areas and draw as the original does; the test tools run them hidden and muted ([vita.md](vita.md#fight-benchmark)).

Frame time on the console (30 fps is 33.4 ms; the latest measurement of each):

| Where | Frame | Measured |
| --- | --- | --- |
| Roadside, Mansion, Driving Intro, standing and walking | 33.4 ms (30 fps) | 15 Oct, Roadside again 20 Oct |
| Crypt, Corridor S, Corridor D | 33.5 ms | 24 Oct |
| Corridor B at the user's view, the Save room | 33.4 ms | 25-26 Oct |
| Ocean Kingdom fight | 34.2 ms | 22 Oct |
| Haunted Hilltop fight | 35.7 ms | 22 Oct |
| Fairy Forest fight | 37.2 ms | 20 Oct |
| Station Square fight (the benchmark) | 38.0-38.9 ms (about 26 fps) | 22-26 Oct |

Free main memory during an area load is 33 MB or more everywhere but three areas: Haunted Hilltop 12-54 MB (once 8), Ocean Kingdom 11-36, Arcade 14-31 (23 Oct).
Each recovers to 37 MB or more once loaded.

## The PS Vita's defaults

Each default is a build setting or a CrowCompat flag's PS Vita value; [vita.md](vita.md#probe-flags) has the flag that turns it back.
The shader patcher has 1 MB of USSE memory, twice Unity's fixed 512 KB (`pipeline.patcher_usse`, 512 KB more main memory).
The CPU runs at 444 MHz and the GPU at 222 MHz (PSVshell's 500 MHz CPU is left alone).
Frames are capped at 30 fps (vSyncCount 2): an even 30 where a frame fits in 33 ms.
Graphics jobs are on (`CROW_GFX_JOBS=on` writes them into `boot.config`), with Unity's two job workers at the render thread's priority on CPU 1.
Every light draws in one pass through CrowVita/Lit: up to 4 key lights per pixel chosen by what they give the ground around Mara, one fill light, the rest per vertex.
Shadows are hard, Low resolution, 12 m, and only the player casts them.
The camera films at 550x351 and the camera effect runs at half size, in three passes written for the SGX543.
Every opaque lit material draws a depth layer back, so the decals, writing and overlays laid a few millimetres over it don't fight it (`asset_rules.vita_lit_bake`).
Particle noise is Unity's low-quality 1D noise.
Physics steps once per 30 fps frame, at most two steps a frame.
The build merges static geometry, rebuilds the enemies (atlas, flattened pivots), merges Mara's parts and bakes MeshCombineStudio's combining.
The fights' cuts are on: Blob trails off, held enemies frozen, decoration and enemy cosmetics at reduced rates.
PlayMaker's FSM updates that would do nothing are skipped, and its small methods are compiled into the FSM code.
The gameplay canvas's and the camera rig's switched-off parts are parked out of their hierarchies.
Area loads go through an empty scene with the old area's assets freed first.
The GC heap is capped to leave Unity 16 MB, its table of pinned handles is grown at the start, and the allocator keeps its free blocks sorted.
A collection runs half a second into a pause once 2 MB have been allocated, so fewer come in play (Boehm otherwise collects after every 5.5-6.6 MB, a 0.75-1 s stop).
MeshCombineStudio's mesh caches are released once every combiner has combined.
AudioSources idle for 2 s are disabled, at most 3 reverb filters are on at once, alike reverbs share one, and a reverb comes on only while FMOD's mixer has room for it.
The game runs in its console mode (Control Mode Console 1) with the Switch's platform animations.

## Open issues

- Haunted Hilltop's loads are the tightest: 8-14 MB free at their lowest in the long chains (Ocean Kingdom -> Haunted Hilltop 10-11, Arcade -> Haunted Hilltop once 8), against a 12 MB target (22-23 Oct).
- Unity's allocator leaves about 2.7 MB more holes after each Ocean Kingdom <-> Haunted Hilltop round trip (22 Oct).
- The Station Square fight is the slowest place measured, at about 26 fps (38-39 ms a frame).
- The player's log stopped being written after a console standby while the game ran on (26 Oct, not investigated).
- The text of Fairy Forest's tree fairy note (Hint 3) changes as the camera moves; it is the same with the depth layers on and off, so likely aliasing: 128x128 without mipmaps, about 35 pixels wide in the camera's picture (29 Oct, not fixed).
- Dig Site's dark spots have not been checked with the depth layers (22 Oct).
- The PS Vita's save clock re-reads its file after a wake; that fix has not been tried at the console (22 Oct).
- The render-thread crashes of 17 and 26 Oct were the shader patcher's 512 KB of USSE memory running full, not graphics jobs; the eboot gives it 1 MB since 27 Oct.
  Whether 1 MB lasts a whole playthrough is not known: 223 KB after 15 loads, about 9 KB per new area (`usseKB` in `-crow-memory-watch`; 27 Oct).
- Something launched the game with no flags while the console sat idle (27 Oct, 04:03 and 06:40); the cause was not found.
- The main menu once dropped to about 10 fps while idle; it was never reproduced (15 Oct).
- vitacompanion once failed to allocate memory for a screenshot and the console hung 7 loads later (23 Oct): a tool problem, not the game's.

## Next steps

1. More memory margin for the loads into Haunted Hilltop.
2. The Station Square fight's frame time.
3. Load times, last measured as a whole on 15 Oct (boot to title about 21 s); reading PlayMaker's data was then the biggest cost.
4. Play further: story events, fights across the game, and saves made in play and loaded later.
