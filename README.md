# Crow Country Vita

[Crow Country](https://store.steampowered.com/app/1996010/) on a PS Vita. A pipeline takes the PC release (Unity 2022.3.49f1) and rebuilds it as a Unity 2018.2 project, since 2018.2 is the last Unity with a Vita build module, then builds that for the Vita. It follows [The Drifter's port](https://github.com/xob0t/the-drifter-vita) and reuses its tools where they fit.

There are no game files in this repository. You bring your own copy, and the pipeline only reads it.

## Where it stands

The game is fully playable from start to finish, at 30 fps nearly everywhere.

- Loading a large area takes a while, up to about 20 seconds.
- The biggest fights can drop a little below 30 fps.

Other than that there are no known issues. [docs/status.md](docs/status.md) has the measurements.

## Building

You need Windows 10 or 11, Crow Country from Steam or GOG, about 20 GB of free disk space, and a console on HENkaku or Ensō with VitaShell.

### What the build installs

The build needs Unity's old editor and Sony's PS Vita SDK, so `build.py` installs these on your PC the first time it runs:

- **Unity 2018.2.19f1** with its PS Vita module, in `%LOCALAPPDATA%\Unity\Editors` (about 4 GB). The editor comes from Unity's servers, and the PS Vita module from a copy on archive.org, because Unity no longer offers it.
- **A patch to that editor's license check**, made with [unigate](https://github.com/xob0t/unigate), so you don't need a Unity license.
- **Sony's PS Vita SDK 3.570**, from archive.org, in `C:\vitasdk` (0.6 GB). Setup sets the `SCE_PSP2_SDK_DIR` variable for your user. To use your own copy of the SDK instead, pass `--vita-sdk <archive or folder>`.
- **The SDK's Target Manager Server and Publishing Tools**, which the editor's PS Vita build runs. Their installers need administrator rights, so Windows asks first. If Publishing Tools needs it, setup also turns on the Windows feature .NET Framework 3.5. Setup sets `SCE_ROOT_DIR` to point at them.
- **AssetRipper 2.0.0, the .NET 8 SDK and Mono.Cecil**, in `tools\external` inside the extracted folder.
- **Python packages**, in `.venv` inside the extracted folder.

Every download is checked against a pinned hash, and the Unity installers must carry Unity's signature before they run. `uv run tools/setup.py --vita --dry-run` lists what setup would install, and changes nothing. To remove it all later, delete the extracted folder, `C:\vitasdk` and the editor's folder, and uninstall Target Manager Server and Publishing Tools in Windows Settings.

### Steps

1. Install [uv](https://docs.astral.sh/uv/): open PowerShell from the Start menu and run

   ```powershell
   powershell -ExecutionPolicy ByPass -c "irm https://astral.sh/uv/install.ps1 | iex"
   ```

2. Download [crow-country-vita.zip](https://github.com/xob0t/crow-country-vita/releases/latest/download/crow-country-vita.zip), right-click it and choose **Extract All**.

3. Open the extracted `crow-country-vita` folder, right-click in it, choose **Open in Terminal** (on Windows 10: Shift + right-click, **Open PowerShell window here**), and run:

   ```powershell
   uv run build.py
   ```

   The VPK is `builds/crow-2018.2.19f1-vita.vpk`. Install it with VitaShell.

After the first run, `build.py` skips what is already installed. [docs/build.md](docs/build.md) has the details and what to do when something fails. [docs/vita.md](docs/vita.md) covers installing the VPK on the console.

## License

The pipeline is MIT-licensed ([LICENSE](LICENSE)). The USCSandbox patches in `tools/downgrade/patches` are GPL-3.0, like the code they change ([THIRD_PARTY_NOTICES](THIRD_PARTY_NOTICES)).
