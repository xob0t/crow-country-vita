# PS Vita

The app is Unity's own PS Vita player, built by the Unity 2018.2.19f1 editor with its PS Vita module and Sony's PS Vita SDK 3.570.
[build.md](build.md) sets them up and finds the game, from Steam or GOG.
The build is a PC Hosted player in `builds/crow-2018.2.19f1-vita`.
`pipeline.py package` turns it into `builds/crow-2018.2.19f1-vita.vpk`.
In the package the player is `eboot.bin` with its trial flag cleared, and the LiveArea art comes from the game's Steam store images (`livearea.py`).
The title ID is `CROW00001`.

## Installing

You need a PS Vita on HENkaku or Ensō with [VitaShell](https://github.com/TheOfficialFloW/VitaShell).
[vitacompanion](https://github.com/devnoname120/vitacompanion) is optional.
It adds an FTP server that works while the game runs, and a command server that `vita.py` uses to start and quit the game, take screenshots and press buttons.

1. In VitaShell, press Select to start its FTP server. Upload the VPK to `ux0:data/` with an FTP client on port 1337, or copy it over USB.
2. In VitaShell, open the VPK and install it.

The app asks for the +109 MB memory expansion (`unity/DowngradeBuild.cs`).
The system reads that from `param.sfo` at installation, so a build that changes `param.sfo` needs a new installation.
`vita.py sync` updates everything else in place.

On the console the app keeps its files in these folders:

| Folder | What |
| --- | --- |
| `ux0:app/CROW00001` | The installed app. It is read-only to the game. |
| `ux0:data/CrowCountry` | The game's saves and settings (`compat/Saves.cs`). |
| `ux0:data/crow` | The probe's flags in `args.txt`, and the player's log in `log.txt`. The log is capped at 8 MB. The two runs before are in `log.prev.txt` and `log.prev2.txt`. |

## vita.py

`tools/downgrade/vita.py` drives the installed app at the address in `CROW_VITA_HOST` (or `DRIFTER_VITA_HOST`).
`uv run tools/setup.py --vita-host <address>` sets it.

What each command needs on the console:

- `seed` needs no console.
- `sync`, `log`, `args`, `files` and `crashes` need only an FTP server on port 1337, VitaShell's or vitacompanion's.
- The others need vitacompanion's command server on port 1338. `do`, `install` and `shot` need vitacompanion 1.20 or later, and `perf` 1.33.
- `install`, `perf` and `shot` use commands the released vitacompanion lacks. Without them they stop with a message.

```powershell
uv run tools/downgrade/vita.py sync                     # upload what changed since the last sync (the first uploads everything)
uv run tools/downgrade/vita.py run                      # quit, sync, launch
uv run tools/downgrade/vita.py log --tail 40            # the player's log
uv run tools/downgrade/vita.py args -crow-heartbeat 5   # the probe's flags for the next launch
uv run tools/downgrade/vita.py args                     # no flags clears them
uv run tools/downgrade/vita.py args --show              # the flags the next launch gets, changing nothing
uv run tools/downgrade/vita.py crashes --pull           # the newest crash dump, symbolized
```

| Command | Effect |
| --- | --- |
| `sync [--build dir]` | Upload the files that changed since the last sync into `ux0:app/CROW00001`. It quits a running game first, since replacing files under it crashes it. |
| `seed --build dir` | Record a build as installed, from its VPK say, without uploading. The next sync then sends only the differences. |
| `install [--build dir]` | Package the build as a VPK and stream it to the installer. This also applies `param.sfo` changes. |
| `launch`, `quit`, `run` | Start the game, quit all apps, or quit, sync and launch. |
| `do "<chain>"` | Run a vitacompanion command chain, for example `do "press cross; wait 100ms; release cross"`. |
| `shot [out.png]` | A system screenshot of what the screen shows. |
| `perf` | Clocks, CPU load per core, frame rate and battery. |
| `log [--tail N] [--prev [N]]` | Print the player's log. `--prev` prints the run before's, `--prev 2` the one before that. |
| `args [flags...]` | Write the probe's flags to `ux0:data/crow/args.txt`. A value with spaces, such as `-crow-fight-area "Ocean Kingdom"`, is quoted in the file. A word that starts with two dashes is refused. With no flags it clears the file. |
| `args --show` | Print the flags the next launch gets. |
| `files [folder]` | List the files under a console folder. The default is the saves folder. |
| `crashes [--pull] [--thread NAME] [--dump FILE]` | List the crash dumps in `ux0:data`. See [Crash dumps](#crash-dumps). |
| `reboot` | Reboot the console. |

`--build` takes another build folder, such as `builds/crow-2018.2.19f1-vita-scenes`.

### Locks

When several jobs share the console, take the `vita-device` lock first with `tools/downgrade/lock.py`.
A person at the console holds it as `user: <purpose>`.
These commands refuse while someone else holds it: `sync`, `install`, `launch`, `quit`, `run`, `do`, `reboot` and `args`.
Run them inside `lock.py run vita-device ...`, or set `CROW_HOLDER=<your holder name>` after `lock.py take`.

`sync`, `install` and `seed` read the build folder under the `vita-build` lock, so a `build` or `quickcode` can't rewrite the files while they go out.
`run` does the same before it quits the game.
They hold the lock as a reader.
A build or `quickcode` waits for a reader as for any holder.
A Windows build under `pc-build` and a reader don't wait for each other.
That is why `pipeline.py` stops a PS Vita `build`, `package` or `quickcode` started under `pc-build`.
Those need `vita-build`.

A reader waits up to `--build-wait` seconds (900 by default) for a build that holds the lock, then stops with exit code 75.
`run` waits before it quits the game, so a busy lock leaves the game running.
Inside `lock.py run vita-build ...`, the lock is the caller's and they don't wait.
The same goes for a `lock.py take vita-build` when `CROW_HOLDER` names its holder.
Without `CROW_HOLDER`, a `vita-build` lock you took by hand makes them wait for your own lock.

### Working with the console

- Don't kill `vita.py` while it sends or reads a file. An FTP transfer cut off part way can hang the console's FTP server until the console reboots. Ctrl+C or Ctrl+Break lets the current file finish and then stops. A second one closes the transfer's connection cleanly and stops at once. `lock.py run` waits for its command after Ctrl+C.
- `sync` and `args` write each file as `<name>.part` and then rename it, so an interrupted upload never leaves the app a short file. `sync` sends an interrupted file again next time.
- Use one FTP client at a time. The console's FTP server has hung under two clients at once.
- Never send `screen off`. With `nosleep off` the console goes into full standby, and only its power button brings it back.
- Clear `args.txt` after a probe run with `vita.py args` and no flags. The flags stay on the card and apply to every launch until then, the user's own play included.
- Add `-crow-mute` to test runs on the console.
- Test runs can write saves. If the saves in `ux0:data/CrowCountry` matter, copy them off the console first.

Unattended runs need the display kept on.
vitacompanion's `nosleep` holds off standby but not the display's idle timeout.
When the display goes off the system pauses the game.
`perf` then shows `app: none` and 0 fps, while FTP and the command server still answer.
`screen on` resumes the game where it was.
A script that finds the log stalled and quits the game at that point leaves a log that ends with no dump, which looks like a silent crash.
`vita.py log` sends `nosleep on; screen on` with every read, and `shot` waits until the screen draws again after waking it.
A script that polls the console another way should send `vita.py do "nosleep on; screen on"` every few seconds, as `fightbench.py` does.

## Probe flags

CrowCompat is the compatibility library the pipeline adds to the game.
Its measurement probe is off unless the player gets a `-crow-*` flag.
A Windows player takes the flags on its command line.
The PS Vita passes no command line, so there the player reads them from `ux0:data/crow/args.txt`, which `vita.py args` writes.
They apply from the next launch.
`-crow-help` logs the list of Diag's own flags.

Many flags switch a PS Vita change on or off.
The tables give the PS Vita default.
Other players default to the game's own behaviour unless a row says otherwise.
The header comment of each source file in `tools/downgrade/compat` describes its flags in more detail.

### Runs, scenes and screenshots

| Flag | Effect |
| --- | --- |
| `-crow-scene <name>` | Load an area once the title and the player's scene have loaded. |
| `-crow-scene-then <name> <s>` | Load a second area s seconds (default 30) after the first, as the game's area changes do, with the player in play. `"A>B>C"` loads several in turn, s seconds apart. |
| `-crow-globals "<name>=<value>;..."` | Set PlayMaker globals just before the first load, each typed as the variable is. `PlayerExit=42` starts Underground NW in its elevator, as the ride down from Corridor A does. It works with `-crow-fight walk`. The other fight modes take `-crow-fight-globals`. |
| `-crow-quit-after <s>` | Quit s seconds after the probe starts. |
| `-crow-shot <s> <path>` | Save a screenshot s seconds after the last scene load. On the PS Vita, ScreenCapture writes nothing, so the player reads the frame back and saves a PNG itself. That needs about 1.5 MB while it runs. |
| `-crow-no-vsync` | vSyncCount 0 and no frame cap. On the PS Vita, pass `-crow-vsync 0` too. |
| `-crow-mute` | No sound. |
| `-crow-input` | Log joystick buttons as they go down, and every 5 s the joysticks Unity and Rewired see. |
| `-crow-language <name>` | Keep I2 Localization on one language. |
| `-crow-windowed` | Keep a Windows player in a window whatever the game's display setting. `run_player.py` passes it. |
| `-crow-find <regex> <s>` | s seconds after each scene load, log every renderer whose hierarchy path matches, active or not. The lines give its state, bounds, mesh, vertex count, materials and shaders. |
| `-crow-teleport <x,y,z> <s>` | s seconds (default 3) after each scene load, move the player's "character RIGIDBODY" to that world position. Entrance's tutorial paper is at `0.3,-0.2,-1.6`. |
| `-crow-activate <name> <s>` | s seconds after each scene load, activate every GameObject of that name with its parents. Repeatable. `"UI Canvas Inventory OFF"` opens the status screen. |
| `-crow-video-log` | Log each playing video's clip, frame and time every second. |
| `-crow-log-sync` | Write the log on the thread that logs instead of on the log's own thread. A write to the memory card can take a second. CROW_LOG_SLOW marks writes of 150 ms or more. |

Code in `compat/Diag.cs`, `compat/Fight.cs`, `compat/InputProbe.cs`, `compat/Window.cs`, `compat/Video.cs` and `compat/ProbeFiles.cs`.

### Frame timing and dumps

| Flag | Effect |
| --- | --- |
| `-crow-heartbeat <s>` | Every s seconds, log frame times, GC and memory, visible renderers and lights. |
| `-crow-loop-profile <s>` | Every s seconds, the main thread's time per player loop step (CROW_LOOP). CROW_CAMS lines split each camera's time into stages. `image` is the camera effect's passes, and `late` runs from the effect's end to the frame's end. |
| `-crow-loop-cpu` | With `-crow-loop-profile` on the PS Vita, the main thread's own run time per frame. For the rest of the frame it waits for jobs, the render thread or the GPU. |
| `-crow-frame-probe` | With `-crow-loop-profile` on the PS Vita, where the frame waits. A thread reads the main and render threads' kernel state every 2 ms and logs CROW_FRAME by loop step and camera stage, with the objects they wait on. FrameTimingManager gives no GPU time on the PS Vita (`ftmGpu=none`). |
| `-crow-sample-dump <stage> wait\|run <ms> <s>` | With `-crow-frame-probe`, a crash dump once the main thread has waited or run ms in a camera stage (`cull`, `draw`, `after`, `image`, `late`) or a loop step. It starts s seconds after the probe. Read it with `vita.py crashes --pull --thread <name>`. `work/scratch/render/dumpstacks.py` prints every thread. |
| `-crow-hook-cost [s]` | Every s seconds (default 10), the time of each handler CrowCompat hangs on the engine's events (CROW_HOOKS, `name=mean/worst/calls`). Nothing is timed without it. MonoBehaviours' own updates and the rewriter's per-call hooks are not in it. |
| `-crow-stall-dump <s>` | Crash the player when a frame has taken s seconds, for a dump of the main thread's stack. See [Crash dumps](#crash-dumps). |
| `-crow-stall-after <t>` | With `-crow-stall-dump`, ignore frames that start in the first t seconds. |
| `-crow-load-trace [ms]` | Each frame of ms or more (default 200) with its time per loop step, and the scene loads and unloads within it (CROW_LOADFRAME, CROW_LOAD, CROW_LOADDONE). |
| `-crow-load-cpu [ms]` | Through each area load, every ms (default 250), each CPU's load and each game thread's share (CROW_LOADCPU). It shows which phase a load is in. It doesn't run with `-crow-audio`, which reads the same counters. |
| `-crow-load-stats` | PlayMaker's load-time methods timed per thread (CROW_LOADSTATS after each area load). |
| `-crow-action-profile` | PlayMaker's actions by type and every MonoBehaviour's updates and callbacks, timed per fight window (CROW_ACTIONS, CROW_BEHAVIOURS, CROW_OWNERS, CROW_FSMS, CROW_OWNER_ACTIONS). It needs code rewritten with `CROW_ACTION_PROFILE=1 uv run tools/downgrade/pipeline.py quickcode`. A normal `quickcode` removes it again. The timing doubles the scripts' time, so only the proportions count. |
| `-crow-tween-log` | Every 10 s, DOTween's update in ms a frame and the tweens it moved (CROW_TWEENS). The fight windows always log `dotween=`. |
| `-crow-tween-detail` | Also time DOTween's callbacks (`cb=`). Key `tweendetail=`. |
| `-crow-tween-census <s>` | Every s seconds, the playing tweens by owner, plugin type, loops, ease and update type (CROW_TWEEN_CENSUS). |
| `-crow-draw-report <s>` | Every s seconds, what the forward renderer is given: renderers drawn and in view, materials, shadow casters and small meshes (CROW_DRAWS). |
| `-crow-camera-report [s]` | After each scene load, and every s seconds if given, each camera that rendered, with its settings (CROW_CAMERA). Once, the graphics device's threading and the settings that cost per draw (CROW_SYSINFO). |
| `-crow-particles-report <s>` | Every s seconds, the particle systems playing, and the busiest with the settings that cost (CROW_PARTICLES). |

Code in `compat/Diag.cs`, `compat/Quality.cs`, `compat/LoopProfile.cs`, `compat/CrowEvents.cs`, `compat/Loading.cs`, `compat/ActionProfile.cs`, `compat/Tweens.cs`, `compat/Draws.cs`, `compat/Cameras.cs` and `compat/Particles.cs`.

### Change sets in one run

These flags apply change sets during a run:

- `-crow-ab <s> <path> "<set>/<set>..."`: s seconds after a scene load it stops time and saves `<path>-a.png`. It then applies each set in turn and saves `-b.png`, `-c.png` and so on, all of the same moment. It works on the PS Vita too.
- `-crow-ab-hold <s>` holds each `-crow-ab` picture s seconds and logs CROW_AB_SHOW as it shows, for `vita.py shot`.
- `-crow-cycle <s> <sets>`: after the first area loads, it applies the next set every s seconds. With `-crow-loop-profile 1000`, each set's window gets one CROW_LOOP line labelled with the set.
- `-crow-fight-cycle <sets>` applies one set per fight window. See [Fight benchmark](#fight-benchmark).

Sets are split by `/` and the changes in a set by `,`, for example `-crow-cycle 10 gpu=222/gpu=166/resolution=0 -crow-loop-profile 1000`.
All three take the same keys (`Quality.Change`, `compat/Quality.cs`).
The flag tables below name each flag's key.
These keys belong to no flag:

| Key | Effect |
| --- | --- |
| `time=<scale>` | The time scale. 0 holds a moment, for screenshots. |
| `vsync=<n>` | vSyncCount. |
| `iter=<n>`, `sync=0\|1` | Physics solver iterations and `Physics.autoSyncTransforms`, as `-crow-physics` sets them. |
| `hide=<what>` | Switch off the enabled renderers that match until `hide=none`, to find what the GPU's frame is spent on. `transparent` matches materials past the opaque queue, but not particle systems. `dense:<n>` matches meshes of n vertices or more. `mat:<regex>` matches a material's name. Anything else is a regex on the hierarchy path, in which `.` matches the path's `/`. The regex can't hold `/`, `,` or `=`. CROW_DRAWS hide logs what it hid. |
| `aplay=<name>` | Play the AudioSource on the active GameObject of that name, as the game does. `aplay=new item` is the item pickup. |
| `pmglobal=<name>:true\|false` | Set a PlayMaker global bool. `pause mode` holds the enemies as a menu does. |
| `acensus=1` | An audio census now. See `-crow-audio-census`. |

Code in `compat/Perf.cs`, `compat/Draws.cs`, `compat/Audio.cs`, `compat/Cuts.cs` and `compat/AudioCensus.cs`.

### Lighting and shadows

| Flag | Effect |
| --- | --- |
| `-crow-pixel-lights <n>` | Lights per object drawn per pixel. Key `pixel=`. |
| `-crow-shadows off\|hard\|all` | Default hard. The game uses all. Key `shadows=`. |
| `-crow-shadow-distance <m>` | Default 12 m. The game uses 50. Key `distance=`. |
| `-crow-shadow-resolution <0-3>` | Low to VeryHigh. Default 0. The game uses 2. Key `resolution=`. |
| `-crow-casters off\|player\|game` | Turn shadow casting off by rule: off for all, player for all but the player's (the default), game for none. The shadow pass costs about 9 ms whatever casts, so there are no finer rules. Key `casters=`. |
| `-crow-lit game\|vita` | vita draws every light in one pass, as described below. Default vita. game needs a build made with `CROW_LIT_BAKE=off`. Key `lit=`. |
| `-crow-lit-keys <n>` | At most n key lights, 0 to 4. Default 4. Key `litkeys=`. |
| `-crow-lit-fade <s>` | The fade when the key lights change. Default 0.35. Key `litfade=`. |
| `-crow-lit-view <m>` | The radius around the player that the key-light scoring counts. Default 4. Key `litview=`. |
| `-crow-lit-area on\|off` | off lifts the areas' own key-light caps, for A/B windows. Key `litarea=`. |
| `-crow-lit-log` | Log the key lights when they change (CROW_LIT). After a load it also logs each material pass with the renderers per layer, and the lights left out for their layers (`cover=`). |
| `-crow-lit-bundle <path>` | Load CrowVita/Lit from an asset bundle, for a build without it. |
| `-crow-lit-debug <n>` | Set the shader's `_CrowDebug`. 1 switches the depth layers off. 2 lets the key lights light every layer. |

Code in `compat/Quality.cs`, `compat/Draws.cs` and `compat/VitaLit.cs`.

The game's own lighting settings, for comparison: `-crow-shadows all -crow-shadow-resolution 2 -crow-shadow-distance 50 -crow-casters game`.

How `-crow-lit vita` works:

- The game's Standard, SFBStandard and Legacy Diffuse materials draw with CrowVita/Lit (`vita_shaders/CrowLit.shader`). It is a simple form of Standard with no ForwardAdd pass.
- It draws the main directional light, the light probes' harmonics and the 4 point lights Unity gives each object per vertex. Per pixel it adds up to 4 key lights and one fill directional light. The main light's highlight uses Unity's mobile GGX.
- VitaLit picks the key lights by what they give the ground around the player. A light whose range ends more than `-crow-lit-view` metres from her is out. The rest score by intensity and range, attenuated over the distance. Spots she stands inside come first, the narrowest first, so the gun's laser sight keeps a slot.
- A key keeps its slot for up to 2 s while the game switches it off, and for half a second after it drops out. Its slot then fades out and the new key fades in. A spot light that comes on takes its slot at once.
- A key light leaves out the layers its culling mask leaves out, as the game's light does. The build saves each material's layer for this (`asset_rules.material_layers`).
- Pool has at most 2 key lights from its load until another area loads (`VitaLit.AreaKeys`). With 2, the boss fight holds 30 fps.
- The build saves the materials with CrowVita/Lit already (`CROW_LIT_BAKE=on`, the default), so no material changes shader at run time. Such a build has no Standard shader.

Depth layers keep decals from fighting the surfaces under them in the PS Vita's 16-bit depth.
Every opaque CrowVita/Lit material has `_CrowDepthLayer` -1 (`asset_rules.vita_lit_bake`).
The shader moves its depth back by 1.46e-5 d² m at distance d, which is 5.85 mm at 20 m.
Decals, writing, end caps, overlays and sprites a few millimetres in front then draw over it.
Fog and shadows keep the true depth.
`-crow-lit-debug 1` switches the layers off at run time for A/B tests.
There is no build switch for them.
The layer is too small to matter for a camera with a 0.1 m near plane, such as the story and title cameras, or any camera under `-crow-near-plane game`.
Fade and Transparent materials write no depth, so a layer can't order two of them.
`CROW_BACKING` moves opaque faces of other shaders that lie just behind a depth-layered wall further back ([build.md](build.md#build-switches)).

### Camera and post-processing

| Flag | Effect |
| --- | --- |
| `-crow-post full\|half` | The camera effect's size. Default half. |
| `-crow-camera 624\|550` | With half, the camera's own picture: 624x351 or 550x351. Default 550. Both give the same picture, since the effect's first pass reads 550 columns. Key `camera=`. |
| `-crow-post-filters all\|none\|<names>` | Which of `sfb`, `psx`, `blur` and `crt` draw, joined by `+`, or `no<name>` for all but one. A filter left out is skipped, and the next one reads the picture before it. Default all. Key `postfilters=`. |
| `-crow-post-vita on\|off` | At half size, draw the chain in three passes of `vita_shaders/CrowPost.shader` instead of the game's four. Default on. On Pool's GPU at 222 MHz it takes about 3 ms against the game chain's 11. Keys `postvita=` and `postdiscard=on\|off`. |
| `-crow-vsync <n>` | vSyncCount after the game sets 1. Default 2, for an even 30 fps where a frame fits in 33 ms. |
| `-crow-rt-cameras game\|visible\|half\|on` | The cameras that film into render textures: Arcade's four cabinet screens, Office Pike's two security feeds and Control Room's monitor. visible renders one only while something showing its texture was drawn the frame before. half renders each every other frame, the area's cameras taking turns. on does both and is the default. Key `rtcams=`. |
| `-crow-menu-cull on\|off` | While the inventory, status screen, map or pause menu covers the world, the Main Camera culls only layer 5, the UI. Default on. Key `menucull=`. |
| `-crow-near-plane <m>\|game` | A near plane of at least m metres for the cameras that draw through the camera effect with a far plane under 100 m. Default 1. The PS Vita gives their render textures about 16 bits of depth, so at the game's 0.1 m surfaces a few centimetres apart fought. The fog's start and end move to give the same fog. Key `near=`. |
| `-crow-near-fog off` | Leave the fog where it was with the new near plane, as a test. Key `nearfog=`. |
| `-crow-map-pan on\|game` | Keep the map's scroll on whole cells of UIPixelate's pixel grid, and skip its 700 or so pixelated images' Update while the map shows. Default on. Key `mappan=`. |
| `-crow-map-arrows canvas\|game` | Put the bobbing scroll arrows under a canvas of their own, so the map canvas isn't rebatched every frame. Default canvas. Key `maparrows=`. |
| `-crow-map-log` | Log the map's skipped updates and their time every 5 s. |
| `-crow-particles game\|off` | off stops and clears every particle system, to measure what they cost. Key `particles=`. |
| `-crow-particle-noise game\|low\|medium` | The noise module's quality on every particle system that has one. Default low, Unity's 1D noise instead of the game's 3D. Key `pnoise=`. |

Code in `compat/CamEffect.cs`, `compat/Cameras.cs`, `compat/MapPan.cs` and `compat/Particles.cs`.

### Memory and garbage collection

| Flag | Effect |
| --- | --- |
| `-crow-memory <s>` | Memory totals every s seconds (default 30) and 5 s after each scene load (MEMORY). |
| `-crow-memory-types` | After each load, count and size per asset type, and the largest textures and meshes (MEMTYPES, MEMTOP). |
| `-crow-memory-types-every` | The same at every MEMORY line. It grows the GC heap. |
| `-crow-memory-fsm` | PlayMaker's FSMs, states, actions and parameters (MEMFSM). |
| `-crow-memory-unload` | Unload unused assets and collect before each count. |
| `-crow-memory-watch [ms]` | Sample memory on a thread of its own every ms (default 250), also through a load's long frames. A MEMWATCH line comes when a value moves by 1 MB, and every 10 s. The last lines before an out-of-memory crash show which pool ran out. |
| `-crow-gc-retries <n>` | Collections tried before an out-of-memory abort. Default 2. Boehm's own is 0. |
| `-crow-gc-divisor <n>` | The heap grows by about a nth of itself at a time, and collects after about a nth of the live data is allocated. Default 6. Boehm's own is 3. |
| `-crow-gc-reserve <MB>` | The main memory the GC heap's cap leaves to Unity. Default 16. 0 means no cap. |
| `-crow-gc-max <MB>` | A fixed ceiling past which the heap collects instead of growing. It is a test tool: set too low, the player hangs. |
| `-crow-no-phycont-heap` | Keep the GC heap out of the PHYCONT memory. |
| `-crow-heap-sort off` | Leave the low-level allocator's free blocks unsorted. Sorting is on from the start on the PS Vita. Without it, free memory fell by about 8 MB over six Ocean Kingdom and Haunted Hilltop round trips. |
| `-crow-gc-handles <n>` | Grow IL2CPP's table of GC handles to n slots at the start. Default 131072, twice what Haunted Hilltop uses. When full, the table doubles into an array the GC heap gives unchecked, which once crashed the game. 0 leaves it to IL2CPP, the default on other players. Key `gchandles=<n>`, which grows nothing once the heap's cap is in force. |
| `-crow-gc-handles-count [s]` | s seconds (default 2) after each scene load, the handles in use per table. A count takes about 370 ms of the main thread on the PS Vita, so frame times taken with it are off. Key `gchandles=count`. |
| `-crow-gc-log [ms]` | Each collection with its pause, the time and MB since the one before, and the heap after (CROW_GC_RUN). Each frame over ms (default 200) as CROW_LONG_FRAME. IL2CPP players only. |
| `-crow-alloc [s]` | Every s seconds (default 10), the MB and KB a frame allocated and the classes that allocated most (ALLOC, ALLOCTOP). IL2CPP players only. |
| `-crow-alloc-stacks [KB]`, `-crow-alloc-file <path>` | On Windows IL2CPP players, a call stack every KB (default 16) allocated, summed into a file. `tools/downgrade/allocreport.py` reads it. |
| `-crow-alloc-clones <s>` | Every s seconds, the "(Clone)" objects made since the last report, by name (CROW_CLONES). |
| `-crow-gc-pace on\|off\|<MB>` | Collect half a second into a pause, such as the pause menu or the status screen, once MB (default 2) have been allocated since the last collection. Default on. Without it, Boehm collects in play after every 6 MB or so, a stop of up to a second wherever the game is. |
| `-crow-keep-mesh-caches` | Keep MeshCombineStudio's caches. By default the PS Vita frees them, the source meshes' CPU copies and about 7 MB of work buffers once every combiner of the scene is done. |
| `-crow-release-meshes` | Free those caches on other players too. |

Code in `compat/Memory.cs`, `compat/Alloc.cs` and `compat/MeshRelease.cs`.

### Area loads

| Flag | Effect |
| --- | --- |
| `-crow-no-gap` | Load areas straight from the old area, as the game does. By default the PS Vita loads them through an empty scene, with the old area's assets freed first. |
| `-crow-gap` | Use the empty scene on other players. |
| `-crow-gap-sweep auto\|on\|off` | The unused-asset pass in the empty scene, which takes about 2 s. auto, the PS Vita default, skips it when the area fits beside the old area's assets with 48 MB of main memory to spare. A first visit always runs it. on always runs it. off never does, for tests. CROW_GAP lines give each decision. |
| `-crow-no-prefetch` | Load the title synchronously instead of reading it while the logos play. |

Code in `compat/SceneFlow.cs`.

In the empty scene the new area loads additively.
Before the old scenes unload, the player drains the renderer for two frames and waits for the render thread's queue, so nothing the unload frees is still being drawn.
The area then becomes the active scene, with its lighting and fog.

### Audio

| Flag | Effect |
| --- | --- |
| `-crow-audio [s]` | Every s seconds (default 2), the DSP clock against the wall clock, the sources playing, clips loading, each CPU's load and each busy thread's CPU share (CROW_AUDIO, CROW_THREADS). A DSP clock that falls behind means stutter or crackle. |
| `-crow-audio-tap` | Time the mixer's blocks from inside the mixer. It stalls the mixer during collections, so use it for block timing only. |
| `-crow-audio-census <s>` | s seconds after each area load, everything that plays: each source, the totals, every reverb filter and the reverb zones (CROW_ACENSUS). |
| `-crow-audio-game` | Keep every audio filter on, as the game has them. |
| `-crow-audio-filters <types>` | The filter types to switch off: any of `reverb`, `lowpass`, `highpass`, `chorus`, `echo`, `distortion` or `all`. With `idle` among them, only while the source is idle. Default `reverb,idle`. |
| `-crow-reverb-max <n>` | At most n reverb filters on at once, the most audible first. Default 0, so no reverb plays. With even one on, FMOD's mixer went over budget in several areas as soon as other sounds started, and the sound crackled. Key `rmax=`. |
| `-crow-reverb-adapt off` | Keep the cap at n in every area, without the mixer budget. By default the cap comes down in an area where the mixer falls behind. On the PS Vita a filter also comes on only while FMOD's mixer thread has room for it under 70% of a CPU, and not while the area's clips still load. `rbudget=on\|off` switches that budget. |
| `-crow-reverb-silent off` | Give filters to sources the listener can't hear too. By default they get none. Key `rsilent=`. |
| `-crow-shared-reverb on\|off` | Sources with the same reverb settings on one mixer group share one reverb, on one of the 12 mixers in `Resources/CrowSharedReverb`. Default on, but with `-crow-reverb-max 0` no group is made. Keys `sreverb=` and `wsharemax=<n>`. |
| `-crow-workers-share off` | Keep Unity's job workers off CPU 2. By default they also run on CPU 2 while no filter has been on for a second. An explicit `-crow-gfx-workers` turns this off too. Key `wshare=`. |
| `-crow-audio-velocity dynamic\|game` | Every AudioSource and the AudioListener take their velocity once a frame instead of in every physics step. Default dynamic. The enemies have 7 to 15 sources each. |
| `-crow-idle-audio on\|off` | Disable AudioSources idle for 2 s, and enable them again before the game's code plays them. Default on. |
| `-crow-idle-audio-log` | Log each pass (CROW_IDLE_AUDIO). |
| `-crow-dsp-buffer <n>`, `-crow-real-voices <n>`, `-crow-sample-rate <hz>` | Call `AudioSettings.Reset` with these before the first scene. On the PS Vita, a 1024-sample buffer or a 32 kHz rate crashes the player. |

Code in `compat/Audio.cs`, `compat/AudioCensus.cs`, `compat/EnemyCost.cs` and `compat/IdleAudio.cs`.

Two more keys: `rzone=off|on` switches the scenes' AudioReverbZones, and `lowpass=off|on` the lowpass filters.

### Clocks and threads

| Flag | Effect |
| --- | --- |
| `-crow-cpu-mhz <n>` | The ARM clock to keep. Default 444, the highest an application may set. It only raises the clock, so PSVshell's higher clock is left alone. 0 leaves the power mode's 333. Key `cpumhz=` sets exactly that clock, lower too, at most 444. |
| `-crow-gpu-mhz <n>` | The GPU clock to keep: 41, 55, 111, 166 or 222. Default 222. 0 leaves the power mode's 166. Key `gpu=`. |
| `-crow-xbar-mhz <n>` | The GPU crossbar clock to keep, 111 or 166. Default 0, which leaves it as it is. Key `xbarmhz=`. |
| `-crow-gfx-workers <priority>:<cpus>\|game` | Set Unity's two job workers to that priority and those CPUs once they first run. The priority is decimal, 64 the highest, and the CPUs are digits. Default 64:1, the render thread's priority and CPU. With graphics jobs the workers record the camera's draws. Key `workers=`. |
| `-crow-loader-cpus <cpus>` | The CPUs of Unity's loading thread. Default 1, away from FMOD's CPU 2. `-` leaves it. |
| `-crow-load-priority <p>` | The background loading priority for the player's scene: High, Normal, BelowNormal or Low. Default Normal when the loading thread has moved, else High. |
| `-crow-thread <name>:<priority>:<cpus>` | With `-crow-audio`, set the priority and CPUs of each thread whose name has `<name>`. Write spaces as underscores. Unity's job workers are `Worker_Thread`. Repeatable. |

Code in `compat/Clock.cs`, `compat/Threads.cs` and `compat/Audio.cs`.

CrowClock sets the clocks.
It is a library inside the game package, `Media/Plugins/CrowClock.suprx`, not a system plugin.
The source is in `tools/downgrade/native/clock`, and `build` and `quickcode` put it in the package.
By default it sets the CPU to 444 MHz and the GPU to 222 MHz.
A thread checks the clocks every 2 s and sets them again after a suspend or a power mode change.
The CLOCK log lines give the clocks at start and at each change.
The bus clock is only logged, since an application can't set it.
CrowClock also reads each thread's CPU time from the kernel for the probes.

### Physics

| Flag | Effect |
| --- | --- |
| `-crow-fixed-dt <s\|fps\|game>` | The physics step. Default 30, one step per 30 fps frame. game keeps the game's 0.02 s. The game still moves as at 0.02: PlayMaker's per-step impulses are scaled, each body's drag is adjusted and bullets use continuous collision detection. Key `fixeddt=`. |
| `-crow-bullet-ccd on\|off` | off leaves the bullets on discrete collision detection, as the game has them. At the longer step they can pass through small colliders. |
| `-crow-fixed-check` | Log clones of adjusted bodies, bodies whose drags are off, and how far new bodies move. |
| `-crow-physics-cap on\|off\|<n>` | Cap `Time.maximumDeltaTime` at n thirtieths of a second. Default 2, or 66.7 ms. on means 1. off keeps the project's 0.333 s. A frame slower than the cap advances game time by the cap only, so the whole game slows for that frame, apart from sound. At 2 only hitches are clamped. Key `physcap=`. |
| `-crow-ground-cache <steps>\|off` | Keep the ground friction that the impulse scaling looks up per body for that many physics steps. Default 4. Each lookup is a raycast of 84 microseconds. Key `groundcache=`. |
| `-crow-physics <changes>` | `iter=<n>` solver iterations and `sync=0\|1` autoSyncTransforms, comma-separated. |
| `-crow-scaled-colliders game\|box\|report` | Give a mesh collider whose scale keeps changing a box of its bounds, since PhysX cooks the mesh again at every change. Station Square's swinging flags and the enemies' growing death pools are such colliders. Default box. report only logs them (CROW_SCALED). Key `scaled=`. |

Code in `compat/FixedStep.cs`, `compat/Perf.cs` and `compat/ScaledColliders.cs`.

### Merge and split

The PS Vita build merges static geometry as it writes each scene, and splits the areas' big hierarchies.
`CROW_MERGE` and `CROW_SPLIT` control that ([build.md](build.md#build-switches)).
These flags inspect the result, or do the same at run time on other players.

| Flag | Effect |
| --- | --- |
| `-crow-merge report\|on\|off\|merged` | report logs what would merge in each area (CROW_MERGE). on merges at run time, from readable meshes only. off switches the build's merged renderers off and their parts on, in a build made with `CROW_MERGE=ab`. merged switches back. Key `merge=`. |
| `-crow-merge-cell <m>`, `-crow-merge-max-vertices <n>`, `-crow-merge-max-kb <n>`, `-crow-merge-drop` | The run-time merge's grid cell (8 m), largest part (500 vertices) and budget per area (1,536 KB), and whether it drops the parts' meshes. |
| `-crow-merge-check` | Every second, log merged parts that move, switch, change material or show apart from their merged renderer (CROW_MERGE_CHECK). |
| `-crow-merge-enemies` | Merge the enemies at run time as the build does. Each enemy's parts below each moving anchor become one renderer with a submesh per material. |
| `-crow-merge-materials` | List the enemies' merged materials in the report. |
| `-crow-scene-split <mode>` | off puts the split parts back after each load, and on takes them out again. check compares each part with its old place every second (CROW_SPLIT_CHECK). report logs what the split would take, and apply splits at run time. `report-movers` and `apply-movers` include the parts that move on their own. A `-check` suffix, as in `apply-movers-check`, also checks. Keys `scenesplit=` and `splittoggle=parents-off\|parents-on\|self-off\|self-on`. |
| `-crow-split-mover-log` | Log each DOTween-moved candidate with what keeps it in, and each part's way up (CROW_SPLIT_WAY). |
| `-crow-split-follow off` | By default every part taken out follows its old parent when that moves (CROW_SPLIT_FOLLOW). off stops it, for A/B. Key `splitfollow=off`. |
| `-crow-find-movers <name> [s]` | s seconds (default 5) after each area load, log every FSM action whose GameObject parameters name an object of that name (CROW_FIND_MOVER). |
| `-crow-player-split game\|ui\|ui+camera` | Make the player's menus a hierarchy of their own, and with `camera` its camera rig too. Default ui. Mara's breathing moves about 600 transforms every frame, which then dirty a smaller hierarchy. Keys `split=ui` and `split=camera`. |
| `-crow-ui-park on\|off\|check` | 2 s after each area load, park the gameplay canvas's switched-off screens below a plain transform, since Unity's canvas update walked them twice a frame. A parked screen comes back before the game's code switches, moves or searches it. Default on. check logs each park and restore with its stack (CROW_UI_PARK). Key `uipark=`. |
| `-crow-rig-park on\|off\|check\|report` | The same for the camera rig's switched-off subtrees, parked below CrowRigPark. Default on. report logs the rig by subtree. Key `rigpark=`, which also takes `probe` as a test. |

Code in `compat/Merge.cs`, `compat/SceneSplit.cs` and `compat/UiPark.cs`.

The build also switches off small animation scripts inside rigidbodies that aren't enemies, so those bodies merge as enemies do.
That is why Pool's boss's hair, pustules, splat balls and fingers no longer wobble.

### Enemy rebuild and player merge

After the merge, the PS Vita build rebuilds the enemies (`compat/EnemyRebuild.cs`, `compat/MergeEnemies.cs`):

- `CROW_ENEMY_ATLAS=on|ab|off` bakes each enemy type's materials into one atlas per type and scene.
- `CROW_ENEMY_FLATTEN=on|ab|off` removes the enemies' bare pivots.
- `CROW_ENEMY_DEBRIS=out|off` moves each enemy's inactive debris to one root per scene. The game's first touch of a piece puts it back in place.

These three are environment variables the build reads, not entries in `BUILD_SWITCHES`.
Each defaults to `on` or `out`, and `ab` keeps the old data so the flags below can switch back at run time.
`CROW_PLAYER_MERGE` does the same for Mara and what she carries ([build.md](build.md#build-switches)).
Only builds with the PS Vita's scene data have any of this.
`CROW_VITA_DATA=1` gives a Windows test player that data.
Results are in status-log.md, under [Enemy rebuild (19 Oct)](status-log.md#enemy-rebuild-19-oct) and [Player merge (19 Oct)](status-log.md#player-merge-19-oct).

| Flag | Effect |
| --- | --- |
| `-crow-enemy-atlas on\|off` | off switches the atlased renderers back to their old meshes and materials. Needs an `ab` build. Key `eatlas=`. |
| `-crow-enemy-flatten on\|off` | off puts the enemies' pivots back. Needs an `ab` build. Key `eflatten=`. |
| `-crow-enemy-debris out\|in\|check` | in puts the debris back in place as each scene loads. check keeps it out and logs each call that brings a piece back (CROW_SPLIT_MISS). Key `edebrisout=`. |
| `-crow-enemy-rebuild-log` | CROW_ENEMY_REBUILD after each load. |
| `-crow-player-merge on\|off\|noatlas` | In a build made with `CROW_PLAYER_MERGE=ab`, off shows the parts again and noatlas shows the merge without the atlas. Key `pmerge=`. |
| `-crow-player-merge-check` | Every second, each merged part against its merged pose, activity, renderer and materials (CROW_PLAYER_MERGE_CHECK). |
| `-crow-player-shots <s> <dir> [n]` | Close-ups of Mara from four sides, merged and not, in the same frame. Needs an `ab` build. |
| `-crow-player-shots-groups` | With `-crow-player-shots`, also each merged renderer alone against its parts. |
| `-crow-player-merge-report <s>` | The player's renderers, drawn or not, with their anchors and what keeps them out, and an estimate of the draws a merge would leave. |
| `-crow-player-merge-dry <dir>` | The build's analysis of the player's scene as it loads, with the uses in dir (`work/player-uses`). |
| `-crow-player-tree` | After each area load, the player's hierarchy by subtree size, and what the split would take out of it (CROW_PLAYER_TREE). |

Code in `compat/EnemyRebuild.cs` and `compat/PlayerMerge.cs`.

### Script costs

| Flag | Effect |
| --- | --- |
| `-crow-decor on\|off` | Decoration is anything with a renderer below it and no collider, light, camera or rigidbody, outside the UI, the player and the enemies. Its DOTween tweens and the game's animation scripts run every frame while it was drawn the frame before. Otherwise they run on one frame in `-crow-decor-hidden`, with the skipped time added, so they end when they would have. Default on. Key `decor=`. |
| `-crow-decor-hidden <n>` | 2 or 4. Default 4. Key `decorhidden=`. |
| `-crow-decor-far <m>`, `-crow-decor-far-rate <n>` | Visible decoration farther than m metres runs on one frame in n (default 2). Default 0 m, which leaves visible decoration alone. Keys `decorfar=` and `decorfarrate=`. |
| `-crow-decor-visible <n>` | Visible decoration runs on one frame in n. Default 2 on the PS Vita, 1 elsewhere. Key `decorvisible=`. |
| `-crow-decor-log` | CROW_DECOR every 10 s. |
| `-crow-fsm-trim on\|off\|check` | Skip PlayMaker FSM updates that would do nothing. Default on. check runs every update it would skip and compares the result with the prediction (CROW_FSM_TRIM_CHECK). Keys `fsmtrim=`, and `fsmupdate=`, `fsmfixed=` and `fsmlate=` with on or off. |
| `-crow-fsm-trim-log` | Every 10 s, the updates per frame run and skipped (CROW_FSM_TRIM), and the enabled FSMs by what their state holds (CROW_FSM_TRIM_CENSUS). The census takes about 0.4 s on the PS Vita. |
| `-crow-fsm-compile on\|off\|check` | Run PlayMaker's methods with its small methods inlined, which the rewriter does since SNC can't. Each changed method keeps its original body, chosen at run time. Default on. check works on Windows builds only. Key `fsmcompile=`. |
| `-crow-fsm-compile-log` | Every 10 s, the mode and, in check mode, the checks. |
| `-crow-fast-property game\|check` | PlayMaker's Set Property and Get Property use delegates bound to the target's accessors instead of reflection. On everywhere. game goes back to reflection. check does both and logs differences (CROW_FAST_CHECK). Key `fastprop=`. |
| `-crow-fast-names game` | Every `Object.name` read and write in the game's assemblies goes through a per-object cache. On everywhere. Key `names=`. |
| `-crow-lit-camera game` | VitaLit keeps the main camera instead of asking `Camera.main` at each key-light choice. On everywhere. Key `litcam=`. |
| `-crow-script-cost game` | Keep the per-frame PlayMaker and fade work the port skips, for A/B runs. This also brings back the dead Blobs' trail exceptions. |
| `-crow-alloc-share game` | Make delegates and DOTween's shortcut closures new at each call, as the game does. By default every player makes them once and reuses them. Key `share=`. |
| `-crow-tween-recycle on\|game` | Make PlayMaker's DOTween actions' tweens recyclable, so DOTween's pool hands completed tweens out again. Default on. |

Code in `compat/Decor.cs`, `compat/FsmTrim.cs`, `compat/FsmCompile.cs`, `compat/FastPaths.cs`, `compat/ScriptCost.cs` and `compat/Alloc.cs`.

`splitfilter=on|off` switches EnemySplit's filter in front of its lookups, which is on everywhere (`compat/FsmCompile.cs`).

### Fight cuts

These cuts remove or slow down work in a fight frame.
All are on by default on the PS Vita.
`cuts=off|on` sets them all at once, together with `groundcache=` and `decorvisible=`.
Measured gains are in status-log.md, under [Fight cuts (18 Oct)](status-log.md#fight-cuts-18-oct).

| Flag | Key | Default | Effect |
| --- | --- | --- | --- |
| `-crow-blob-trails on\|off` | `trails=` | off | A Blob's slime trail spawns no pieces. The pieces have no collider or FSM, so only the look changes. |
| `-crow-enemy-freeze on\|culled\|off` | `freeze=` | on | Freeze an enemy that its own Pause/Cull FSM has held for 10 frames. Its FSMs' updates are skipped and its tweens and animation scripts get no time. It still takes events, and it wakes the moment any of its FSMs changes state. culled leaves out enemies held only by the pause. |
| `-crow-enemy-anim full\|half\|off` | `eanim=` | half | The game's animation scripts on enemy parts without a collider run every other frame with two frames' time. off holds them. |
| `-crow-enemy-tweens full\|half` | `etweens=` | half | The same for DOTween tweens on those parts. |
| `-crow-breath on\|half\|off` | `breath=` | half | Mara's breathing and idle tweens run every other frame. off holds them. |
| `-crow-flicker full\|half` | `flicker=` | half | The "light flicker" FSMs and LightFlicker scripts run every other frame. |
| `-crow-props-rate <n>`, `-crow-props <names>` | `props=` | 4, `bunting` | FSMs and tweens below objects of these names run on one frame in n, on screen too. The default names Station Square's 18 swinging flags. |
| `-crow-blood-stains <n>\|game` | `stains=` | 8 | Keep at most n of Mara's blood stains. The oldest go. |
| `-crow-burst <f>\|game` | `burst=` | 0.5 | PlayMaker's ParticleSystemEmit bursts emit f of their count. |
| `-crow-same-pose on\|game` | `samepose=` | on | PlayMaker's SetPosition and SetRotation skip a transform that already has the value. |
| `-crow-rewired-axes <n>\|game` | `rwaxes=` | 8 | Rewired reads only the first n of each joystick's 29 Unity axes. Each `Input.GetAxis` by name costs about 12 microseconds on the PS Vita. |
| `-crow-rewired-mouse on\|off` | `rwmouse=` | off | Rewired's own mouse polling. |
| `-crow-rewired-joysticks first\|all` | `rwjoys=` | first | Of the four joysticks Unity reports, keep only the first with a name. |
| `-crow-cuts-log` | | | Every 10 s, CROW_CUTS with the frozen enemies, skipped updates, wakes by cause and Rewired's InputManager time. Also a line for each blood stain removed. |

While the pad reader serves, which is the default, the Rewired cuts apply only to the frames where Rewired answers.

### Pad and Rewired

| Flag | Effect |
| --- | --- |
| `-crow-pad reader\|rewired\|compare` | reader serves player 0's actions from CrowCompat's own reader of the pad, and Rewired's InputManager skips its updates. Default reader. Rewired still answers in the first frames after each area's Rewired start, for names it doesn't know and for other players. compare lets Rewired serve and compares every action with the reader's each frame (CROW_PAD_DIFF). Key `pad=`. |
| `-crow-pad-press "<t>:<inputs>[:<s>];..."` | Synthetic input for the reader, for Windows tests, t seconds after the start and held s seconds (default 0.15). Inputs are `cross`, `circle`, `square`, `triangle`, `l`, `r`, `select`, `start`, `up`, `down`, `left` and `right` joined by `+`, `lx=`, `ly=`, `rx=` and `ry=` as axis values, and `shot` for a screenshot into `-crow-pad-shots <folder>`. On the console, use `vita.py do "press ..."`. |
| `-crow-pad-press-from <scene> [each]` | Start the presses when that scene first loads. With `each`, start them again at every load of it. |
| `-crow-pad-log` | CROW_PAD every 10 s with the mode, whether the reader serves, its time a frame and its rebuilds, and each table it builds. |
| `-crow-rewired game\|pad` | pad switches Rewired's keyboard and mouse controllers off. Default pad, since the PS Vita has neither. Key `rewired=`. |

Code in `compat/Pad.cs`.

### Console behaviour

| Flag | Effect |
| --- | --- |
| `-crow-console <0-3>` | The PlayMaker global `Control Mode Console`. Default 1: PlayStation prompts, no keyboard or other pad sought, no Quit on the title and no Screen entry on the Graphics page. The game's own value is 0. |
| `-crow-platform-anim win\|switch` | The platform flag PlatformSpecificAnimation follows. Default switch, so the infected crows' skin blobs keep their scale animations off and their meshes combined, as on the Switch. |
| `-crow-culture invariant\|system\|<name>` | The culture numbers and dates use. Default invariant, so a console set to Russian still writes `71.33` in the log the tools read. system keeps the language's culture. A name such as `ru-RU` sets that one, for tests. The Load Game slots' date follows it. |

Code in `compat/Settings.cs`.

## Fight benchmark

`tools/downgrade/fightbench.py` runs the scripted fight (`-crow-fight`, `compat/Fight.cs`) and prints where the frame goes.

```powershell
uv run tools/downgrade/fightbench.py vita --modes real,stress --runs 3 -- -crow-mute   # the console
uv run tools/downgrade/fightbench.py pc --modes real,stress --runs 1                  # a Windows player
uv run tools/downgrade/fightbench.py pc --vitalike --timeout 1200                     # the same at Vita-like CPU speed
uv run tools/downgrade/fightbench.py report <tag>                                     # the table again from saved logs
```

`vita` takes the `vita-device` lock, waiting up to `--wait` seconds for it (0 by default).
Inside `lock.py run vita-device` it uses the caller's lock.
It syncs the build unless you pass `--no-sync`.
For each mode and run it quits the game, writes `args.txt`, launches, keeps the console awake and waits for the summary line, up to `--timeout` seconds (420 by default).
It then pulls the log, and samples vitacompanion's `perf` during the fight.
A run stops early if no log naming its flags appears within 2 minutes.
However it ends, it quits the game and clears `args.txt`.
A run takes about 3 minutes.
That is about a minute to reach the area, then 12 s of settling, 6 s of fighting and 60 s measured.

`pc` runs a copy of the Windows player (`builds/crow-2018.2.19f1`, or `--build`) through `run_player.py`, on its own desktop, windowed and muted.
For a Mono build the copy gets the current CrowCompat, compiled into `work/fightbench/compat`, so you don't need a new build.
The copy leaves out Steam's `steam_api` library and `steam_appid.txt`.
`--vitalike [CAP]` runs that player at CAP percent of the wall time (5.5 by default) on three cores, using `vitalike.py`.
Its calibration was made for the IL2CPP player, so with a Mono build the numbers are only a rough guide.

`--area`, `--seconds` and `--tag` set the area, the measured seconds and the output folder's name.
`--input <chain>` sends a vitacompanion chain once in the second window, for walk mode.
Extra `-crow-*` flags go after `--`, such as `-crow-fight-area "Ocean Kingdom"` or `-crow-shot 40 <png>`.
Logs, perf samples and `summary.txt` go to `artifacts/fightbench/<tag>/`.
The tag defaults to the date and time.

The table has one row per run and a mean per mode.
Its columns are in milliseconds per frame unless they say otherwise:

| Column | What |
| --- | --- |
| frame, p50, p95, p99, worst, fps | Frame time over the measured windows, read from the wall clock. |
| main | The main thread's own run time per frame, on the PS Vita. For the rest of the frame it waits for the render thread, the GPU or the display. |
| scripts, phys, anim, skin, part, audio, render, ui, wait | The player loop by subsystem, from `-crow-loop-profile`. render includes culling and draw submission, and also waits when the render thread or the GPU is behind. wait is present, frame start and target-rate waits. Each window's `top=` names the costliest steps, and its CROW_LOOP and CROW_CAMS lines (`cycle=fight-wN`) have the detail. |
| gc, gcms, KB/s | Collections, their pause and the allocation rate, on IL2CPP players. |
| steps, rate | Physics steps a frame, and game seconds per wall second. rate drops under 1 when `-crow-physics-cap` clamps slow frames. |
| alive, shots, kills, hits, shotk, freeMB | Enemies alive on average, shots fired, kills (the `Kills` global), health drops from shots, kills by shots, and the lowest free main memory on the PS Vita. |

### Modes

The modes are `real`, `stress`, `idle`, `stand`, `walk` and `discover`.

- `real` runs the area's own fight. In Station Square, the default, that is the 9 enemies of a later visit: 6 Blobs, 2 Grinners and a Spindle. 7 of them stand within 5 m of Mara's spot by the area's entry. As they die the fight winds down, and by the last window about 3 are left, out of reach.
- `stress` adds 12 copies of the area's enemies in a ring around Mara and replaces the dead from 12 spares, which keeps about 20 alive.
- `idle` is the same setup with no enemies and no shots. Mara aims with the laser on. It is the baseline.
- `stand` loads the area as the game does and leaves Mara where it puts her, without aiming. It gives the plain standing frame rate.
- `walk` puts Mara on the spot without enemies or aim, and logs her position every 0.1 s of game time while `fightbench.py --input` walks her with the pad.
- `discover` logs the area's enemies and quits.

In `real`, `stress` and `idle`, Mara stands on a fixed spot, can't be hurt, and has the handgun with the laser sight and a full box.
The camera is locked.
In `real` and `stress` she fires at the nearest awake enemy every 0.7 s through the combat FSM.
Every 8 s (6 in stress) one enemy dies through its own damage FSM, the way a bullet kills it.
Enemies cull themselves beyond 5 m of the camera's visibility point, so only those near Mara cost anything.
UnityEngine.Random is seeded, but physics and frame timing still differ between runs, so compare means over several runs.
Other areas with many enemies: Ocean Kingdom (12 with its globals), Haunted Hilltop (10), Underground SW and Pre Machine Centre (6 or 7 on a first visit).

After the settle and engage time, the player logs a CROW_FIGHT_WINDOW line every 10 s for 60 s, then CROW_FIGHT_SUMMARY, and quits.
A window line has the frame time and its percentiles, the main thread's time, the loop by subsystem, collections and allocations, enemies alive and awake, shots, kills and the lowest free memory.
Its `near=` field lists each awake enemy's type, distance and height from Mara, and renderers on and drawn, as in `Lumber:3.0:0.23:41/34`.
CROW_FIGHT_TYPE gives each enemy type's make-up at the setup.

### Fight flags

| Flag | Effect |
| --- | --- |
| `-crow-fight <mode>` | Run the benchmark in that mode. |
| `-crow-fight-area <scene>` | The area. Default Station Square. |
| `-crow-fight-spot x,y,z,yaw[,camyaw]` | Where Mara stands, her facing and the camera's. By default the area's entry in `Areas` in `Fight.cs`, else near the biggest group of enemies. |
| `-crow-fight-extra <n>`, `-crow-fight-pool <n>` | Stress mode's copies (12), and the spares that replace dead ones, one a second at most (12). |
| `-crow-fight-seconds <s>`, `-crow-fight-window <s>` | The measured time (60), in windows of 10 s. |
| `-crow-fight-settle <s>`, `-crow-fight-engage <s>` | Seconds from the load to the setup (12), then seconds of fighting before the first window (6). |
| `-crow-fight-fire <s>` | Seconds between shots. Default 0.7. |
| `-crow-fight-kill <s>` | Every s seconds the target dies through its own damage FSM. Default 8, or 6 in stress. 0 means none. |
| `-crow-fight-kill-mode explosion` | Give the grenade's death instead, where a Grinner blows apart into 22 dynamic pieces. |
| `-crow-fight-aim off` | No aim and no shots. The enemies are awake and the pad is free for the menus. |
| `-crow-fight-globals "<name>=<value>;..."` | More PlayMaker globals set before the load. `"item: map=1;MapFF=true"` gives the map. |
| `-crow-fight-cycle <sets>` | A change set per window, in turn. `-` means none. With `-crow-fight-kill 0 -crow-fight-fire 1000` the fight holds steady, so one launch is an A/B test. |
| `-crow-fight-moves "t:x,y,z,yaw[,camyaw];..."` | t seconds after the fight starts, move Mara and the camera to another spot and pin them there (CROW_FIGHT_MOVE). 2 s later CROW_FIGHT_MOVED logs each enemy's Pause/Cull state. Enemies near the new spot wake, and those at the old one cull. For Fairy Forest: `"14:0,0,22,0;26:0,0,5,0;38:0,0,12,0"`. |
| `-crow-fight-hold <type>:x,z` | Hold every enemy of that type at x,z on the ground, so it walks towards Mara at a fixed distance. Ocean Kingdom's Lumber 3 m in front of her: `Lumber:0.4,3.8` with the spot `-2.2,0,2.3,60,60`. |
| `-crow-fight-log` | Log the first 40 shots with their target, aim and the target's health. |
| `-crow-fight-run <name>` | Ignored. fightbench.py adds it so each run's log head differs from the last run's. |

### The final boss

The boss fight starts when Mara talks to Edward Crow with `NPC7` at 7 or more.
That turns on `level/code/Event: boss sequence (Crow goes in Pool)`, which plays the rest.
Crow sinks into the pool, Mara is put at the pool's edge, the roots come down, and the boss rises about 30 s later.
So a launch needs no save.
`-crow-activate "Event: boss sequence (Crow goes in Pool)" 4` switches the sequence on 4 s after the area loads.
The idle mode keeps Mara unhurt and aiming at the boss from where the sequence put her, while the boss attacks.
This run measures from 45 s after the load, six windows at vsync 2 and four uncapped:

```powershell
uv run tools/downgrade/fightbench.py vita --modes idle --runs 1 --area Pool --timeout 540 -- -crow-mute -crow-frame-probe -crow-fight-settle 45 -crow-fight-seconds 100 -crow-fight-kill 0 -crow-activate "Event: boss sequence (Crow goes in Pool)" 4 -crow-fight-cycle "vsync=2/vsync=2/vsync=2/vsync=2/vsync=2/vsync=2/vsync=0/vsync=0/vsync=0/vsync=0"
```

Expect about 33.4 ms a frame (30 fps) at vsync 2, and 29 to 31 ms uncapped.
Pool before the boss also runs at 30 fps.
Two of the reasons are `CROW_STACK_FOLD`, which draws the pool's stacked edge rings once ([build.md](build.md#build-switches)), and the 2-key cap.
To measure it, stand at the pool's rim with `-crow-fight idle -crow-fight-area Pool -crow-fight-spot 0,0.01,7.19,0`.
The history is in [status-log.md](status-log.md), in the entries Pool's boss fight (30 Oct, BOSS) and Pool at 30 fps before the boss and in the fight (31 Oct, POOLROCKS, POOLFIGHT).

## Crash dumps

When the player crashes, the console writes a dump to `ux0:data`.
`vita.py crashes` lists the dumps.
`vita.py crashes --pull` fetches the newest into `artifacts/vita/` and prints the crashed thread's call chain.
It reads the dump with [vita-parse-core](https://github.com/xyzz/vita-parse-core), which it downloads into `tools/external` on first use.
Names come from the SDK's `psp2bin` and the build's `SymbolFiles`.
`--thread main` prints the main thread instead, and `--dump FILE` reads a dump already on the PC.

`sync`, `install` and `seed` keep a copy of the game module's symbols for each build they send, in `artifacts/vita/symbols/<fingerprint>`.
They keep the last 5, about 55 MB each.
`crashes` uses the copy whose fingerprint the dump lists, so a dump still reads right after the next `quickcode`.
Without a copy it maps the dump's game addresses onto the current build's symbols through the two modules' exception indexes, and marks those frames `game~`.
The engine's frames are always right.

A frame that takes seconds or minutes doesn't crash, and the console has no sampling profiler.
`-crow-stall-dump 15` makes the player crash once a frame has taken 15 seconds, so the dump holds the main thread's stack in the middle of it.
`-crow-stall-after <t>` skips stalls in the first t seconds.
The watcher checks every quarter of the limit, so a fraction of a second works too: `-crow-stall-dump 0.1` catches a 150 ms hitch.

```powershell
uv run tools/downgrade/vita.py args -crow-stall-dump 15
uv run tools/downgrade/vita.py launch
uv run tools/downgrade/vita.py crashes --pull --thread main
uv run tools/downgrade/vita.py args              # clears the flags: args.txt stays until then
```

## IL2CPP settings

The player's code is compiled as the PS Vita module sets it up.
This was read from the build log's IL2CPP command line, `UnityEditor.PSP2.Extensions.dll` and `PSP2Il2CppPlugin.dll`.

| Setting | Value |
| --- | --- |
| IL2CPP configuration | `--configuration=Release`. The PSP2 toolchain (SNC) only tells Debug apart, so Master would compile the same. 2018.2 has no player setting for it. |
| C++ compiler | `psp2snc -O3 -Xthumb=1 -Xc+=exceptions -Xstd=cpp11 -fno-strict-overflow`, with `NDEBUG`. -O3 is SNC's highest level. SNC has no link-time optimization, so no call between IL2CPP's `Bulk_*.cpp` files is inlined. Every property getter and `List<T>.Count` is a real call. Thumb-2 keeps the 55 to 58 MB module small. C++ exceptions carry managed ones. |
| Linker | `psp2ld --strip-unused-data --strip-duplicates`. |
| Development build, script debugging | Off. `pipeline.py build --development` makes a development player. |
| Null and array bounds checks | On, with `--emit-null-checks --enable-array-bounds-check` from `unity/DowngradeBuild.cs`. A null reference or bad index the PC game logs as an exception then doesn't crash the console. Without them Station Square's fight saved under 1 ms of scripts and the module 2.6 MB, so they stay. `Il2CppSetOptionAttribute` could drop them per method where nothing can be null. |
| Stack traces | Native. Every log type has ScriptOnly stack traces. The game logs about one line a minute in play, so this costs nothing in frames. |
| Graphics jobs | On. Unity's two job workers record the camera's draws beside the render thread. Standing in Station Square without vsync, the main thread went from 24.9 to 22.1 ms. The 2018.2 PS Vita editor has no setting for them, so `build` and `quickcode` write `gfx-enable-gfx-jobs=1` into `Media/boot.config`. `CROW_GFX_JOBS=off` turns them off. |
| Stripping | UnityLinker runs on every assembly but those `link.xml` keeps whole. It keeps the game's assemblies, and `PLATFORM_LINKED` leaves seven plugins to it. It also keeps whole the engine types that PlayMaker's Set and Get Property actions name in the scenes' FSMs (`asset_rules.link_xml`) and those only CrowCompat's reflection reaches (`LINK_PRESERVE`). `stripEngineCode` doesn't apply, since the PS Vita player is prebuilt. |

## Code changes without a full build

A full PS Vita build runs `project` and `build`, which take 15 to 30 minutes ([build.md](build.md#time-and-disk-space)).
For a change to the rewriter or CrowCompat, `quickcode` takes about 2 minutes instead.
It rewrites the assemblies and replays the last full build's UnityLinker and IL2CPP steps on the inputs that build saved in `work/vita-code-stage`.
Then it puts the new `il2CppAssemblies.suprx` and `global-metadata.dat` in `builds/crow-2018.2.19f1-vita`.
`vita.py sync` uploads the two files.

```powershell
uv run tools/downgrade/lock.py run vita-build "me: what for" --wait 1800 -- uv run tools/downgrade/pipeline.py quickcode
uv run tools/downgrade/vita.py run
uv run tools/downgrade/vita.py log --tail 40
```

The time `lock.py` waits for a lock counts against whatever limits the caller's own run.
An agent's background command stopped at 2 hours stops `lock.py` and its command with it, in the middle of a build or a console transfer.
So keep `--wait` plus the command's time well inside that limit.
To queue for longer, wait in short steps without holding anything, then take the lock without waiting:

1. `lock.py wait vita-build --wait 540` exits 0 once the lock is free, or 75 if it isn't.
2. `lock.py run vita-build ... --wait 0 -- ...` takes it. If another job took it first, this exits 75, and you go back to step 1.

`lock.py status` says who holds each lock and for how long.

The build's scenes and assets keep the scripts' fields as they were built.
So `quickcode` stops when a type the data serializes changed, such as a field, a new MonoBehaviour or a new ScriptableObject.
It also stops when a `[RuntimeInitializeOnLoadMethod]` came or went.
It warns when the asset rules or the build script changed since the full build.
Those changes need `project` and `build` again.
`--force` tries anyway.

For changes to assets, `pipeline.py build --scenes "Title,Station Square"` builds the first scene (Logos) and the named ones into `builds/crow-2018.2.19f1-vita-scenes`, leaving the full build alone.
Install it with `vita.py --build builds/crow-2018.2.19f1-vita-scenes install`.
The game loads scenes by name, so loading a scene left out fails.
