# Building

The pipeline turns your installed copy of Crow Country into a Unity 2018.2 project and builds a PS Vita app or a Windows player from it with the Unity editor. It reads the game and never changes it. Everything it makes goes to `work/` (the export and the projects), `builds/` (players and the VPK) and `artifacts/` (logs).

The steps are for Windows 10 or 11, x64. In short, with uv installed:

```powershell
uv run build.py                 # the PS Vita app: builds/crow-2018.2.19f1-vita.vpk
uv run build.py --windows       # a Windows player: builds/crow-2018.2.19f1/CrowCountry.exe
```

`build.py` runs the setup for the targets (step 1), then the pipeline's stages in order (step 2). Run it again after a failure or a change: setup skips what is installed, `rip` is skipped while its export is current, and `project` and the editor only redo what changed.

## 1. Set up

1. Install Crow Country from Steam, updated to the current release, and uv:

   ```powershell
   powershell -ExecutionPolicy ByPass -c "irm https://astral.sh/uv/install.ps1 | iex"
   ```

   Open a new terminal afterwards, so it finds uv.

2. Clone the repository. In its folder, run the setup script for your targets:

   ```powershell
   uv run tools/setup.py                 # Windows player (Mono)
   uv run tools/setup.py --vita          # PS Vita app
   uv run tools/setup.py --vita --il2cpp # both, and Windows IL2CPP players
   ```

3. Check that everything is in place:

   ```powershell
   uv run tools/setup.py doctor --target vita    # or windows, il2cpp; all three without --target
   ```

The setup script installs what is missing and skips what is already there, so you can run it again at any time. It:

- runs `uv sync` for the Python packages;
- installs the Unity 2018.2.19f1 editor from Unity's CDN into `%LOCALAPPDATA%\Unity\Editors\2018.2.19f1`, with `--vita` its PS Vita module and with `--il2cpp` its Windows IL2CPP module;
- downloads AssetRipper 2.0.0 into `tools/external/assetripper-2.0.0`, the .NET 8 SDK (8.0.425) into `tools/external/dotnet`, and Mono.Cecil 0.10.0-beta6 from NuGet;
- finds the game in your Steam libraries, or else a GOG install, and offers to set `CROW_GAME` if it is not in Steam's default folder;
- with `--vita`, installs Sony's PS Vita SDK (downloaded from archive.org, or your copy with `--vita-sdk`), checks it and sets `SCE_PSP2_SDK_DIR` for your user, then installs the SDK's Target Manager Server and Publishing Tools, which the editor's PS Vita build runs, and sets `SCE_ROOT_DIR`;
- removes the editor's license checks with unigate, then starts it once in batch mode to check that it runs (see [The Unity license](#the-unity-license));
- ends with the doctor's checklist (see [Checking the setup](#checking-the-setup)).

It checks every download against a pinned hash: SHA-256, SHA-512 for the .NET SDK (the hash Microsoft publishes), and SHA-1 for the PS Vita SDK's files, the only hash archive.org lists for them. It runs a Unity installer only if the installer's Authenticode signature is Unity Technologies'. Installers that need administrator rights ask for them.

| Option | Effect |
| --- | --- |
| `--il2cpp` | Also install the editor's Windows IL2CPP module. An IL2CPP player also needs Visual Studio 2022 Build Tools with "Desktop development with C++"; the doctor prints the command. |
| `--vita` | Also install the PS Vita module, the SDK (one already on the machine, or else SDK 3.570 from archive.org), Target Manager Server and Publishing Tools. |
| `--vita-sdk <path>` | Your own PS Vita SDK instead of the download, as an archive (`.zip`, `.7z`, ...) or a folder. Implies `--vita`. |
| `--vita-sdk-dir <dir>` | Where to extract the archive (default `C:\vitasdk`) or copy the folder. Without it, a folder is used in place. |
| `--vita-host <address>` | Set `CROW_VITA_HOST`, the console's address, for `vita.py`. |
| `--game <folder>` | Set `CROW_GAME` to this copy of the game. |
| `--yes` | Accept the offers: set a variable for a game or SDK the script found, run the SDK's tool installers. |
| `--dry-run` | Report what the script would do, and change nothing. |
| `--keep-downloads` | Keep the installers and archives in `work/installers`. |
| `--no-unigate` | Keep the editor's license checks, for an editor with a license of its own. `build.py` takes it too. |
| `--skip-license-check` | Don't start the editor at the end. |

