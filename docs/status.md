# Status

What works on the PS Vita, how fast it runs, what the build changes for it, and what is still open.
[status-log.md](status-log.md) is the dated record behind this page, with every measurement, oldest first.
Each change adds one entry at the end of status-log.md, and this page changes only when the current state does.
Prose here is one sentence per line, so a diff shows only the sentences that changed.

## What works

The pipeline builds the PS Vita VPK and the Windows Mono and IL2CPP players from the Steam or the GOG release ([build.md](build.md)).
The game has been played through to the end on the console.
Every one of the 30 areas with the most data ran in chains of 29-38 loads with no exception and no crash.
Saves go to `ux0:data/CrowCountry`, and a save loads from the title, after Quit to Title and after a relaunch.
Numbers and dates use the invariant culture, whatever the console's system language is.
The pad drives the game through CrowCompat's pad reader, with PlayStation button prompts and the console version's menus.
Audio keeps up with real time. In fights the DSP clock runs at 0.99-1.01 of the wall clock.
The Windows players boot, load areas and draw as the original does, and the test tools run them hidden and muted ([vita.md](vita.md#fight-benchmark)).

## Frame time on the console

The game is capped at 30 fps, which is 33.4 ms a frame.
The table gives the latest measurement of each place.

| Where | Frame | Measured |
| --- | --- | --- |
| Roadside, Mansion, Driving Intro, standing and walking | 33.4 ms (30 fps) | 15-20 Oct |
| Crypt, Corridor S, Corridor D | 33.5 ms | 24 Oct |
| Corridor B, the Save room | 33.4 ms | 25-26 Oct |
| Pool before the boss, looking into the pool | 33.4 ms (27.2 with the cap off) | 1 Nov |
| Pool, the final boss fight | 33.4 ms (29.3-29.9 with the cap off) | 1 Nov |
| Fairy Forest fight | 27-33 ms with the cap off | 1 Nov |
| Haunted Hilltop fight | 41 ms in its first 10 s, then 25-33, cap off | 1 Nov |
| Station Square fight (the benchmark) | 44 ms in its first 10 s, then 24-35, cap off | 1 Nov |

The CPU limits the two slow fights. Their frame time falls as enemies die, and the GPU clock makes no difference there.
The GPU limits Pool. At the stock GPU clock of 166 MHz it runs at 29 fps before the boss and 25-26 fps in the fight.

Free main memory during an area load is 33 MB or more everywhere except three areas: Haunted Hilltop 12-54 MB (once 8), Ocean Kingdom 11-36 and Arcade 14-31.
Each recovers to 37 MB or more once loaded.

## What the build changes for the PS Vita

Each item is a build switch ([build.md](build.md#build-switches)) or a CrowCompat flag's PS Vita default ([vita.md](vita.md#probe-flags)), and either can turn it back.

Clocks, threads and memory:

- CrowClock, a small native library inside the game's package (`Media/Plugins/CrowClock.suprx`), sets the CPU to 444 MHz and the GPU to 222 MHz. It only raises the CPU clock, so a higher PSVshell setting stays. It is not a system plugin and nothing outside the game sees it.
- Unity's two graphics job workers run at the render thread's priority on CPU 1.
- The shader patcher gets 1 MB of USSE memory, twice Unity's fixed 512 KB.
- The GC heap is capped to leave Unity 16 MB. Once 2 MB have been allocated, a collection runs half a second into a pause, so fewer collections happen in play.
- An area load goes through an empty scene and frees the old area's assets first, unless the new area fits beside them with 48 MB to spare.

Rendering:

- Frames are capped at 30 fps (vSyncCount 2).
- The camera renders at 550x351, and the post effect runs at half size in three passes written for the SGX543.
- Every light draws in one pass through CrowVita/Lit: up to 4 key lights per pixel, picked by what they add around Mara, one fill light, and the rest per vertex. Pool allows at most 2 key lights.
- Every opaque lit material draws one depth layer back, so the decals and overlays laid a few millimetres over it don't fight it. The black backing faces that sit just behind such a wall move further back, so they stay hidden (`CROW_BACKING`).
- Shadows are hard, Low resolution, 12 m, and only Mara casts them.
- Particle noise is Unity's 1D noise instead of the game's 3D noise.

Meshes and scenes:

- The build merges static geometry, rebuilds the enemies (one atlas, flattened pivots), merges Mara's parts and bakes MeshCombineStudio's combining.
- The small wobbling parts of the bosses' bodies are held still and merged like an enemy's.
- Every mesh's identical vertices become one, and triangles are put in vertex cache order. Textures used only by materials get mipmaps.
- In Pool, the rock placed 1,056 times becomes a convex hull without the faces hidden inside its neighbours, and the meshes with only seamless textures get one vertex per position.
- See-through layers stacked at one place draw once with the stack's alpha (`CROW_STACK_FOLD`).
- An enemy's growing death pool uses a box trigger instead of a mesh collider, so a corpse no longer drops the frame rate.

Game logic:

- Physics steps once per frame, at most twice a frame.
- In fights, Blob trails are off, held enemies are frozen, and decoration and enemy cosmetics update less often.
- PlayMaker's FSM updates that would do nothing are skipped, and its small actions are compiled into the FSM code.
- The gameplay canvas's and the camera rig's switched-off parts are moved out of their hierarchies.
- AudioSources idle for 2 s are disabled, and reverb is off. With reverb on, FMOD's mixer fell behind and crackled in nearly every area that has it.
- The game runs in its console mode with the Switch's platform animations.

## Open issues

- Haunted Hilltop's loads are the tightest on memory: 8-14 MB free at their lowest in long chains, against a 12 MB target.
- Unity's allocator leaves about 2.7 MB more holes after each Ocean Kingdom to Haunted Hilltop round trip.
- The first seconds of the Station Square and Haunted Hilltop fights, with every enemy awake, run at 23-25 fps.
- The shader patcher had 223 KB of its 1 MB used after 15 loads, about 9 KB more per new area. Whether 1 MB lasts a whole playthrough is not known.
- The GOG build has not been tested on the console.
- Once, after a console standby with the game running, the player's log stopped being written. Not investigated.
- Dig Site's dark spots have not been checked with the depth layers.
- The save clock's re-read after a wake has not been tried on the console.
- The game was once launched with no flags while the console sat idle. The cause was not found.

## Next steps

1. More free memory for the loads into Haunted Hilltop.
2. The CPU cost of the big fights' first seconds.
3. Load times. An area load takes 2-23 s, and in a big area most of it is Unity's loading thread reading the level's objects. About half of Station Square's level file is PlayMaker FSMs, so a shorter load needs smaller level data.
4. A console test of the GOG build.
