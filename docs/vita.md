# PS Vita

The app is Unity's own PS Vita player, built by the Unity 2018.2.19f1 editor with its PS Vita module and Sony's PS Vita SDK 3.570 ([build.md](build.md) sets them up). The build is a PC Hosted player in `builds/crow-2018.2.19f1-vita`; `pipeline.py package` turns it into `builds/crow-2018.2.19f1-vita.vpk` with the player as `eboot.bin`, its trial flag cleared, and LiveArea art made from the game's Steam store images (`livearea.py`). The title ID is `CROW00001`.

## Installing

You need a PS Vita on HENkaku or Ensō with [VitaShell](https://github.com/TheOfficialFloW/VitaShell). [vitacompanion](https://github.com/devnoname120/vitacompanion) is optional: it adds an FTP server that works while the game runs and a command server that `vita.py` uses to start and quit the game, take screenshots and press buttons.

1. Copy the VPK to the console: in VitaShell press Select to start its FTP server, and upload the VPK to `ux0:data/` with any FTP client (port 1337), or copy it over USB.
2. In VitaShell, open the VPK and install it.

The app asks for the +109 MB memory expansion (`DowngradeBuild.cs`), which the system reads from `param.sfo` at installation. A build that changes `param.sfo` needs a new installation; anything else can be updated in place with `vita.py sync`.

On the console the app keeps its files in:

| Folder | What |
| --- | --- |
| `ux0:app/CROW00001` | The installed app. |
| `ux0:data/CrowCountry` | The game's saves and settings (`compat/Saves.cs`). The app's own folder is read-only. |
| `ux0:data/crow` | The probe's flags (`args.txt`) and the player's log (`log.txt`, at most 8 MB; the two runs before in `log.prev.txt` and `log.prev2.txt`). |

## vita.py

`tools/downgrade/vita.py` drives the installed app at `CROW_VITA_HOST` (or `DRIFTER_VITA_HOST`); `uv run tools/setup.py --vita-host <address>` sets it. `sync`, `log`, `args`, `files` and `crashes` need only an FTP server on port 1337, VitaShell's or vitacompanion's; `seed` needs no console. The others need vitacompanion's command server on port 1338: `do`, `install` and `shot` need vitacompanion 1.20 or later, `perf` 1.33. `install`, `perf` and `shot` use commands the released vitacompanion lacks; without them they stop with a message.

```powershell
uv run tools/downgrade/vita.py sync              # upload what changed since the last sync (the first uploads everything)
uv run tools/downgrade/vita.py run               # quit, sync, launch
uv run tools/downgrade/vita.py log --tail 40     # the player's log
uv run tools/downgrade/vita.py args -crow-heartbeat 5   # the probe's flags for the next launch; no flags clears them
uv run tools/downgrade/vita.py crashes --pull    # the newest crash dump, symbolized
```

| Command | Effect |
| --- | --- |
| `sync [--build dir]` | Upload the files that changed since the last sync into `ux0:app/CROW00001`. |
| `seed --build dir` | Record a build as installed (from its VPK, say) without uploading, so the next sync sends only differences. |
| `install [--build dir]` | Package the build as a VPK and stream it to the installer; also applies `param.sfo` changes. |
| `launch`, `quit`, `run` | Start the game, quit all apps, or quit, sync and launch. |
| `do "<chain>"` | Run a vitacompanion command chain, for example `do "press cross; wait 100ms; release cross"`. |
| `shot [out.png]` | A system screenshot of what the screen shows. |
| `perf` | Clocks, CPU load per core, frame rate and battery. |
| `log [--tail N] [--prev [N]]` | Print the player's log; `--prev` the run before's (`log.prev.txt`), `--prev 2` the one before that. |
| `args [flags...]` | Write the probe's flags to `ux0:data/crow/args.txt`. |
| `files [folder]` | List the files under a console folder, by default the saves. |
| `crashes [--pull] [--thread NAME] [--dump FILE]` | List the crash dumps in `ux0:data`; `--pull` fetches the newest into `artifacts/vita/` and prints a thread's symbolized call chain; `--dump` reads a dump already on the PC. |
| `reboot` | Reboot the console. |

`--build` takes another build folder, such as a test build's `builds/crow-2018.2.19f1-vita-scenes`.

When several jobs share the console, take the `vita-device` lock first (`tools/downgrade/lock.py`); a person at the console holds it as `user: <purpose>`.

## Probe flags

CrowCompat, the compatibility library the pipeline adds to the game, has a measurement probe that is off unless the player gets a `-crow-*` flag. A Windows player takes the flags on its command line; on the PS Vita, which passes no command line, the player reads them from `ux0:data/crow/args.txt`, which `vita.py args` writes. They apply from the next launch. `-crow-help` logs the probe's own list.

| Flags | Effect | Source |
| --- | --- | --- |
| `-crow-scene <name>`, `-crow-scene-then <name> <s>` | Load an area once the title and the player's scene have loaded; s seconds (default 30) after it, load a second one as the game's area changes do (with the player in play); `"A>B>C"` loads them in turn, s seconds apart. | `compat/Diag.cs` |
| `-crow-heartbeat <s>` | Every s seconds, log frame times, GC and memory, visible renderers and lights. | `compat/Diag.cs` |
| `-crow-quit-after <s>`, `-crow-shot <s> <path>`, `-crow-no-vsync`, `-crow-mute`, `-crow-input` | Timed runs, screenshots, unsynchronized frame times, no sound, input logging. | `compat/Diag.cs` |
| `-crow-find <regex> <s>` | s seconds after each scene load, every renderer whose hierarchy path matches, active or not: enabled, visible, bounds, mesh and vertex count, materials and shaders (for what looks missing in play). | `compat/Diag.cs` |
| `-crow-teleport <x,y,z> <s>` | s seconds (default 3) after each scene load, the player (its "character RIGIDBODY") moved to that world position, to look at a place without walking there (Entrance's tutorial paper: `0.3,-0.2,-1.6`). | `compat/Diag.cs` |
| `-crow-fight real\|stress\|idle\|discover`, `-crow-fight-area <scene>`, `-crow-fight-spot x,y,z,yaw[,camyaw]`, `-crow-fight-extra <n>`, `-crow-fight-pool <n>`, `-crow-fight-seconds <s>`, `-crow-fight-window <s>`, `-crow-fight-settle <s>`, `-crow-fight-engage <s>`, `-crow-fight-fire <s>`, `-crow-fight-kill <s>`, `-crow-fight-log`, `-crow-fight-run <name>` | The fight benchmark (see [Fight benchmark](#fight-benchmark)): loads the area (default Station Square) with its enemies switched on through the game's globals, puts Mara on a fixed spot (unhurtable, the handgun with the laser sight, a full box, the camera locked), fires at the nearest awake enemy every 0.7 s through the combat FSM and kills one through its own damage FSM every 8 s (6 in stress mode); stress adds 12 copies of the area's enemies around her and replaces the dead from 12 spares; idle is the same setup with no enemies or shots; discover logs the area's enemies and quits. After 12 s of settling and 6 of fighting it logs a CROW_FIGHT_WINDOW line every 10 s for 60 s (frame time mean and percentiles, the main thread's own time, the player loop by subsystem, collections and their pause, the allocation rate, enemies alive and awake, shots, kills, the lowest free main memory), then CROW_FIGHT_SUMMARY, and quits. `-crow-fight-log` logs the first 40 shots; `-crow-fight-run` is ignored (fightbench.py names each run with it, so each run's log differs from the last). | `compat/Fight.cs` |
| `-crow-stall-dump <s>`, `-crow-stall-after <t>` | Crash the player when a frame has taken s seconds, for a dump of the main thread's stack (see below). | `compat/Diag.cs` |
| `-crow-pixel-lights <n>`, `-crow-shadows off\|hard\|all`, `-crow-shadow-distance <m>`, `-crow-shadow-resolution <0-3>`, `-crow-lights game\|vita` | Try lighting and shadow settings without a build. The PS Vita's defaults: hard shadows, Low resolution, 12 m, cast by the player only (`-crow-casters player`); the game's are `-crow-shadows all -crow-shadow-resolution 2 -crow-shadow-distance 50 -crow-casters game`. | `compat/Quality.cs` |
| `-crow-loop-profile <s>`, `-crow-loop-cpu`, `-crow-light-report <s>`, `-crow-census <s>`, `-crow-disable <types>`, `-crow-ab ...` | The player loop's time per step (with `-crow-loop-cpu`, the main thread's own run time per frame: the rest is waiting for the render thread or the GPU), light passes, behaviours by type, A/B screenshots. | `compat/Quality.cs` |
| `-crow-cycle <s> <sets>`, `-crow-particles-report <s>`, `-crow-particles game\|off`, `-crow-physics-report <s>`, `-crow-physics <changes>` | Settings tried in turn in one run: after the first area loads, every s seconds the next set of changes (`gpu=222/camera=416/resolution=0`; sets split by `/`, changes by `,`; with `-crow-loop-profile 1000`, one CROW_LOOP line per set); the particle systems playing and what they cost, or all stopped; the fixed time step, physics steps per frame, rigidbodies and colliders, or physics settings (`fixed=`, `maxdt=`, `iter=`, `sync=`). | `compat/Perf.cs` |
| `-crow-draw-report <s>`, `-crow-casters off\|flat\|small:<m>\|player` | What the forward renderer is given (renderers drawn and in view, materials, shadow casters by size, small and readable meshes); shadow casting turned off by a rule (`-crow-ab` takes `casters=<rule>`). | `compat/Draws.cs` |
| `-crow-lit game\|vita`, `-crow-lit-keys <n>`, `-crow-lit-min-range <m>`, `-crow-lit-log`, `-crow-lit-bundle <path>`, `-crow-lit-debug <n>` | Every light in one pass (vita, the PS Vita's default; game elsewhere): the game's Standard materials (and SFBStandard's and Legacy Diffuse's) drawn with CrowVita/Lit (`vita_shaders/CrowLit.shader`, Standard's simple form without a ForwardAdd pass), which draws the main directional light, the harmonics and the 4 point lights Unity gives each object per vertex, and per pixel (Lambert) up to n key lights (default 4, at most 8: the spot and point lights, those of m metres' range or more if given, that reach the camera's view, on the layers most renderers are on) and one fill directional light; the key lights follow the game's lights every frame. The key lights in use (CROW_LIT); the shader from an asset bundle for a build without it; a debug value. `-crow-ab` and `-crow-cycle` take `lit=`, `litkeys=`, `litrange=`. | `compat/VitaLit.cs` |
| `-crow-moving-report <s>`, `-crow-rewired game\|pad`, `-crow-player-split game\|ui` | The renderers whose transforms change every frame and the transform hierarchies they change (each engine system that follows transform changes walks a changed hierarchy whole); Rewired's keyboard and mouse controllers on or off (off is the PS Vita's default: it has neither); the player's menus (`UI Canvases`, 6,304 of the player prefab's 10,251 transforms) a hierarchy of their own (the PS Vita's default). `-crow-cycle` also takes `rewired=`, `split=ui`, `sort=`, `probes=off`, `reflections=off`. | `compat/Perf.cs` |
| `-crow-scaled-colliders game\|box\|report` | Mesh colliders whose scale keeps changing (PhysX cooks them again at every change: Station Square's 18 swinging bunting flags, 8 ms of physics a frame) get a box of their mesh's bounds instead, found by watching the area's non-trigger mesh colliders for 40 frames two seconds after each load and every 15 s (box is the PS Vita's default; report only logs them, CROW_SCALED). `-crow-cycle` takes `scaled=`. | `compat/Perf.cs` |
| `-crow-scene-split off\|on\|check\|report\|apply\|apply-check` | The PS Vita build takes the still parts of each area's big hierarchies out to the scene's root (`Merge.Split` in `CrowMergeScenes`; `CROW_SPLIT=on\|off` when building; below objects the game only switches, a SplitHolder in the part's place switches it). off puts the parts back in their places after each load, on takes them out again (`-crow-cycle` takes `scenesplit=` and `splittoggle=parents-off\|parents-on\|self-off\|self-on`); check compares each part's activity and place with its old place every second and at each switch (CROW_SPLIT_CHECK); report logs what the split would take; apply splits at run time as the build would (other players). | `compat/Perf.cs`, `compat/Merge.cs` |
| `-crow-static-batch off\|all\|safe` | Static batching at run time some seconds after each area loads (`StaticBatchingUtility.Combine`; readable meshes only); safe leaves out what PlayMaker actions or the game's scripts move. Not a default: on the PS Vita it saved 0.5 ms in Roadside with every candidate batched, and safe batches few. | `compat/Draws.cs` |
| `-crow-merge report\|on\|off\|merged`, `-crow-merge-cell <m>`, `-crow-merge-max-vertices <n>`, `-crow-merge-max-kb <n>`, `-crow-merge-check`, `-crow-merge-drop` | The static merge (`compat/Merge.cs`; the PS Vita build merges as it writes each scene, `CROW_MERGE=on\|ab\|off` when building: on, the default, also drops the parts' meshes nothing else uses; ab keeps them so off works): report logs what would merge in each area (CROW_MERGE: candidates, parts, groups, what leaves renderers out); on merges at run time (readable meshes only); off switches a merged build's merged renderers off and their parts back on (builds made with `CROW_MERGE=ab`), merged back again. The grid cell (8 m), the biggest part mesh (500 vertices) and the merged meshes' memory per area (1,536 KB; `CROW_MERGE_MAX_KB` and `CROW_MERGE_MAX_VERTICES` for the build); check logs every merged part that moves, switches or shows apart from its merged renderer (CROW_MERGE_CHECK). `-crow-cycle` takes `merge=` and `hide=static\|safe\|none` (static batching's candidates switched off: what renderers cost). | `compat/Merge.cs`, `compat/Draws.cs` |
| `-crow-memory <s>`, `-crow-memory-types`, `-crow-memory-fsm`, `-crow-memory-unload` | Memory totals, and per asset type, after each scene load. | `compat/Memory.cs` |
| `-crow-memory-watch [ms]` | Memory sampled on a thread of its own every ms (default 250), also through a scene load's long frames: a MEMWATCH line when the GC heap, its free part, or the free main memory, CDRAM or PHYSCONT moved by 1 MB (and every 10 s). The last lines before an out-of-memory crash show which pool ran out. | `compat/Memory.cs` |
| `-crow-gc-retries <n>`, `-crow-gc-divisor <n>`, `-crow-gc-reserve <MB>`, `-crow-gc-max <MB>`, `-crow-no-phycont-heap` | The garbage collector's settings (PS Vita defaults: 2 collections tried before an out-of-memory abort, heap sections of a sixth of the heap, the heap capped so it leaves 16 MB of main memory to Unity; 0 keeps Boehm's own); a fixed ceiling, past which the heap collects instead of growing (a test tool: too low and the player hangs when the heap runs out); the PHYCONT memory (26 MB) left out of the GC heap. | `compat/Memory.cs` |
| `-crow-gc-log [ms]`, `-crow-alloc [s]`, `-crow-alloc-stacks [KB]`, `-crow-alloc-file <path>` | On IL2CPP players, through IL2CPP's profiler interface: each garbage collection (CROW_GC_RUN: its pause, the time and MB allocated since the one before, the heap after) and each frame over ms (default 200; CROW_LONG_FRAME, with the collections' pause in it); every s seconds (default 10, from 5 s after each scene load) the MB and KB a frame allocated and the classes that allocated most (ALLOC, ALLOCTOP); on Windows IL2CPP players a call stack every KB allocated, summed into a file that `tools/downgrade/allocreport.py` names. | `compat/Alloc.cs` |
| `-crow-gc-pace on\|off\|<MB>` | A collection half a second into a pause (Time.timeScale 0: the pause menu, the status screen) once MB (default 2) have been allocated since the last one (PS Vita default; off leaves it to Boehm, which in play collects every ~5.5 MB allocated, a 0.75-1 s stop wherever the game is). | `compat/Alloc.cs` |
| `-crow-idle-audio on\|off`, `-crow-idle-audio-log` | AudioSources idle for 2 s disabled, and enabled again before the game's code plays them (PS Vita default; the rewriter routes AudioSource's play calls and `Behaviour.enabled` through CrowCompat); sources that play on enable are left alone. The log line counts the sources off, woken, and playing the frame after. | `compat/IdleAudio.cs`, `rewriter/IdleAudio.cs` |
| `-crow-tween-recycle on\|game` | PlayMaker's DOTween actions' tweens recyclable (PS Vita default: DOTween's pool hands a completed tween out again instead of a new one; the actions keep no reference to a tween past their OnEnter); game keeps the game's setting. | `compat/Alloc.cs`, `rewriter/Alloc.cs` |
| `-crow-prewarm on\|all` | For builds made with `CROW_PROBUILDER=off` (the PS Vita build now builds the player's ProBuilder meshes itself, `unity/ProBuilderMeshes.cs`, and this is off): the inventory's ProBuilder meshes built during an area load's empty scene instead of at the first inventory open; all builds every player mesh. | `compat/Prewarm.cs` |
| `-crow-log-sync` | Write the log on the thread that logs, as before, instead of on a thread of its own (a write to the memory card sometimes takes a second; CROW_LOG_SLOW marks a write of 150 ms or more). | `compat/ProbeFiles.cs` |
| `-crow-no-gap`, `-crow-gap`, `-crow-gap-single` | Area loads straight from the old area, as the game does, instead of through an empty scene with the old area's assets freed first (the PS Vita's default); the empty scene on other players; from the empty scene, the area loaded as a single-mode load, as before, instead of additively (the default: a single-mode load runs a second unused-asset pass and a collection at the end of the load's frame, 3.2-3.7 s in Station Square and Pool, at the load's peak of memory). `-crow-gap-additive` is accepted and changes nothing. | `compat/Loading.cs` |
| `-crow-post full\|half`, `-crow-camera 624\|550\|416` | The camera effect's size (half is the PS Vita's default); with half, the camera's own picture: 624x351, 550x351 (the PS Vita's default, the same picture: the effect's first pass reads 550 columns) or 416x234 (faster, but edges stair-step). | `compat/CamEffect.cs` |
| `-crow-script-cost game`, `-crow-script-cost-log`, `-crow-blob-trail game` | Keep the per-frame PlayMaker and fade work the port skips, for A/B runs on one build; log the fade actions' starts and ends; keep only the dead Blobs' trail exceptions (by default `BlobTrailSpawn.Spawn` skips a pool whose pieces the Blob's death destroyed, where the game throws a NullReferenceException every 0.01-0.1 s per dead Blob; rewriter `PatchBlobTrailSpawn`). | `compat/ScriptCost.cs`, `rewriter/Patches.cs` |
| `-crow-load-trace [ms]`, `-crow-mcs-time`, `-crow-no-prefetch` | Each long frame's time per player loop step, when scenes load and unload within it, and when a load's long frames end; MeshCombineStudio's combining time per scene (it combines from sceneLoaded then; the Station Square runs with it never finished); the title loaded synchronously instead of read during the logos. | `compat/Loading.cs` |
| `-crow-load-cpu [ms]`, `-crow-load-stats`, `-crow-read-test <files>` | Through each area load, every ms (default 250) from a thread of its own, each CPU's load and each game thread's share of a CPU (CROW_LOADCPU: the main thread, UnityPreload, the render worker, FMOD's threads; with `-crow-gc-log` the collections in the window): which phase a load is in (UnityPreload busy: reading and deserializing; the main thread busy: integration or an unused-asset pass; neither: waiting on the card or on uploads); PlayMaker's load-time methods timed per thread (CROW_LOADSTATS after each area load: OnAfterDeserialize, InitData, LoadActions, CreateAction, LoadActionField, Awake, OnEnable, Start; the rewriter's TimeLoadMethods, `rewriter/LoadStats.cs`); build files (comma-separated, under the data folder) read at startup in 1 MB and 64 KB blocks (CROW_READ: the card's MB/s). Not `-crow-load-cpu` with `-crow-audio`. | `compat/Loading.cs` |
| `-crow-cpu-mhz <n>`, `-crow-gpu-mhz <n>` | The ARM clock the player keeps (default 444 MHz; 0 leaves the power mode's 333) and the GPU's (41, 55, 111, 166 or 222; default 222, 0 leaves the power mode's 166). Through the CrowClock plugin (`tools/downgrade/native/clock`), which the build and `quickcode` put in `Media/Plugins`. | `compat/Clock.cs` |
| `-crow-audio [s]`, `-crow-audio-tap` | Every s seconds (default 2) the DSP clock's advance against the wall clock's (behind means the mixer fell behind: stutter, crackle), the sources playing, clips loading, each CPU's load, and the CPU share of each busy thread (FMOD's mixer, UnityPreload, the render worker) from the kernel through the CrowClock plugin; the tap times the mixer's blocks from inside it (it stalls the mixer during garbage collections, so use it only for block timing). | `compat/Audio.cs` |
| `-crow-audio-game`, `-crow-audio-filters <types>`, `-crow-reverb-max <n>` | Keep every audio filter on, as the game has them (the PS Vita's default turns each reverb filter on only while its source plays and for its decay after, at most 3 at once); which filter types to switch off (`reverb,idle` is the default). | `compat/Audio.cs` |
| `-crow-loader-cpus <cpus>`, `-crow-load-priority <p>`, `-crow-thread <name>:<priority>:<cpus>` | The CPUs of Unity's loading thread (default 1, away from FMOD's CPU 2; `-` leaves it); the background loading priority for the player's scene (default Normal with the loading thread moved, else High); any thread's priority and CPUs, with `-crow-audio`. | `compat/Audio.cs` |
| `-crow-dsp-buffer <n>`, `-crow-real-voices <n>`, `-crow-sample-rate <hz>` | `AudioSettings.Reset` with these before the first scene. | `compat/Audio.cs` |

The comment at the top of each file is the full list, with what each flag does; the probe changes as the port goes on.

## Fight benchmark

`tools/downgrade/fightbench.py` runs the scripted fight (`-crow-fight`, `compat/Fight.cs`) and prints where the frame goes. One command per target:

```powershell
uv run tools/downgrade/fightbench.py vita --modes real,stress --runs 3   # the console: lock, sync, runs, table
uv run tools/downgrade/fightbench.py pc --modes real,stress --runs 1     # a Windows player (run_player.py)
uv run tools/downgrade/fightbench.py pc --vitalike --timeout 1200         # the same at Vita-like CPU speed
uv run tools/downgrade/fightbench.py report <tag>                        # the table again from saved logs
```

`vita` takes the `vita-device` lock (waiting up to `--wait` seconds, 2 hours by default, for whoever holds it; inside `lock.py run vita-device` it uses that), syncs the build (`--no-sync` skips it), and for each mode and run quits the game, writes args.txt, launches, keeps the console awake, waits for the summary line (`--timeout`, 420 s a run), pulls the log and samples vitacompanion's `perf` during the first windows. However it ends, it quits the game and clears args.txt. Each run takes about 3 minutes (about a minute to the title and the area, 12 s of settling, 6 s of fighting, 60 s measured). `pc` runs a copy of the Windows player (`builds/crow-2018.2.19f1`, or `--build`) through `run_player.py`, on its own desktop, windowed and muted; for a Mono build the copy gets today's CrowCompat (compiled into `work/fightbench/compat`), so no new build is needed, and Steam's `steam_api` library and `steam_appid.txt` are left out of it. `--vitalike [CAP]` runs that player at CAP percent of the wall time (5.5 by default) on three cores, with `vitalike.py`'s duty cycle and guards; the calibration was made for the IL2CPP player, so with a Mono build the numbers are only a rough guide. Extra `-crow-*` flags go after `--`, such as `-crow-fight-area "Ocean Kingdom"` or `-crow-shot 40 <png>`.

Logs, perf samples and `summary.txt` go to `artifacts/fightbench/<tag>/` (the tag defaults to the date and time). The table has one row per run and a mean per mode; its columns are milliseconds per frame:

| Column | What |
| --- | --- |
| frame, p50, p95, p99, worst, fps | Frame time (`Time.unscaledDeltaTime`) over the measured windows. |
| main | The main thread's own run time per frame (PS Vita, from the kernel through CrowClock); the rest of the frame it waits for the render thread, the GPU or the display. |
| scripts, phys, anim, skin, part, audio, render, ui, wait | The player loop by subsystem (`-crow-loop-profile`'s steps): every script's Update, LateUpdate and FixedUpdate (PlayMaker's FSMs among them); physics; Animator, Director and legacy animation; skinned meshes; particle systems; audio; culling, draw submission and the renderers' update (this step also waits when the render thread or the GPU is behind); canvases; present, frame start and target-rate waits. The window lines' `top=` lists the costliest steps by name, and each window's CROW_LOOP and CROW_CAMS lines (`cycle=fight-wN`) have the per-step and per-camera detail. |
| gc, gcms, KB/s | Collections, their pause (`-crow-gc-log`) and the allocation rate (IL2CPP players). |
| alive, shots, kills, freeMB | Enemies alive (mean), shots fired, kills (the `Kills` global), the lowest free main memory (PS Vita). |

The modes. `real` runs Station Square's own fight: its 9 enemies (6 Blobs, 2 Grinners, a Spindle) are those of a later visit (`VisitedCrowCountry`, items 2, 5 and 9, `NPC6` 3, set before the area loads), and 7 of them stand within 5 m of the spot by the area's entry; as they die the fight winds down (about 3 left, out of reach, by the last window). `stress` adds 12 copies of them in a ring around Mara and keeps about 20 alive. `idle` is the same setup with no enemies and no shots, the baseline. Enemies cull themselves beyond 5 m of the camera's visibility point, so only those near Mara cost anything. Mara's spot is fixed per area (`Areas` in `Fight.cs`; elsewhere near the biggest group, or `-crow-fight-spot`), and UnityEngine.Random is seeded, but the enemies' physics and the moment of each frame differ between runs: compare means over several runs. Other areas with many enemies (`-crow-fight-area`): Ocean Kingdom (12 with its globals), Haunted Hilltop (10), Underground SW and Pre Machine Centre (6-7 on a first visit).

## Crash dumps

When the player crashes, the console writes a dump to `ux0:data`. `vita.py crashes` lists them, and `vita.py crashes --pull` fetches the newest, reads it with [vita-parse-core](https://github.com/xyzz/vita-parse-core) (downloaded into `tools/external` on first use) and prints the crashed thread's call chain, with names from the SDK's `psp2bin` and the build's `SymbolFiles`. `sync`, `install` and `seed` keep a copy of the game module's symbols for each build they send (`artifacts/vita/symbols/<fingerprint>`, the last 5, about 55 MB each), and `crashes` uses the copy whose fingerprint the dump lists, so a dump still reads right after the next `quickcode`. Without a copy it maps the dump's game addresses onto the build's symbols through the two modules' exception indexes, which the dump keeps (frames marked `game~`); the engine's frames are always right. `--thread main` prints the main thread instead.

A frame that takes seconds or minutes doesn't crash, and the console has no sampling profiler. `-crow-stall-dump 15` makes the player crash when a frame has taken 15 seconds, so the dump holds the main thread's stack in the middle of it; `-crow-stall-after <t>` skips the stalls of the first t seconds. The watcher looks every quarter of the limit, so a fraction of a second works too (`-crow-stall-dump 0.1` samples a 150 ms hitch).

```powershell
uv run tools/downgrade/vita.py args -crow-stall-dump 15
uv run tools/downgrade/vita.py launch
uv run tools/downgrade/vita.py crashes --pull --thread main
```

## Code changes without a full build

A full PS Vita build takes `project` and the editor's `build` (about half an hour). For a change to the rewriter or CrowCompat, `quickcode` rewrites the assemblies and replays the last full build's UnityLinker and IL2CPP steps on the inputs that build saved in `work/vita-code-stage`, then puts the new `il2CppAssemblies.suprx` and `global-metadata.dat` in `builds/crow-2018.2.19f1-vita`, in about 2 minutes. `vita.py sync` uploads the two files.

```powershell
uv run tools/downgrade/lock.py run vita-build "me: what for" --wait 7200 -- uv run tools/downgrade/pipeline.py quickcode
uv run tools/downgrade/vita.py run
uv run tools/downgrade/vita.py log --tail 40
```

The build's scenes and assets keep the scripts' fields as they were built, so `quickcode` stops when a type the data serializes changed (a field, a new MonoBehaviour or ScriptableObject) or a `[RuntimeInitializeOnLoadMethod]` came or went, and it warns when the asset rules or the build script changed since the full build. Those changes need `project` and `build` again (`--force` tries anyway).

For changes to assets, `pipeline.py build --scenes "Title,Station Square"` builds the first scene (Logos) and the named ones into `builds/crow-2018.2.19f1-vita-scenes`, leaving the full build alone; install it with `vita.py --build builds/crow-2018.2.19f1-vita-scenes install`. The game loads scenes by name, so loading a scene left out fails.
