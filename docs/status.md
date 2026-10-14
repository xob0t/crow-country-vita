# Status and plan

## The game

Crow Country is Unity 2022.3.49f1, Mono, built-in render pipeline, gamma colour space. Its data is 1.3 GB in 63 scenes. Most of the logic is PlayMaker FSMs (1,431 action classes) on top of SFB's own code in `com.sfbgames.*`. It also uses Rewired for input, DOTween, TextMeshPro 3, I2 Localization, ProBuilder and MeshCombineStudio, and Addressables for a few loads.

The data is heavy on 3D: 516,000 GameObjects, 86,000 meshes (412 MB of mesh objects), 130,000 mesh renderers and 239,000 MonoBehaviours. The largest scene file is 35 MB (Pool). Memory per scene will decide what runs on the Vita.

The game has a Switch build, and some of its code shows it. `CrowCountryCamEffect` has an `isSwitch` path that renders the camera at 624x351 instead of 1248x702.

## Done

- **Rip.** AssetRipper 2.0.0 exports the game as a 2018.2.19f1 project in about 4 minutes (92,809 assets, 2.7 GB). The first export stopped near the end with no error in either log; the second finished.
- **Code.** All 15 assemblies the game needs rewrite against the 2018.2 PS Vita engine with no unresolved references (`pipeline.py code`). The rewriter is The Drifter's core (retargeting, `RedirectMembers`, `Verify`) without its game-specific passes. CrowCompat fills in:
  - .NET Standard 2.1 string overloads, `TryGetComponent`, `Mesh.MarkModified`, `HasVertexAttribute`, `SetIndices(List)`, `LocalKeyword`, `Application.Quit(int)` and a few TMP needs (`compat/ApiShims.cs`).
  - TextCore's font data types under the field names 2022 serializes, so TMP 3's font assets load as written. FontEngine reports failure, so dynamic font assets get no new glyphs (`compat/TextCore.cs`).
  - Addressables. Unity.Addressables and Unity.ResourceManager are not shipped. The game's calls (`LoadAssetAsync`, `LoadSceneAsync`, `Acquire`, the operation handle) load from `Resources/Addressables/<key>` and `SceneManager` (`compat/Addressables.cs`).
  - VideoPlayer, which the Vita's 2018.2 player lacks (`compat/Video.cs`). A started video ends at once and shows nothing.
  - SFB's change to uGUI, `VertexHelper.SetUV1(Vector4)`. 2018.2 keeps uv1 as a Vector2, so the shim also writes the vector to the tangent.
  - A rewriter pass, `RetypeFields`, converts TMP's stores to `UIVertex.uv0` (Vector4 in 2019+, Vector2 in 2018).
