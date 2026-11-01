# Building

The pipeline turns your copy of Crow Country into a Unity 2018.2 project, and the Unity editor builds a PS Vita app or a Windows player from it. The pipeline only reads the game. It writes the export and the Unity projects to `work/`, the players and the VPK to `builds/`, and the logs to `artifacts/`.

You need Windows 10 or 11 on x64, and Crow Country from Steam or GOG. With uv installed, the short version is:

```powershell
uv run build.py                 # the PS Vita app: builds/crow-2018.2.19f1-vita.vpk
uv run build.py --windows       # a Windows player: builds/crow-2018.2.19f1/CrowCountry.exe
```

`build.py` runs the setup for your targets (step 1), then the pipeline's stages in order (step 2). After a failure or a change, run it again. It skips what is installed and what is up to date, and the editor imports only what changed.

## 1. Set up

1. Install Crow Country from Steam or GOG, and update it to the current release.
2. Install uv, then open a new terminal so it finds uv:

   ```powershell
   powershell -ExecutionPolicy ByPass -c "irm https://astral.sh/uv/install.ps1 | iex"
   ```

3. Clone the repository. In its folder, run the setup script for your targets:

   ```powershell
   uv run tools/setup.py                 # Windows Mono player
   uv run tools/setup.py --vita          # PS Vita app
   uv run tools/setup.py --vita --il2cpp # every target, the Windows IL2CPP player included
   ```

4. Check that everything is in place:

   ```powershell
   uv run tools/setup.py doctor --target vita    # or windows, or il2cpp
   ```

