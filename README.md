# Crow Country Vita

[Crow Country](https://store.steampowered.com/app/1996010/) on a PS Vita. A pipeline takes the PC release (Unity 2022.3.49f1) and rebuilds it as a Unity 2018.2 project, since 2018.2 is the last Unity with a Vita build module, then builds that for the Vita. It follows [The Drifter's port](https://github.com/xob0t/the-drifter-vita) and reuses its tools where they fit.

There are no game files in this repository. You bring your own copy, and the pipeline only reads it.

## Where it stands

Playable on the PS Vita. [docs/status.md](docs/status.md) has the details, the measurements and the plan.

## Building

With Crow Country installed from Steam or GOG and [uv](https://docs.astral.sh/uv/), in the repository:

```powershell
uv run build.py              # set up, then build builds/crow-2018.2.19f1-vita.vpk
```

`build.py` installs what is missing (the Unity 2018.2.19f1 editor, AssetRipper, the .NET 8 SDK and Sony's PS Vita SDK), then builds. [docs/build.md](docs/build.md) has the details and what to do when something fails; [docs/vita.md](docs/vita.md) covers installing the VPK on the console.

## License

The pipeline is MIT-licensed ([LICENSE](LICENSE)). The USCSandbox patches in `tools/downgrade/patches` are GPL-3.0, like the code they change ([THIRD_PARTY_NOTICES](THIRD_PARTY_NOTICES)).