`uv run tools/setup.py download editor il2cpp vita assetripper dotnet cecil --to <dir>` only downloads and checks the installers, for example to install them on another machine.

### The PS Vita SDK

With `--vita` and no SDK on the machine (none in `SCE_PSP2_SDK_DIR`, `C:\vitasdk` or the SCE folder), the script downloads `SDK Software/PSVITA.zip` (350 MB) from archive.org's [copy of the SDK 3.570 DevNet release](https://archive.org/details/psvita-sdk-3.570-devnet-full) and extracts it to `C:\vitasdk`; the SDK is then `C:\vitasdk\PSVITA\sdk`. That SDK is 3.570.011. The script brings it to 3.570.071 with Sony's two patches from the same item: the shader compiler `psp2cgc` (3.570.021), then `libc` (3.570.071). To use your own copy instead, pass it with `--vita-sdk`. The script extracts an archive with 7-Zip if it is installed, or else with Windows' own `tar`, which reads `.zip`, `.7z` and `.rar`. It then finds the SDK folder inside (the one with `host_tools` and `target`) and checks:

- the version in `target\include\sdk_version.h`: 3.570 or later;
- the tools Unity looks for before a build: `psp2snc`, `psp2ld`, `arm-eabi-as`, `psp2snarl` and `psp2bin` in `host_tools\build\bin`, and `psp2psarc` in `host_tools\bin`;
- Target Manager Server's `psp2ctrl` and `psp2run` and Publishing Tools' `psp2pubcmd`, in `PSP2\Tools` under `SCE_ROOT_DIR`, usually `C:\Program Files (x86)\SCE`. The editor's build looks for all three, and writes the app's `param.sfo` with `psp2pubcmd`. If they are missing, the script offers to run their installers (`TMServer-*.msi` and `PublishingTools-*.msi`) from the SDK folder, or downloads them from the same archive.org item when the folder lacks them. Publishing Tools needs .NET Framework 3.5, which a new Windows has turned off; the script turns it on first.

It sets `SCE_PSP2_SDK_DIR` and `SCE_ROOT_DIR` for your user and tells Windows, so new shells see them, Git Bash included. Shells that were already open don't, but the tools read these two, `CROW_GAME` and `CROW_VITA_HOST`, from the registry when their shell lacks them.

### The Unity license

Every build here is the editor's, so it has to start in batch mode with the build targets you need, and Unity hides the PS Vita target unless the license includes console access. So the setup script patches the editor with [unigate](https://github.com/xob0t/unigate), which removes the license checks of 2017.x to 2018.2.x editors: it downloads `unigate.py` at a pinned commit (SHA-256 checked) into `tools/external/unigate` and runs it on `Unity.exe`, which keeps the original beside it (`Unity.exe<date>.orig.bak`). An editor already patched is left alone. Close Unity first: a running editor's `Unity.exe` can't be written, and setup stops with a note to run it again. With a license of your own, pass `--no-unigate` and activate it in the editor. To undo the patch, with Unity closed:

```powershell
uv run tools/external/unigate/unigate.py --editor "$env:LOCALAPPDATA\Unity\Editors\2018.2.19f1\Editor" --restore
```

The setup script starts the editor once with an empty project in a temporary folder and reads its log; with `--vita` it also checks that the editor loads its PS Vita module. It remembers a pass in `work/license-check.ok` until the editor changes. To check again, run `uv run tools/setup.py doctor --license` (with `--target vita` for the PS Vita module).

### Checking the setup

```powershell
uv run tools/setup.py doctor                  # every target
uv run tools/setup.py doctor --target vita    # or windows, il2cpp; repeat --target for two
```

The doctor prints a checklist of everything the targets need, with the command that fixes each missing item, and changes nothing. It exits with code 1 if something required is missing. `--license` also starts the editor once. Items marked `optional` don't stop a build: Mono.Cecil and USCSandbox, which the pipeline fetches or builds on first use, the console's address, and a release of the game other than the one the rules were written for.

