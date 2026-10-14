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
| `-crow-scene <name>` | Load an area once the first scene has loaded. | `compat/Diag.cs` |
| `-crow-heartbeat <s>` | Every s seconds, log frame times, GC and memory, visible renderers and lights. | `compat/Diag.cs` |
| `-crow-quit-after <s>`, `-crow-shot <s> <path>`, `-crow-no-vsync`, `-crow-mute`, `-crow-input` | Timed runs, screenshots, unsynchronized frame times, no sound, input logging. | `compat/Diag.cs` |
| `-crow-stall-dump <s>`, `-crow-stall-after <t>` | Crash the player when a frame has taken s seconds, for a dump of the main thread's stack (see below). | `compat/Diag.cs` |
| `-crow-pixel-lights <n>`, `-crow-shadows off\|hard\|all`, `-crow-shadow-distance <m>`, `-crow-shadow-resolution <0-3>`, `-crow-lights game\|vita` | Try lighting and shadow settings without a build. | `compat/Quality.cs` |
| `-crow-loop-profile <s>`, `-crow-light-report <s>`, `-crow-census <s>`, `-crow-disable <types>`, `-crow-ab ...` | The player loop's time per step, light passes, behaviours by type, A/B screenshots. | `compat/Quality.cs` |
| `-crow-draw-report <s>`, `-crow-casters off\|flat\|small:<m>` | What the forward renderer is given (renderers drawn and in view, materials, shadow casters by size, small and readable meshes); shadow casting turned off by a rule (`-crow-ab` takes `casters=<rule>`). | `compat/Draws.cs` |
| `-crow-memory <s>`, `-crow-memory-types`, `-crow-memory-fsm`, `-crow-memory-unload` | Memory totals, and per asset type, after each scene load. | `compat/Memory.cs` |
| `-crow-post full\|half` | The camera effect's size (half is the PS Vita's default). | `compat/CamEffect.cs` |
| `-crow-script-cost game`, `-crow-script-cost-log` | Keep the per-frame PlayMaker and fade work the port skips, for A/B runs on one build; log the fade actions' starts and ends. | `compat/ScriptCost.cs` |
| `-crow-load-trace [ms]`, `-crow-mcs-time`, `-crow-no-prefetch` | Each long frame's time per player loop step, when scenes load and unload within it, and when a load's long frames end; MeshCombineStudio's combining time per scene (it combines from sceneLoaded then; the Station Square runs with it never finished); the title loaded synchronously instead of read during the logos. | `compat/Loading.cs` |
| `-crow-cpu-mhz <n>` | The ARM clock the player keeps (default 444 MHz; 0 leaves the power mode's 333). Through the CrowClock plugin (`tools/downgrade/native/clock`), which the build and `quickcode` put in `Media/Plugins`. | `compat/Clock.cs` |

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
