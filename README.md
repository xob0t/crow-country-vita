<img src="assets/icon.svg" width="64" height="64" alt="">

# Crow Country Vita

[Crow Country](https://store.steampowered.com/app/1996010/) on a PS Vita. A pipeline takes the PC release (Unity 2022.3.49f1) and rebuilds it as a Unity 2018.2 project, since 2018.2 is the last Unity with a Vita build module, then builds that for the Vita. It follows [The Drifter's port](https://github.com/xob0t/the-drifter-vita) and reuses its tools where they fit.

There are no game files in this repository. You bring your own copy, and the pipeline only reads it.

## Where it stands

Early. The pipeline builds the game for Windows (Mono and IL2CPP) and the PS Vita from Unity 2018.2, and packages a VPK. On Windows it boots, loads areas and draws as the original does. The VPK hasn't been tested on a console yet. [docs/status.md](docs/status.md) has the details, the measurements and the plan.

## Running the pipeline

The pipeline needs the same tools as The Drifter's: uv, the Unity 2018.2.19f1 editor with its PS Vita module, Sony's PS Vita SDK, AssetRipper 2.0.0, a .NET 8 SDK and Mono.Cecil. This checkout links them from The Drifter's `tools/external` for now; a setup script comes later.

```powershell
uv run tools/downgrade/pipeline.py rip                       # AssetRipper export, about 4 minutes
uv run tools/downgrade/pipeline.py code                      # rewrite the game's assemblies, list what fails
uv run tools/downgrade/pipeline.py link --platform vita      # Unity's linker and IL2CPP's conversion on it, in a minute
uv run tools/downgrade/pipeline.py project --platform vita   # assemble work/crow-2018.2.19f1-vita
uv run tools/downgrade/pipeline.py build --platform vita     # import and build with the Unity editor
uv run tools/downgrade/pipeline.py package                   # builds/crow-2018.2.19f1-vita.vpk
```

For Windows players, `project` and `build` take `--platform windows` (and `build` `--backend il2cpp`). `loadcheck` loads every rewritten type and compiles every method under 2018's Mono, in a copy of the Windows player. The players take `-crow-*` flags for measurements (`tools/downgrade/compat/Diag.cs`), and `tools/downgrade/vitalike.py` runs the Windows IL2CPP player at about the PS Vita's CPU speed.

`--game <folder>` reads the game from another folder (or set `CROW_GAME`). The console's address comes from `CROW_VITA_HOST`, or `DRIFTER_VITA_HOST` if you set that up for The Drifter.

## License

The pipeline is MIT-licensed ([LICENSE](LICENSE)). The USCSandbox patches in `tools/downgrade/patches` are GPL-3.0, like the code they change.