`build.py` runs setup itself, so you can also skip steps 3 and 4 and go straight to [the build](#2-build).

You can run setup again at any time. It installs what is missing and skips the rest. In this order, it:

- Runs `uv sync` for the Python packages.
- Installs the Unity 2018.2.19f1 editor into `%LOCALAPPDATA%\Unity\Editors\2018.2.19f1`, with the Windows IL2CPP module for `--il2cpp` and the PS Vita module for `--vita`.
- Downloads AssetRipper 2.0.0 into `tools/external/assetripper-2.0.0`, the .NET 8 SDK 8.0.425 into `tools/external/dotnet`, and Mono.Cecil 0.10.0-beta6 from NuGet.
- Finds [the game](#the-game).
- With `--vita`, installs [the PS Vita SDK](#the-ps-vita-sdk) and its tools.
- Removes the editor's license checks and starts the editor once. See [The Unity license](#the-unity-license).
- Prints [the doctor's checklist](#checking-the-setup).

Setup checks every download against a pinned hash. It runs a Unity installer only if it carries Unity Technologies' Authenticode signature. Installers that need administrator rights ask for them.

| Option | Effect |
| --- | --- |
| `--il2cpp` | Also install the editor's Windows IL2CPP module. An IL2CPP player also needs Visual Studio 2022 Build Tools with "Desktop development with C++". The doctor prints the command that installs them. |
| `--vita` | Also install the PS Vita module, the PS Vita SDK, Target Manager Server and Publishing Tools. |
| `--vita-sdk <path>` | Use your own PS Vita SDK, an archive such as `.zip` or `.7z` or a folder, instead of the download. Implies `--vita`. |
| `--vita-sdk-dir <dir>` | Extract the archive or copy the folder here. The default for an archive is `C:\vitasdk`. Without this option, a folder is used where it is. |
| `--vita-host <address>` | Set `CROW_VITA_HOST`, the console's address, for `vita.py`. |
| `--game <folder>` | Set `CROW_GAME` to this copy of the game. |
| `--yes`, `-y` | Accept every offer without asking. Setup offers to set a variable for a game or SDK it found, and to run the SDK's tool installers. Without a terminal to ask in, it declines and tells you to rerun with `--yes`. |
| `--dry-run` | Report what setup would do, and change nothing. |
| `--keep-downloads` | Keep the installers and archives in `work/installers`. |
| `--no-unigate` | Keep the editor's license checks, for an editor with a license of its own. `build.py` takes this option too. |
| `--skip-license-check` | Don't start the editor at the end. |

To download and check installers without installing them, for example for another machine, name the ones you want:

```powershell
uv run tools/setup.py download editor il2cpp vita assetripper dotnet cecil --to <folder>
```

### The game

The Steam and GOG releases build the same game. They have the same assets, and their code differs only in the store it signs in to. The pipeline replaces both store libraries with stubs (`STUBBED_ON` in `pipeline.py`), so no player signs in to Steam or GOG Galaxy.

The pipeline reads the game from `CROW_GAME` if it is set. Otherwise it looks in `C:\Program Files (x86)\Steam\steamapps\common\Crow Country`, then in `C:\GOG Games\Crow Country`. Setup also searches your other Steam libraries and the folder GOG's installer recorded. If it finds the game in one of them, it offers to set `CROW_GAME`.

The rules were written for version 1.0.1 of the game, built with Unity 2022.3.49f1. That is Steam build 18838972 and GOG build 58711154377035024. The doctor tells them apart from other releases by the hash of `Crow Country_Data\Managed\Assembly-CSharp.dll`.

### The PS Vita SDK

With `--vita`, setup looks for an SDK in `SCE_PSP2_SDK_DIR`, in `C:\vitasdk` and in the SCE folder's `PSP2 SDKs`. If it finds none, it downloads `SDK Software/PSVITA.zip` (350 MB) from archive.org's [copy of the SDK 3.570 DevNet release](https://archive.org/details/psvita-sdk-3.570-devnet-full) and extracts it, so the SDK is in `C:\vitasdk\PSVITA\sdk`. Setup then applies Sony's `psp2cgc` and `libc` patches from the same item, which bring the SDK from 3.570.011 to 3.570.071.

To use your own copy, pass it with `--vita-sdk`. Setup extracts a `.zip` itself. For other archives it uses 7-Zip if it is installed, or else Windows' own `tar`, which reads `.7z` and `.rar` too. In either case it finds the SDK folder, the one with `host_tools` and `target`, and checks two things:

- The version in `target\include\sdk_version.h` is 3.570 or later.
- The tools Unity looks for before a build are there. These are `psp2snc`, `psp2ld`, `arm-eabi-as`, `psp2snarl` and `psp2bin` in `host_tools\build\bin`, and `psp2psarc` in `host_tools\bin`.

The editor's PS Vita build also runs `psp2ctrl` and `psp2run` from Target Manager Server, and `psp2pubcmd` from Publishing Tools. They live in `PSP2\Tools` under `SCE_ROOT_DIR`, usually `C:\Program Files (x86)\SCE`. If they are missing, setup offers to run `TMServer-*.msi` and `PublishingTools-*.msi`. It takes them from the SDK's folder, or downloads them from the same archive.org item. Publishing Tools needs .NET Framework 3.5, which is off on a new Windows, so setup turns it on first.

Setup sets `SCE_PSP2_SDK_DIR` and `SCE_ROOT_DIR` for your user. New shells see them, Git Bash included, but shells that were already open don't. `build.py` reads both from the registry, so it works from an old shell. If you run `pipeline.py build` yourself, use a new shell, because the editor needs both.

### The Unity license

Every build runs the editor in batch mode, and Unity hides the PS Vita target unless the license includes console access. So setup patches the editor with [unigate](https://github.com/xob0t/unigate), which removes the license checks of 2017.x to 2018.2.x editors. Setup downloads `unigate.py` at a pinned commit into `tools/external/unigate`, checks its hash and runs it on `Unity.exe`. unigate keeps the original beside it as `Unity.exe<date>.orig.bak`.

Close Unity before you run setup. Windows can't write the `Unity.exe` of a running editor, and setup then asks you to close Unity and run it again. If you have a license of your own, pass `--no-unigate` and activate the license in the editor.

To undo the patch, close Unity and run:

```powershell
uv run tools/external/unigate/unigate.py --editor "$env:LOCALAPPDATA\Unity\Editors\2018.2.19f1\Editor" --restore
```

Setup then starts the editor once with an empty project in a temporary folder, which takes about a minute. It reads the editor's log, and with `--vita` it checks that the editor loaded its PS Vita module. A pass goes into `work/license-check.ok`, and setup skips the check until the editor changes. To check again, run `uv run tools/setup.py doctor --license`. Without `--target` that includes the PS Vita module, so pass `--target windows` if you didn't install it.

### Checking the setup

```powershell
uv run tools/setup.py doctor                                 # every target
uv run tools/setup.py doctor --target vita                   # one target: vita, windows or il2cpp
uv run tools/setup.py doctor --target vita --target il2cpp   # two targets
```

The doctor prints a checklist of what the targets need, with the command that fixes each missing item. It also checks the free space on the repository's drive. It changes nothing, and exits with code 1 if a required item is missing. Items marked `optional` don't stop a build:

- Mono.Cecil and USCSandbox, which the pipeline fetches or builds on first use.
- The console's address, which only `vita.py` needs.
- A release of the game other than the one the rules were written for.

### Installing by hand

If you prefer to install things yourself, these are the steps setup takes:

- Run `uv sync` in the repository.
- Install the game. If it isn't in Steam's or GOG's default folder, set `CROW_GAME` to its folder.
- Extract `AssetRipper_win_x64.zip` from the [AssetRipper](https://github.com/AssetRipper/AssetRipper) 2.0.0 release to `tools\external\assetripper-2.0.0`.
- Extract [dotnet-sdk-8.0.425-win-x64.zip](https://builds.dotnet.microsoft.com/dotnet/Sdk/8.0.425/dotnet-sdk-8.0.425-win-x64.zip) to `tools\external\dotnet`. The pipeline builds USCSandbox, the shader decompiler, with it.
- Install Unity 2018.2.19f1 with its [installer](https://download.unity3d.com/download_unity/06990f28ba00/Windows64EditorInstaller/UnitySetup64-2018.2.19f1.exe). In a Command Prompt, run `UnitySetup64-2018.2.19f1.exe /S /D=%LOCALAPPDATA%\Unity\Editors\2018.2.19f1`.
- For IL2CPP players, install the Windows IL2CPP module with its [installer](https://download.unity3d.com/download_unity/06990f28ba00/TargetSupportInstaller/UnitySetup-Windows-IL2CPP-Support-for-Editor-2018.2.19f1.exe) and the same `/S /D=...` arguments. Also install Visual Studio 2022 Build Tools with "Desktop development with C++".
- For the PS Vita, install the PS Vita module, `UnitySetup-Playstation-Vita-Support-for-Editor-2018.2.19f1.exe`, the same way. Unity no longer serves it, but [archive.org has a copy](https://archive.org/details/unitypsvitasupport). Before you run it, check with PowerShell's `Get-AuthenticodeSignature` that Unity Technologies signed it.
- For the PS Vita, install Sony's PS Vita SDK 3.570 or later, and set `SCE_PSP2_SDK_DIR` to its folder, for example `C:\vitasdk\PSVITA\sdk`. Install Target Manager Server and Publishing Tools, and set `SCE_ROOT_DIR` to their folder, for example `C:\Program Files (x86)\SCE`. Before the Publishing Tools installer, turn on .NET Framework 3.5 in "Turn Windows features on or off", or run `DISM /Online /Enable-Feature /FeatureName:NetFx3 /All` as administrator.

The pipeline fetches the rest on first use. That is Mono.Cecil if setup didn't, USCSandbox at a pinned commit with the patches in `tools/downgrade/patches`, and vita-parse-core for `vita.py crashes`. It compiles its C# helpers with the .NET Framework 4 compiler that comes with Windows.

## 2. Build

```powershell
uv run build.py                       # the PS Vita app
uv run build.py --windows             # a Windows player with Mono
uv run build.py --il2cpp              # a Windows player with IL2CPP, which runs the code as the Vita does
uv run build.py --vita --windows      # the PS Vita app and a Windows Mono player
uv run build.py --dry-run             # print the setup command and the stages, run nothing
```

`build.py` runs `tools/setup.py` for the targets, then these stages of `tools/downgrade/pipeline.py`:

1. `rip`. AssetRipper exports the game as a 2018.2 project into `work/rip-2018.2.19f1`, which all targets share. `build.py` skips this stage while the export comes from the same game files, AssetRipper and settings.
2. `project`. It assembles the Unity project in `work/crow-2018.2.19f1-vita`, or `work/crow-2018.2.19f1` for Windows. It rewrites the game's assemblies, decompiles the shaders and applies the asset rules.
3. `build`. The editor imports the project in batch mode and builds the player into `builds/crow-2018.2.19f1-vita`. A Windows player goes to `builds/crow-2018.2.19f1/CrowCountry.exe`, or `builds/crow-2018.2.19f1-il2cpp/CrowCountry.exe` for IL2CPP.
4. `package`, for the PS Vita only. It makes `builds/crow-2018.2.19f1-vita.vpk` with the title ID `CROW00001`. It downloads the game's Steam store images for the LiveArea art.

`build.py` holds a build lock from `tools/downgrade/lock.py` for the whole run. That is `vita-build` when the targets include the PS Vita, and `pc-build` otherwise. If another job is running the pipeline, `build.py` waits for it.

| Option | Effect |
| --- | --- |
| `--vita`, `--windows`, `--il2cpp` | The targets. Without any of them, `build.py` builds the PS Vita app. |
| `--game <folder>` | The game's folder, if it isn't in Steam's or GOG's default folder. Setup saves it in `CROW_GAME`. |
| `--vita-sdk <path>` | Your own PS Vita SDK, passed to setup. |
| `--no-unigate` | Keep the editor's license checks, passed to setup. |
| `--skip-setup` | Go straight to the pipeline. |
| `--from <stage>` | Start at `rip`, `project`, `build` or `package`, when the earlier stages are done. |
| `--wait <seconds>` | How long to wait for the build lock. The default is 6 hours. |
| `--dry-run` | Print the setup command and the stages, and run nothing. |

### Running the stages one at a time

`tools/downgrade/pipeline.py` runs one stage. Its platform is `vita` unless you pass `--platform windows`.

```powershell
uv run tools/downgrade/pipeline.py rip                       # AssetRipper export, about 4 minutes
uv run tools/downgrade/pipeline.py code                      # rewrite the game's assemblies, list what doesn't resolve
uv run tools/downgrade/pipeline.py link --platform vita      # Unity's linker, then IL2CPP's C++ conversion, in about a minute
uv run tools/downgrade/pipeline.py project --platform vita   # assemble work/crow-2018.2.19f1-vita
uv run tools/downgrade/pipeline.py build --platform vita     # import and build with the Unity editor
uv run tools/downgrade/pipeline.py package                   # builds/crow-2018.2.19f1-vita.vpk
```

Run on its own, `rip` always exports again. For Windows players, pass `--platform windows` to `code`, `link`, `project` and `build`, and add `--backend il2cpp` to `build` for IL2CPP. `loadcheck` loads every rewritten type and compiles every method under the 2018 Mono, in a copy of the Windows Mono player, so build that player first. `--game <folder>` reads the game from another folder.

Other jobs on the machine share `work/`, `builds/` and `artifacts/`, so run each stage under a build lock:

```powershell
uv run tools/downgrade/lock.py run vita-build "me: what for" --wait 1800 -- uv run tools/downgrade/pipeline.py project
```

Use `pc-build` for a Windows stage. Without a lock, `pipeline.py` stops if another run holds one, and warns otherwise. `tools/downgrade/lock.py` has the details.

For quicker rounds on the PS Vita:

```powershell
uv run tools/downgrade/pipeline.py quickcode                 # code changes only, about 2 minutes
uv run tools/downgrade/vita.py sync                          # upload what changed to the console
uv run tools/downgrade/pipeline.py build --scenes "Title,Station Square"   # a test build of a few scenes
uv run tools/downgrade/vita.py --build builds/crow-2018.2.19f1-vita-scenes install
```

`quickcode` needs a full PS Vita build first. It rewrites the assemblies, replays that build's UnityLinker and IL2CPP steps without the editor, and replaces the IL2CPP module and metadata in the build. The build's data still has the scripts' old fields. So `quickcode` stops when a serialized type changed, such as a new field or MonoBehaviour, or when a `[RuntimeInitializeOnLoadMethod]` came or went. It warns when the asset rules or the build script changed. These changes need `project` and `build`, and `--force` tries anyway.

`build --scenes` builds the first scene, Logos, and the named ones into `builds/crow-2018.2.19f1-vita-scenes`. It leaves the full build alone. Use it only for tests, because the game loads scenes by name and loading a scene the build left out fails. `build --development` makes Unity's development player.

[vita.md](vita.md#code-changes-without-a-full-build) has more on quick rounds. The players take `-crow-*` flags for measurements, listed in [vita.md](vita.md#probe-flags). `tools/downgrade/vitalike.py` runs the Windows IL2CPP player at about the PS Vita's CPU speed. The console tools find the console at `CROW_VITA_HOST`, or at `DRIFTER_VITA_HOST` if you set that up for The Drifter's port.

### Build switches

These environment variables change what a stage writes, for experiments and measurements. The list is `BUILD_SWITCHES` in `tools/downgrade/common.py`.

- `pipeline.py` stops before any stage on a value a switch doesn't take.
- Each stage logs the values in effect.
- `project` records the code and project switches with the project, and `build` warns when they differ.
- A full PS Vita `build` records every switch. `quickcode` applies the code and player switches itself, and warns when another switch differs from the full build's.

The defaults depend on whether the build uses the PS Vita's scene data. The PS Vita build does, and so does a Windows player with `CROW_VITA_DATA=1`. The table gives the default with PS Vita data first.

| Variable | Values | Default | Read by | What it does |
| --- | --- | --- | --- | --- |
| `CROW_VITA_DATA` | `0`, `1` | `0` / `0` | `build` | `1` builds a Windows player with the PS Vita's scene data and switch defaults, for measurements. |
| `CROW_VARIANT_BAKE` | `on`, `off` | `on` / `off` | `build` | Picks every enemy's random look (RandomVariant) at build time, from a seed per placement, so every build looks the same. `unity/VariantBake.cs`. |
| `CROW_PROBUILDER` | `on`, `bake`, `off` | `on` / `off` | `build` | Builds the player's ProBuilder meshes at build time. `bake` also keeps the components. `unity/ProBuilderMeshes.cs`. |
| `CROW_MERGE` | `on`, `ab`, `off` | `on` / `off` | `build` | The static merge, `unity/MergeScenes.cs`. `ab` also keeps the parts' meshes, so `-crow-merge off` can turn the merge off at run time. |
| `CROW_MERGE_MAX_VERTICES` | a whole number | unset / unset | `build` | The largest merged part, in vertices. Unset, the merge uses its own limit. |
| `CROW_MERGE_MAX_KB` | a whole number | unset / unset | `build` | The merge's budget per scene, in KB. Unset, the merge uses its own budget. |
| `CROW_SPLIT` | `movers`, `on`, `off` | `movers` / `off` | `build` | The scene split after the merge. `on` splits off the still parts, and `movers` also the parts that move on their own. |
| `CROW_PLAYER_MERGE` | `on`, `ab`, `off` | `on` / `off` | `build` | The player merge, `unity/PlayerMerge.cs`. `ab` also keeps the parts, so `-crow-player-merge off` can turn it off at run time. |
| `CROW_PLAYER_MERGE_PARTIAL` | `0`, `1` | `0` / `0` | `build` | `1` merges the player from the scenes this build has, for a test build made before any full one. |
| `CROW_MCS_BAKE` | `on`, `off` | `on` / `off` | `build` | Does MeshCombineStudio's run-time combining at build time. `unity/MeshCombineBake.cs`. |
| `CROW_MCS_STRIP` | `on`, `off` | as `CROW_MCS_BAKE` | `build` | Leaves out the renderers of the meshes the bake combined. |
| `CROW_STRIP` | `on`, `off` | `on` / `off` | `build` | Leaves the scripts that do nothing in the player out of the scenes. `unity/StripScenes.cs`. |
| `CROW_STACK_FOLD` | `on`, `off` | `on` / `off` | `build` | Draws see-through layers stacked in one place once, with the stack's alpha. A stack shares one mesh and material, with scales within 2.5%. `unity/StackedLayers.cs`. |
| `CROW_BACKING` | `on`, `off` | `on` / `off` | `build` | Moves opaque faces of the game's other shaders 3 cm back when they lie just behind a depth-layered CrowVita/Lit face and face its way, such as the Dungeon's black wall backing. Otherwise the depth layer puts them in front. `unity/BackingFaces.cs`. |
| `CROW_LIT_BAKE` | `on`, `off` | `on`, PS Vita only | `project` | Saves the materials with the CrowVita/Lit shader. `off` leaves the swap to run time. |
| `CROW_FSM_COMPILE` | `on`, `off` | `off` / `off` | `code`, `project`, `quickcode` | `on` keeps only the FSMs' compiled bodies and drops the originals. `rewriter/FsmCompile.cs`. |
| `CROW_ACTION_PROFILE` | `0`, `1` | `0` / `0` | `code`, `project`, `quickcode` | `1` times PlayMaker's actions and the behaviours, for the [`-crow-action-profile`](vita.md#probe-flags) flag. |
| `CROW_GFX_JOBS` | `on`, `off` | `on`, PS Vita only | `build`, `quickcode` | Turns on the PS Vita player's graphics jobs in `Media/boot.config`. See [vita.md](vita.md#il2cpp-settings). |
| `CROW_ALLOW_SCENE_FAILURES` | `0`, `1` | `0` / `0` | `build` | `1` lets a build with errors or failed scene steps through, with a warning. |

### Time and disk space

These times come from an 8-core, 16-thread Ryzen 7 5800X3D with 32 GB of RAM and an SSD:

| Step | Time |
| --- | --- |
| `rip` | about 4 minutes |
| `project`, first run | about 10 minutes, mostly reading the export and restoring the original asset names |
| `project`, rerun | a minute or two, because `work/scan-cache` keeps what it read and unchanged files keep their dates |
| `build`, PS Vita, project already imported | 14 to 21 minutes |
| `build`, Windows IL2CPP, project already imported | about 19 minutes |
| `package` | under a minute |

The first `build` of a new project also imports the whole export and converts its textures and audio for the target, so it takes longer than a rebuild. It hasn't been timed from a clean state.

The export takes 2.6 GB on the repository's drive, shared by the targets. The PS Vita project takes 5.4 GB with Unity's `Library`, its build 1.5 GB and the VPK 0.5 GB. The Windows project takes 6.3 GB, a Mono player 1.2 GB and an IL2CPP player 2.1 GB. Counting Unity's temporary files, the doctor asks for about 13 GB free for a first PS Vita or Mono build, 14 GB for IL2CPP, and 4 to 5 GB for a rebuild. The editor with both modules takes another 3.9 GB in `%LOCALAPPDATA%`, where setup wants 5 GB free. The PS Vita SDK takes 0.6 GB.

### Checking that builds are reproducible

Two builds of the same tree with the same switches give the same files, apart from a few that Unity's own tools write differently every time. `tools/downgrade/buildcompare.py` checks this. It hashes what a build made into `work/buildcompare/<label>`, then compares two such snapshots file by file:

```powershell
uv run tools/downgrade/pipeline.py project
uv run tools/downgrade/buildcompare.py snapshot A --what project   # before the build: the editor writes in Assets as it builds
uv run tools/downgrade/pipeline.py build
uv run tools/downgrade/pipeline.py package
uv run tools/downgrade/buildcompare.py snapshot A --what build,vpk
# ... the same again as B, then:
uv run tools/downgrade/buildcompare.py compare A B                  # exit code 1 if anything differs
```

For a Windows player, pass `--platform windows` to `snapshot`, and `--backend il2cpp` for IL2CPP. The `project` part is the project's `Assets`, `ProjectSettings` and `Packages` as the `project` stage leaves them. The `build` part is the player's folder. The `vpk` part is each file in the VPK with its zip header.

`compare` expects these differences and reports them as known:

- The PS Vita's IL2CPP module, `Media/Modules/il2CppAssemblies.suprx`, and its copy in `SymbolFiles`. The module embeds the path IL2CPP linked it in, `Library/il2cpp_cache/linkresult_<hash>`, whose name changes on every build. The module's fingerprint changes with it. `compare` checks the module without those 36 bytes.
- The VPK file, because it holds the module. `compare` checks the files inside it one by one.

After a build, the editor also rewrites some project files it imported, such as the `.meta` files of cubemaps and plugins, the reverb mixers, `ProjectSettings` and the player scene. The next `project` stage puts the pipeline's versions back. That is why you take the project snapshot before the build. The `rip` export isn't compared.

If `compare` reports a new difference, something new varies or one of the pipeline's fixes stopped working. These are the fixes:

- `pipeline.reproducible_assembly` sets the time stamp, module ID and a GUID in the tools' assemblies, since the .NET Framework's `csc` has no `/deterministic`.
- `Merge.PickSeed` in `compat/Merge.cs` seeds each of the build's random picks from where its script is.
- `asset_rules.product_guid` fixes the project's `productGUID`, and `asset_rules.name_overlay` the names of assets whose names can't be file names.
- `unity/MeshCombineBake.cs` zeroes `combineTime`, and `unity/DowngradeBuild.cs` fixes a prefab instance ID in `Assets/CrowMara.unity`.
- `pipeline.reproducible_player_data` sorts the lists in `globalgamemanagers` and derives its build GUID from the content. The `[RuntimeInitializeOnLoadMethod]` methods then run in a fixed order, CrowCompat's first.
- `package` gives every file in the VPK the same date and attributes.

## 3. Install the PS Vita app

Install `builds/crow-2018.2.19f1-vita.vpk` with VitaShell on a console with HENkaku or Ensō. After that, `vita.py sync` updates the app from the PC. [vita.md](vita.md#installing) has the steps and the console tools.

## When something fails

`build.py` stops at the first failure and says what failed. Fix it and run `build.py` again, and it picks up where it stopped.

### Setup

- Setup retries a download that times out or hits a server error. If a download's hash doesn't match, setup deletes it and stops. Run setup again.
- Setup stops without running a Unity installer that lacks a valid Unity Technologies signature.
- "unigate could not patch" usually means Unity is running. Close it and run setup again.
- "To do" lines at the end are steps setup couldn't take, such as an offer you declined. Do them or rerun with `--yes`. `build.py` won't build until they are done.

### The game

- If the doctor reports "another release than the rules were written for", your `Assembly-CSharp.dll` differs from Steam build 18838972's and GOG build 58711154377035024's. The game may still build. If a rule stops in `project`, update the game, or have Steam or GOG Galaxy verify its files.
- If the doctor reports "built with Unity ..., the rules expect 2022.3.49f1", the game is another version. The same fix applies.
- If the doctor finds no `Crow Country.exe`, or a stage finds no `_Data` folder or no Mono build, the tools are looking in the wrong folder. Run `uv run tools/setup.py --game <folder>`.

### `rip`

If you see "AssetRipper did not start" or "AssetRipper export produced no project", check `tools/external/assetripper-2.0.0` and `artifacts/downgrade/assetripper.log`.

### `project`

- "No .NET 8 SDK" means `tools/external/dotnet` is missing. Run setup.
- A rewriter failure names the assemblies that don't resolve. Its log is `artifacts/downgrade/rewrite-<version>-<platform>.log`.
- "Expected at least ... The export changed or the rule no longer matches it" means a rule matched less than on the export it was written for. Another release of the game is the usual cause.

### `build`

- The editor's log is `artifacts/downgrade/build-<version>-<platform>.log`, with `-il2cpp` or `-scenes` added for those builds. The pipeline prints a summary of it.
- A good build logs `DOWNGRADE_BUILD Succeeded errors=0`. The stage stops on any error in the build report or any failed scene step of the editor scripts, and lists them. The report's errors are also in `<player>.messages.txt` beside the player. For experiments, `CROW_ALLOW_SCENE_FAILURES=1` lets such a build through.
- "A Unity editor has ... open" means an editor still has the project open. Close it.
- "PS Vita build support is not installed for this editor" means the PS Vita module is missing. Run `uv run tools/setup.py --vita`.
- If the editor stops at once, run `uv run tools/setup.py doctor --license`. A failed check leaves the editor's log in `work/license-check.log`.
- If IL2CPP or the PS Vita build steps fail, run `uv run tools/setup.py doctor --target vita`. If you started `pipeline.py` from a shell opened before setup, use a new shell.

### `quickcode`

"No saved PS Vita build to update" means there is no full PS Vita build yet. Run `pipeline.py build --platform vita` once.

### Locks

- "Held by another run" means another pipeline holds a build lock. `uv run tools/downgrade/lock.py status` shows who.
- If `build.py` says another job held the lock for the whole `--wait`, try again later or pass a longer `--wait`.

### `vita.py`

"Set CROW_VITA_HOST" means the console's address is not set. Run `uv run tools/setup.py --vita-host <address>`.
