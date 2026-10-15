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
| `ux0:data/crow` | The probe's flags (`args.txt`) and the player's log (`log.txt`, at most 8 MB). |

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
| `log [--tail N]` | Print the player's log. |
| `args [flags...]` | Write the probe's flags to `ux0:data/crow/args.txt`. |
| `files [folder]` | List the files under a console folder, by default the saves. |
| `crashes [--pull] [--thread NAME]` | List the crash dumps in `ux0:data`; `--pull` fetches the newest into `artifacts/vita/` and prints a thread's symbolized call chain. |
| `reboot` | Reboot the console. |

`--build` takes another build folder, such as a test build's `builds/crow-2018.2.19f1-vita-scenes`.

When several jobs share the console, take the `vita-device` lock first (`tools/downgrade/lock.py`); a person at the console holds it as `user: <purpose>`.

## Probe flags

CrowCompat, the compatibility library the pipeline adds to the game, has a measurement probe that is off unless the player gets a `-crow-*` flag. A Windows player takes the flags on its command line; on the PS Vita, which passes no command line, the player reads them from `ux0:data/crow/args.txt`, which `vita.py args` writes. They apply from the next launch. `-crow-help` logs the probe's own list.

| Flags | Effect | Source |
| --- | --- | --- |
| `-crow-scene <name>`, `-crow-scene-then <name> <s>` | Load an area once the title and the player's scene have loaded; s seconds (default 30) after it, load a second one as the game's area changes do (with the player in play). | `compat/Diag.cs` |
| `-crow-heartbeat <s>` | Every s seconds, log frame times, GC and memory, visible renderers and lights. | `compat/Diag.cs` |
| `-crow-quit-after <s>`, `-crow-shot <s> <path>`, `-crow-no-vsync`, `-crow-mute`, `-crow-input` | Timed runs, screenshots, unsynchronized frame times, no sound, input logging. | `compat/Diag.cs` |
| `-crow-stall-dump <s>`, `-crow-stall-after <t>` | Crash the player when a frame has taken s seconds, for a dump of the main thread's stack (see below). | `compat/Diag.cs` |
| `-crow-pixel-lights <n>`, `-crow-shadows off\|hard\|all`, `-crow-shadow-distance <m>`, `-crow-shadow-resolution <0-3>`, `-crow-lights game\|vita` | Try lighting and shadow settings without a build. | `compat/Quality.cs` |
| `-crow-loop-profile <s>`, `-crow-loop-cpu`, `-crow-light-report <s>`, `-crow-census <s>`, `-crow-disable <types>`, `-crow-ab ...` | The player loop's time per step (with `-crow-loop-cpu`, the main thread's own run time per frame: the rest is waiting for the render thread or the GPU), light passes, behaviours by type, A/B screenshots. | `compat/Quality.cs` |
| `-crow-cycle <s> <sets>`, `-crow-particles-report <s>`, `-crow-particles game\|off`, `-crow-physics-report <s>`, `-crow-physics <changes>` | Settings tried in turn in one run: after the first area loads, every s seconds the next set of changes (`gpu=222/camera=416/resolution=0`; sets split by `/`, changes by `,`; with `-crow-loop-profile 1000`, one CROW_LOOP line per set); the particle systems playing and what they cost, or all stopped; the fixed time step, physics steps per frame, rigidbodies and colliders, or physics settings (`fixed=`, `maxdt=`, `iter=`, `sync=`). | `compat/Perf.cs` |
| `-crow-draw-report <s>`, `-crow-casters off\|flat\|small:<m>` | What the forward renderer is given (renderers drawn and in view, materials, shadow casters by size, small and readable meshes); shadow casting turned off by a rule (`-crow-ab` takes `casters=<rule>`). | `compat/Draws.cs` |
| `-crow-moving-report <s>`, `-crow-rewired game\|pad`, `-crow-player-split game\|ui` | The renderers whose transforms change every frame and the transform hierarchies they change (each engine system that follows transform changes walks a changed hierarchy whole); Rewired's keyboard and mouse controllers on or off (off is the PS Vita's default: it has neither); the player's menus (`UI Canvases`, 6,304 of the player prefab's 10,251 transforms) a hierarchy of their own (the PS Vita's default). `-crow-cycle` also takes `rewired=`, `split=ui`, `sort=`, `probes=off`, `reflections=off`. | `compat/Perf.cs` |
| `-crow-static-batch off\|all\|safe` | Static batching at run time some seconds after each area loads (`StaticBatchingUtility.Combine`; readable meshes only); safe leaves out what PlayMaker actions or the game's scripts move. Not a default: on the PS Vita it saved 0.5 ms in Roadside with every candidate batched, and safe batches few. | `compat/Draws.cs` |
| `-crow-memory <s>`, `-crow-memory-types`, `-crow-memory-fsm`, `-crow-memory-unload` | Memory totals, and per asset type, after each scene load. | `compat/Memory.cs` |
| `-crow-memory-watch [ms]` | Memory sampled on a thread of its own every ms (default 250), also through a scene load's long frames: a MEMWATCH line when the GC heap, its free part, or the free main memory, CDRAM or PHYSCONT moved by 1 MB (and every 10 s). The last lines before an out-of-memory crash show which pool ran out. | `compat/Memory.cs` |
| `-crow-gc-retries <n>`, `-crow-gc-divisor <n>`, `-crow-gc-reserve <MB>`, `-crow-no-phycont-heap` | The garbage collector's settings (PS Vita defaults: 2 collections tried before an out-of-memory abort, heap sections of a sixth of the heap, the heap capped so it leaves 16 MB of main memory to Unity; 0 keeps Boehm's own); the PHYCONT memory (26 MB) left out of the GC heap. | `compat/Memory.cs` |
| `-crow-no-gap`, `-crow-gap` | Area loads straight from the old area, as the game does, instead of through an empty scene with the old area's assets freed first (the PS Vita's default); the empty scene on other players. | `compat/Loading.cs` |
| `-crow-post full\|half`, `-crow-camera 624\|550\|416` | The camera effect's size (half is the PS Vita's default); with half, the camera's own picture: 624x351, 550x351 (the PS Vita's default, the same picture: the effect's first pass reads 550 columns) or 416x234 (faster, but edges stair-step). | `compat/CamEffect.cs` |
| `-crow-script-cost game`, `-crow-script-cost-log` | Keep the per-frame PlayMaker and fade work the port skips, for A/B runs on one build; log the fade actions' starts and ends. | `compat/ScriptCost.cs` |
| `-crow-load-trace [ms]`, `-crow-mcs-time`, `-crow-no-prefetch` | Each long frame's time per player loop step, when scenes load and unload within it, and when a load's long frames end; MeshCombineStudio's combining time per scene (it combines from sceneLoaded then; the Station Square runs with it never finished); the title loaded synchronously instead of read during the logos. | `compat/Loading.cs` |
| `-crow-cpu-mhz <n>`, `-crow-gpu-mhz <n>` | The ARM clock the player keeps (default 444 MHz; 0 leaves the power mode's 333) and the GPU's (41, 55, 111, 166 or 222; default 222, 0 leaves the power mode's 166). Through the CrowClock plugin (`tools/downgrade/native/clock`), which the build and `quickcode` put in `Media/Plugins`. | `compat/Clock.cs` |
| `-crow-audio [s]`, `-crow-audio-tap` | Every s seconds (default 2) the DSP clock's advance against the wall clock's (behind means the mixer fell behind: stutter, crackle), the sources playing, clips loading, each CPU's load, and the CPU share of each busy thread (FMOD's mixer, UnityPreload, the render worker) from the kernel through the CrowClock plugin; the tap times the mixer's blocks from inside it (it stalls the mixer during garbage collections, so use it only for block timing). | `compat/Audio.cs` |
| `-crow-audio-game`, `-crow-audio-filters <types>`, `-crow-reverb-max <n>` | Keep every audio filter on, as the game has them (the PS Vita's default turns each reverb filter on only while its source plays and for its decay after, at most 3 at once); which filter types to switch off (`reverb,idle` is the default). | `compat/Audio.cs` |
| `-crow-loader-cpus <cpus>`, `-crow-load-priority <p>`, `-crow-thread <name>:<priority>:<cpus>` | The CPUs of Unity's loading thread (default 1, away from FMOD's CPU 2; `-` leaves it); the background loading priority for the player's scene (default Normal with the loading thread moved, else High); any thread's priority and CPUs, with `-crow-audio`. | `compat/Audio.cs` |
| `-crow-dsp-buffer <n>`, `-crow-real-voices <n>`, `-crow-sample-rate <hz>` | `AudioSettings.Reset` with these before the first scene. | `compat/Audio.cs` |

The comment at the top of each file is the full list, with what each flag does; the probe changes as the port goes on.

## Crash dumps

When the player crashes, the console writes a dump to `ux0:data`. `vita.py crashes` lists them, and `vita.py crashes --pull` fetches the newest, reads it with [vita-parse-core](https://github.com/xyzz/vita-parse-core) (downloaded into `tools/external` on first use) and prints the crashed thread's call chain, with names from the SDK's `psp2bin` and the build's `SymbolFiles`. Use the build the console runs, and keep its `SymbolFiles`. `--thread main` prints the main thread instead.

A frame that takes seconds or minutes doesn't crash, and the console has no sampling profiler. `-crow-stall-dump 15` makes the player crash when a frame has taken 15 seconds, so the dump holds the main thread's stack in the middle of it; `-crow-stall-after <t>` skips the stalls of the first t seconds.

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