- **Shaders.** USCSandbox decompiles all 29 of the game's own shaders. It needed a fix for 2022: the player programs are stored one list per graphics tier, and it read the last list, which is empty (`patches/uscsandbox-tiers.patch`). `Standard` fails to decompile, but the game gets the 2018.2 built-in one.
- **First import.** The first editor import stopped on the I2 Localization targets. I2 keeps each `Localize` component's target object in the scene, and AssetRipper exported the 70 targets as assets that reference the scene's text components. Unity logged "Do not use ReadObjectThreaded on scene objects!" without end, 11.8 GB in 30 minutes. `scene_reference_overlay` now cuts asset references to scene objects; I2's `LocalizeTarget.IsValid` finds the same component again at run time. The editor's API updater also failed to check five rewritten assemblies (it couldn't resolve the engine modules their attribute enums come from). That was only logged, but the rewriter now marks every assembly with `[UnityAPICompatibilityVersion("2018.2.19f1")]`, so the updater skips them.
- **Material keywords.** Unity 2022 keeps keywords in `m_ValidKeywords`, and the export left every material's `m_ShaderKeywords` empty, so all 3,379 Standard materials would have lost their alpha modes, detail maps and emission. `material_rules.py` matches each exported material to the game's by name and property values (3,627 of 3,643 match) and writes the original keywords back. The few without a single match get what Unity's Standard inspector would set; that derivation agrees with the real keywords on 3,374 of the 3,376 matched Standard materials.
- **Vita shader compile.** Two decompiler outputs psp2cgc rejects. The light probe proxy volume uniforms are declared by Unity only where `UNITY_LIGHT_PROBE_PROXY_VOLUME` is on, which it isn't on the Vita, and the decompiler leaves Unity's uniforms undeclared (`conditional_uniforms` adds them under the opposite condition). A program's vertex and fragment variant blocks can both declare a uniform for the same variant (`guard_repeated_declarations` gives each repeated declaration its own `#ifndef` guard).
- **Linker.** `typeof()` in attribute arguments (`[CheckForComponent(typeof(VideoPlayer))]` on 11 PlayMaker actions) is stored as a name in the attribute blob, outside the type table, and kept pointing at the Vita's empty VideoModule. The rewriter now retargets those (82 sites) and Verify checks them. The game assembly also kept assembly references to the Addressables DLLs after all their types moved to CrowCompat; those are dropped.
- **Rewired and Mono 2018.** The first Windows player booted to the title, but Rewired failed to start ("Input will not function") and logged errors every frame. Three findings:
  - AssetRipper's exported DLLs are not the game's: its `Rewired_Core.dll` has an interface method the game's lacks, so a class implementing it failed to load (Mono), and IL2CPP refused to convert it. The rewriter now reads the game's own `Managed` folder; the export gives only the `.meta` GUIDs.
  - Rewired's obfuscated classes re-declare interfaces their base classes implement explicitly, which Mono 2018 can't resolve. `ReimplementedInterfaces` (`rewriter/Runtime.cs`) drops the re-declaration where the class implements nothing itself, and otherwise gives it explicit forwarding stubs.
  - The game's one default interface method (`IFsmStateActionGUI.OnGUI`, empty) becomes abstract; its implementers inherit `FsmStateAction.OnGUI`.
  - Compilers mark unsafe assemblies with security declarations that the 2018 class libraries can't resolve; Unity's linker stopped on them, so they're stripped.
- **Quick checks.** `pipeline.py link` runs Unity's linker on the rewritten assemblies (seconds, where the editor reports it after building every scene), and `pipeline.py loadcheck` loads every type and compiles every method under the editor's Mono in a copy of the Windows player.
- **First runs.** The Windows player boots to the title and loads Station Square from `-crow-scene` in about 3 s. There, about 900 of the scene's 6,073 renderers are enabled and about 306 visible, with 12-14 lights (2 directional, 8-10 point, 2 spot).
- **Project.** `pipeline.py project` assembles the 2018.2 project with the rewritten assemblies, decompiled shaders, Vita audio and texture import settings, the Addressables assets moved into Resources, the VideoPlayer components replaced by CrowCompat's, and the build script.

- **Vita build.** The editor builds the PS Vita player: IL2CPP for PSP2, 63 scenes, 1.5 GB of data. `pipeline.py package` makes `builds/crow-2018.2.19f1-vita.vpk` (535 MB, title ID CROW00001) with LiveArea art from the Steam store images (`livearea.py`): the booth crow as the bubble, the ticket booth scene as the background, the logo capsule as the gate and boot picture. It has not run on a console yet.

## Next

1. Import and build the project with the 2018.2 editor for the Vita (`pipeline.py build`), and fix what the import and IL2CPP report.
2. Get the title screen running on the console.
3. Input. Rewired_Core knows the PS Vita (`platformVars_psVita`, `fallback_PSVita`) and falls back to Unity's input there, and the exported Input Manager keeps Rewired's 484 `JoyNAxisM` axes. So Rewired may work on the console as it is. If it doesn't, the fallback is to redirect the game's `ReInput` calls to a CrowCompat input layer.
4. Rendering cost.
   - Done: the post-processing chain at half size on the Vita (`compat/CamEffect.cs`). The camera rendered at 1248x702 and the PSX and blur passes drew at 1248x702, but the first filter reads the camera into 550x400 and the last writes 416x234, which the screen shows. At 624x351 (the game's own Switch camera size) those three steps shade a quarter of the pixels. Screenshots of Station Square with `-crow-post full` and `half` on Windows look the same.
   - Draw calls: the original build has no static batching, and every mesh renderer has a PlayMaker FSM above it, so editor-time batching can't tell what moves. The game combines meshes and materials itself at run time (MeshCombineStudio, `MaterialCombiner`): in Station Square about 900 of 6,073 renderers are enabled and about 300 visible. The next step is to count draw calls in a running frame.
   - Lights and shadows: 4 pixel lights and soft High shadows over 50 m. `compat/Quality.cs` overrides them from flags (`-crow-pixel-lights`, `-crow-shadows`, `-crow-shadow-distance`, `-crow-shadow-resolution`); none is a Vita default yet. Screenshots: in Station Square, 2 pixel lights and hard, Low, 25 m shadows look the same as the original, and so does no shadows; 0 pixel lights lights the big walls per vertex, flat and wrong. In Mansion, whose objects are lit by several coloured lights (28 active, 308 additive draws), 2 pixel lights loses the vases' red and teal light. Additive draws in Station Square: 268 with 4 pixel lights, 222 with 2, 129 with 1.
   - CPU at Vita speed (`tools/downgrade/vitalike.py`, the Windows IL2CPP player at 5.5% of the PC's time on three cores, The Drifter's calibration for this CPU): Station Square standing, 29-30 fps, 34 ms a frame, with the original settings.
5. `Unlit/UIPixelate` reads uv1.zw, which 2018.2 drops; its shader has to read the tangent instead.
6. Memory. Mesh data per scene, read/write flags on meshes, ProBuilder's per-object data (`ProBuilderMesh` components keep their own copy of every mesh), and MeshCombineStudio's output.
7. The health monitor videos (five short webm loops). They could become frame sequences that CrowCompat's VideoPlayer draws into the target texture.
8. Saves (`CrowCountrySave`, SmoothSave), the save folder on the Vita, and Steam and GOG stubs (both are stubbed on the Vita already, with `StubAssembly`).
9. A setup script and `build.py`, like The Drifter's, once the build works.