### Installing by hand

These are the steps the script takes, if you prefer to do them yourself:

- `uv sync` in the repository installs the Python packages from `uv.lock`.
- The game: the pipeline looks in `C:\Program Files (x86)\Steam\steamapps\common\Crow Country`. Set `CROW_GAME` or pass `--game <path>` if yours is elsewhere. The rules were written for Steam build 18838972 (Unity 2022.3.49f1); the doctor compares the hash of `Crow Country_Data\Managed\Assembly-CSharp.dll`.
- [AssetRipper](https://github.com/AssetRipper/AssetRipper) 2.0.0: extract the release's `AssetRipper_win_x64.zip` to `tools\external\assetripper-2.0.0`.
- The .NET 8 SDK: extract [dotnet-sdk-8.0.425-win-x64.zip](https://builds.dotnet.microsoft.com/dotnet/Sdk/8.0.425/dotnet-sdk-8.0.425-win-x64.zip) to `tools\external\dotnet`. The pipeline builds USCSandbox, the shader decompiler, with it.
- Unity **2018.2.19f1**: `UnitySetup64-2018.2.19f1.exe /S /D=%LOCALAPPDATA%\Unity\Editors\2018.2.19f1` ([installer](https://download.unity3d.com/download_unity/06990f28ba00/Windows64EditorInstaller/UnitySetup64-2018.2.19f1.exe)).
- For IL2CPP players: the Windows IL2CPP module ([installer](https://download.unity3d.com/download_unity/06990f28ba00/TargetSupportInstaller/UnitySetup-Windows-IL2CPP-Support-for-Editor-2018.2.19f1.exe)) with the same `/S /D=...`, and Visual Studio 2022 Build Tools with "Desktop development with C++".
- For the PS Vita: the PS Vita module, `UnitySetup-Playstation-Vita-Support-for-Editor-2018.2.19f1.exe`, with the same `/S /D=...`. Unity no longer serves it; a copy is at [archive.org/details/unitypsvitasupport](https://archive.org/details/unitypsvitasupport). Check that the installer's Authenticode signature is Unity Technologies' before you run it.
- For the PS Vita: Sony's PS Vita SDK 3.570 or later, with `SCE_PSP2_SDK_DIR` set to its folder (for example `C:\vitasdk\PSVITA\sdk`), and Target Manager Server and Publishing Tools, with `SCE_ROOT_DIR` set to their folder (for example `C:\Program Files (x86)\SCE`). The Publishing Tools installer needs .NET Framework 3.5: turn it on in "Turn Windows features on or off", or with `DISM /Online /Enable-Feature /FeatureName:NetFx3 /All` as administrator.

The pipeline fetches the rest on first use: Mono.Cecil (if setup didn't), USCSandbox at a pinned commit with the patches in `tools/downgrade/patches`, and for `vita.py crashes`, vita-parse-core. It compiles its C# helpers with the .NET Framework 4 compiler that ships with Windows.

## 2. Build

```powershell
uv run build.py                       # the PS Vita app
uv run build.py --windows             # a Windows player with Mono
uv run build.py --il2cpp              # a Windows player with IL2CPP, which runs the code as the Vita does
uv run build.py --vita --windows      # both
uv run build.py --dry-run             # print the setup command and the stages, run nothing
```

`build.py` runs `tools/setup.py` for the targets, then these stages of `tools/downgrade/pipeline.py`:

1. `rip`: AssetRipper exports the game as a 2018.2 project into `work/rip-2018.2.19f1`. It is skipped while the export is from the same game files, AssetRipper and settings; the PS Vita and Windows projects share it.
2. `project`: assembles `work/crow-2018.2.19f1-vita` (or `work/crow-2018.2.19f1` for Windows). It rewrites the game's assemblies, decompiles its shaders and applies the asset rules.
3. `build`: the Unity editor imports the project in batch mode and builds the player into `builds/crow-2018.2.19f1-vita` (or `builds/crow-2018.2.19f1/CrowCountry.exe`, `builds/crow-2018.2.19f1-il2cpp/CrowCountry.exe`).
4. `package`, PS Vita only: makes `builds/crow-2018.2.19f1-vita.vpk` (title ID `CROW00001`) with LiveArea art made from the game's Steam store images, which it downloads.

The stages run under the `vita-build` (or `pc-build`) lock of `tools/downgrade/lock.py`, so a `build.py` started while another job runs the pipeline waits for it (`--wait`, 6 hours by default).

| Option | Effect |
| --- | --- |
| `--vita`, `--windows`, `--il2cpp` | The targets. Without any, `--vita`. |
| `--game <folder>` | The game's folder, instead of `CROW_GAME` or the Steam default. |
| `--vita-sdk <path>` | Passed to setup: your own PS Vita SDK. |
| `--skip-setup` | Go straight to the pipeline. |
| `--from <stage>` | Start at `project`, `build` or `package`, when the earlier stages are done. |
| `--wait <seconds>` | How long to wait for the build lock. |
| `--dry-run` | Print what would run. |

### Running the stages one at a time

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
uv run tools/downgrade/lock.py run vita-build "me: what for" --wait 1800 -- uv run tools/downgrade/pipeline.py quickcode
```

`--game <folder>` reads the game from another folder (or set `CROW_GAME`). The console's address comes from `CROW_VITA_HOST`, or `DRIFTER_VITA_HOST` if you set that up for The Drifter.

### Build switches

These environment variables change what a stage writes, for experiments and measurements. Each takes the values listed, and a stage stops on any other; the stages log the values in effect. `project` records the code and project switches with the project, and `build` all of them with the PS Vita build's code stage, so `build` warns when the project was assembled with others, and `quickcode` when the full build had other project or build switches (which it can't apply). The table is `BUILD_SWITCHES` in `tools/downgrade/common.py`. "PS Vita data" is the PS Vita build, or a Windows player with `CROW_VITA_DATA=1`.

| Variable | Stage | Values (default with PS Vita data, without) | What |
| --- | --- | --- | --- |
| `CROW_VITA_DATA` | build | `0`, `1` (`0`) | A Windows player with the PS Vita's scene data, the defaults below as on the PS Vita, for measurements. |
| `CROW_VARIANT_BAKE` | build | `on`, `off` (`on`, `off`) | Enemies' random looks (RandomVariant) picked at build time, each from its own seed, so every build has the same look (`unity/VariantBake.cs`): every enemy's in every scene, one look per placement (the Puddles' 2,700 switched-off transforms each; the Lumber's 72 variants kept its parts from merging: 289 renderers). |
| `CROW_PROBUILDER` | build | `on`, `bake`, `off` (`on`, `off`) | The player's ProBuilder meshes built at build time (`unity/ProBuilderMeshes.cs`); `bake` keeps the components. |
| `CROW_MERGE` | build | `on`, `ab`, `off` (`on`, `off`) | The static merge (`unity/MergeScenes.cs`); `ab` keeps the parts' meshes, for `-crow-merge off`. |
| `CROW_MERGE_MAX_VERTICES`, `CROW_MERGE_MAX_KB` | build | a whole number (unset: the merge's own) | The merge's largest part, and its budget per scene. |
| `CROW_SPLIT` | build | `movers`, `on`, `off` (`movers`, `off`) | The scene split after the merge: the still parts (`on`), and those that move on their own (`movers`). |
| `CROW_PLAYER_MERGE` | build | `on`, `ab`, `off` (`on`, `off`) | The player merge (`unity/PlayerMerge.cs`); `ab` keeps the parts, for `-crow-player-merge off`. |
| `CROW_PLAYER_MERGE_PARTIAL` | build | `0`, `1` (`0`) | The player merge from the scenes there are uses of, in a test build before any full one. |
| `CROW_MCS_BAKE`, `CROW_MCS_STRIP` | build | `on`, `off` (`on`, `off`; the strip as the bake) | MeshCombineStudio's run-time combining done at build time (in Pool, first each rock placing's triangles hidden inside another rock culled), and the combined sources left out (`unity/MeshCombineBake.cs`). |
| `CROW_STRIP` | build | `on`, `off` (`on`, `off`) | The scripts that do nothing in the player left out of the scenes (`unity/StripScenes.cs`). |
| `CROW_STACK_FOLD` | build | `on`, `off` (`on`, `off`) | See-through layers stacked at one place (one mesh and material, scales within 2.5%) drawn once, with the stack's alpha (`unity/StackedLayers.cs`). |
| `CROW_LIT_BAKE` | project | `on`, `off` (`on`; PS Vita only) | The materials saved with CrowVita/Lit; `off` leaves the swap to run time. |
| `CROW_FSM_COMPILE` | code | `on`, `off` (`off`) | The FSMs' compiled bodies only, without the originals beside them (`rewriter/FsmCompile.cs`). |
| `CROW_ACTION_PROFILE` | code | `0`, `1` (`0`) | PlayMaker's actions and the behaviours timed, for `-crow-action-profile` ([vita.md](vita.md)). |
| `CROW_GFX_JOBS` | build, quickcode | `on`, `off` (`on`; PS Vita only) | The PS Vita player's graphics jobs, in `Media/boot.config` ([vita.md](vita.md)). |
| `CROW_ALLOW_SCENE_FAILURES` | build | `0`, `1` (`0`) | A build with errors or failed scene steps goes through, with a warning. |

The code switches apply wherever the rewriter runs (`code`, `project`, `quickcode`); `quickcode` also applies `CROW_GFX_JOBS`.

### Time, memory and disk

Measured on an 8-core, 16-thread Ryzen 7 5800X3D with 32 GB and an SSD:

| Step | Time |
| --- | --- |
| `rip` | about 4 minutes |
| `project`, first run | about 10 minutes, most of it reading the export and restoring the original asset names |
| `project`, rerun | a minute or two: `work/scan-cache` keeps what it read, and unchanged files keep their dates |
| `build`, PS Vita, project already imported | 14 to 21 minutes |
| `build`, Windows IL2CPP, project already imported | about 19 minutes |
| `package` | under a minute |

A first `build` of a new project also imports all 92,800 assets of the export (the PS Vita's textures and audio converted for the console) and takes longer than a rebuild; it hasn't been timed from a clean state yet.

A first build needs free space on the repository's drive. The export takes 2.6 GB, shared by the targets. The PS Vita project takes 5.4 GB with Unity's import cache (`Library`, 2.9 GB), its build 1.5 GB and the VPK 0.5 GB. The Windows project takes 6.3 GB, a Mono player 1.2 GB and an IL2CPP player 2.1 GB. With Unity's temporary files the doctor counts about 13 GB for a first PS Vita or Windows build. The editor with both modules takes another 3.9 GB in `%LOCALAPPDATA%`, and the PS Vita SDK 0.6 GB.

### Checking that builds are reproducible

Two builds of the same tree with the same switches give the same files, apart from what Unity's own tools write differently on every build (below). `tools/downgrade/buildcompare.py` checks it. It hashes what a build made into `work/buildcompare/<label>`, then compares two such snapshots file by file:

```powershell
uv run tools/downgrade/pipeline.py project
uv run tools/downgrade/buildcompare.py snapshot A --what project   # before the build: the editor writes in Assets as it builds
uv run tools/downgrade/pipeline.py build
uv run tools/downgrade/pipeline.py package
uv run tools/downgrade/buildcompare.py snapshot A --what build,vpk
# ... the same again as B, then:
uv run tools/downgrade/buildcompare.py compare A B                  # exit code 1 if anything differs
```

`--platform windows` (and `--backend il2cpp`) snapshots a Windows player and its project. The project part is the Unity project's `Assets`, `ProjectSettings` and `Packages` as the `project` stage leaves them. The build part is the player's folder, and the VPK part is each file in the VPK with its zip header.

What makes them the same:

- CrowCompat and the other tools compiled with the .NET Framework's `csc`, which has no `/deterministic`: `pipeline.reproducible_assembly` sets the time stamp, the module ID and the `<PrivateImplementationDetails>` GUID from a hash of the rest of the file.
- The random picks the build makes for the game's scripts (the static merge's and the enemy rebuild's `DoRandom`, `unity/VariantBake.cs`'s variants) seed `UnityEngine.Random` from where each script is: its scene, its path in the hierarchy and its place among its object's scripts (`Merge.PickSeed`). So a change in a scene leaves the other picks in it as they were.
- The project's `productGUID`, which the export wrote in a form 2018.2 can't read, so the editor made a new one at every build (`asset_rules.product_guid`).
- The assets whose names can't be file names (`Legacy Shaders/Diffuse`): the editor renamed them after their files only when it had imported them in the same session, and the merged meshes are named after their materials. The `project` stage now writes the file's name into them (`asset_rules.name_overlay`).
- `MeshCombiner.combineTime`, the combining's stopwatch time, zeroed after the build's bake.
- The player scene's prefab instance ID in `Assets/CrowMara.unity`, which the editor draws at random at every save, set to a fixed one.
- In `globalgamemanagers` after the build (`pipeline.reproducible_player_data`): the `[RuntimeInitializeOnLoadMethod]` list sorted, so these methods run in a fixed order (CrowCompat's first); MonoManager's list of scripts and ResourceManager's list of dependencies sorted; and the build GUID set from a hash of the player's scenes and assets.
- The VPK's zip headers: one date and one set of attributes for every file.

What still differs, and why:

- The PS Vita's IL2CPP module (`Media/Modules/il2CppAssemblies.suprx` and its copy in `SymbolFiles`), in 36 bytes. IL2CPP links it in `Library/il2cpp_cache/linkresult_<hash>`, whose name changes on every build although the object files and their order are the same, and the module embeds that path; the linker's fingerprint of the module (`vita.py crashes` matches dumps to symbols by it) changes with it. `buildcompare.py` compares the module without those bytes. The VPK holds the module, so the VPK file differs too; its files are compared one by one.
- After the build, the editor rewrites some project files it imported: the cubemaps' and the plugins' `.meta` files, the shared reverb mixers, `ProjectSettings` (adding files of its own) and the player scene. It writes them the same way in every build, and the next `project` stage puts the pipeline's versions back. So take the project snapshot before the build.
- `rip` is left out: AssetRipper's export is redone only when the game or its settings change.

## 3. Install the PS Vita app

Install `builds/crow-2018.2.19f1-vita.vpk` with VitaShell on a console with HENkaku or Ensō, then update it from the PC with `vita.py sync`. [vita.md](vita.md) describes the console tools.

## When something fails

- **setup**: a download that fails is retried; a hash or signature that doesn't match stops the script and deletes the file. Run it again.
- **The game check**: "another release than the rules were written for" means `Assembly-CSharp.dll` differs from Steam build 18838972's. It may build; if a rule stops in `project`, update the game or have Steam verify its files.
- **`rip`**: "AssetRipper did not start" or "produced no project": check `tools/external/assetripper-2.0.0` and `artifacts/downgrade/assetripper.log`. A run of `rip` alone always exports again.
- **`project`**: "No .NET 8 SDK" means `tools/external/dotnet` is missing: run setup. A rewriter failure names the assemblies that don't resolve; its log is `artifacts/downgrade/rewrite-<version>-<platform>.log`.
- **`build`**: the editor's log is `artifacts/downgrade/build-<version>-<platform>[-il2cpp].log`, and the pipeline prints a summary of it. A good build has 0 errors (`DOWNGRADE_BUILD Succeeded errors=0`): the stage stops on any error in the build's report or any failed scene step of the editor scripts, and lists them (the report's are also in `<player>.messages.txt` beside the player). "A Unity editor has ... open" means an editor still has the project open: close it. If the editor stops at once, check the license (`uv run tools/setup.py doctor --license`). If IL2CPP or the PS Vita build steps fail, check the SDK with `uv run tools/setup.py doctor --target vita`.
- **"Held by another run"**: another pipeline holds a build lock; `uv run tools/downgrade/lock.py status` shows who. `build.py` waits for it.
- **`vita.py`**: "Set CROW_VITA_HOST" means the console's address is not set (`uv run tools/setup.py --vita-host <address>`).
