<img src="assets/icon.svg" width="64" height="64" alt="">

# Crow Country Vita

[Crow Country](https://store.steampowered.com/app/1996010/) on a PS Vita. A pipeline takes the PC release (Unity 2022.3.49f1) and rebuilds it as a Unity 2018.2 project, since 2018.2 is the last Unity with a Vita build module, then builds that for the Vita. It follows [The Drifter's port](https://github.com/xob0t/the-drifter-vita) and reuses its tools where they fit.

There are no game files in this repository. You bring your own copy, and the pipeline only reads it.

## Where it stands

Early. The pipeline builds the game for Windows (Mono and IL2CPP) and the PS Vita from Unity 2018.2, and packages a VPK. On Windows it boots, loads areas and draws as the original does. The VPK hasn't been tested on a console yet. [docs/status.md](docs/status.md) has the details, the measurements and the plan.

## Building

With Crow Country installed from Steam and [uv](https://docs.astral.sh/uv/), in the repository:

```powershell
uv run build.py              # set up, then build builds/crow-2018.2.19f1-vita.vpk
uv run build.py --windows    # or a Windows player
```

`build.py` runs `tools/setup.py`, which installs the Unity 2018.2.19f1 editor and its modules, AssetRipper, the .NET 8 SDK and Sony's PS Vita SDK (skipping what is there), then the pipeline's stages in order. `uv run tools/setup.py doctor` checks the setup and changes nothing. [docs/build.md](docs/build.md) has the requirements, the Unity license, times and disk space, and what to do when a stage fails; [docs/vita.md](docs/vita.md) covers installing the VPK and working with the console.

## Running the pipeline

The pipeline needs the same tools as The Drifter's: uv, the Unity 2018.2.19f1 editor with its PS Vita module, Sony's PS Vita SDK, AssetRipper 2.0.0, a .NET 8 SDK and Mono.Cecil. `uv run tools/setup.py` installs what is missing into `tools/external` and `uv run tools/setup.py doctor` checks it all; [docs/build.md](docs/build.md) has the details.

```powershell
uv run tools/downgrade/pipeline.py rip                       # AssetRipper export, about 4 minutes
uv run tools/downgrade/pipeline.py code                      # rewrite the game's assemblies, list what fails
uv run tools/downgrade/pipeline.py link --platform vita      # Unity's linker and IL2CPP's conversion on it, in a minute
uv run tools/downgrade/pipeline.py project --platform vita   # assemble work/crow-2018.2.19f1-vita
uv run tools/downgrade/pipeline.py build --platform vita     # import and build with the Unity editor
uv run tools/downgrade/pipeline.py package                   # builds/crow-2018.2.19f1-vita.vpk
```

For Windows players, `project` and `build` take `--platform windows` (and `build` `--backend il2cpp`). `loadcheck` loads every rewritten type and compiles every method under 2018's Mono, in a copy of the Windows player. The players take `-crow-*` flags for measurements (`tools/downgrade/compat/Diag.cs`), and `tools/downgrade/vitalike.py` runs the Windows IL2CPP player at about the PS Vita's CPU speed.

For quicker rounds on the PS Vita:

```powershell
uv run tools/downgrade/pipeline.py quickcode                 # code changes only: a new IL2CPP module in the last build, in about 2 minutes
uv run tools/downgrade/vita.py sync                          # upload what changed to the console
uv run tools/downgrade/pipeline.py build --scenes "Title,Station Square"   # a test build of a few scenes
uv run tools/downgrade/vita.py --build builds/crow-2018.2.19f1-vita-scenes install
```

`quickcode` rewrites the assemblies and replays the last full build's UnityLinker and IL2CPP steps, without the editor. The build's scenes and assets keep the scripts' fields as they were, so it stops when a type the data serializes changed (a field, a new MonoBehaviour or ScriptableObject) or a `[RuntimeInitializeOnLoadMethod]` came or went, and warns when the asset rules or the build script changed since that build: those need `project` and `build` (`--force` tries anyway). `build --scenes` builds the first scene (Logos) and the named ones only, into a folder of its own (`builds/crow-2018.2.19f1-vita-scenes`, the scenes listed beside it), and leaves the full build, its log and what `quickcode` needs alone. It's for tests: the game loads scenes by name, so loading one that was left out fails. `build --development` makes Unity's development player.

`project` keeps what it read from the export in `work/scan-cache` and leaves unchanged files with their dates, so the editor imports only what changed; a rerun takes a minute or two. Every stage writes in `work/`, `builds/` or `artifacts/`; when several jobs share the machine, run them under a lock, one pipeline at a time (`tools/downgrade/lock.py` has the details):

```powershell
uv run tools/downgrade/lock.py run vita-build "me: what for" --wait 7200 -- uv run tools/downgrade/pipeline.py quickcode
```

`--game <folder>` reads the game from another folder (or set `CROW_GAME`). The console's address comes from `CROW_VITA_HOST`, or `DRIFTER_VITA_HOST` if you set that up for The Drifter.

## License

The pipeline is MIT-licensed ([LICENSE](LICENSE)). The USCSandbox patches in `tools/downgrade/patches` are GPL-3.0, like the code they change.
