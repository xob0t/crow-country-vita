using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEngine;
using UnityEngine.SceneManagement;

// The measurement probe: off unless a -crow-* flag is given (on the PS Vita, in ux0:data/crow/args.txt;
// see ProbeFiles). Each flag's line in Help says what it does. Heartbeats go to the player log as
// "HEARTBEAT key=value ...", for tools to read.
// Part of CrowCompat; see Compat2018.cs.
namespace CrowCompat
{
    public class Diag : MonoBehaviour
    {
        const string Help =
            "-crow-scene <name>       load this scene once the Title and the player's scene (CrowMara) have loaded (an area)\n" +
            "-crow-scene-then <name> <s>  s seconds (default 30) after that scene loaded, load this one as the game's\n" +
            "                         area changes do (PlayMaker's LoadScene action), with the player in play;\n" +
            "                         \"A>B>C\" loads them in turn, each s seconds after the one before\n" +
            "-crow-heartbeat <s>      every s seconds, log frame times, GC and memory, visible renderers and lights\n" +
            "-crow-quit-after <s>     quit s seconds after the probe starts\n" +
            "-crow-shot <s> <path>    save a screenshot s seconds after the last scene load\n" +
            "-crow-no-vsync           vSyncCount 0 and no frame cap, for unsynchronized frame times\n" +
            "-crow-mute               no sound (AudioListener.volume 0, kept there), for test runs on a PC in use\n" +
            "-crow-input              log Unity's joystick buttons as they go down, and every 5 s the joysticks Unity and Rewired see\n" +
            "                         and player 0's maps; as they change, Unity's raw axes past 0.2 and held joystick buttons,\n" +
            "                         Rewired's active joystick elements and player 0's active actions\n" +
            "-crow-stall-dump <s>     crash the player when a frame has taken s seconds, for a core dump with the main thread's stack\n" +
            "-crow-stall-after <t>    with -crow-stall-dump, only frames that start t seconds or more after startup (skips earlier stalls)\n" +
            "-crow-dump-text <s>      s seconds after the last scene load, log each TextMeshPro text with its font and mesh\n" +
            "-crow-language <name>    keep I2 Localization on this language (English, French, Japanese, Korean, \"Chinese (Simplified)\", ...)\n" +
            "-crow-find <regex> <s>   s seconds after each scene load, log every renderer (active or not) whose hierarchy path matches\n" +
            "                         the regex: active, enabled, isVisible, bounds, mesh, materials and shaders (CROW_FIND), for\n" +
            "                         what looks missing in play\n" +
            "-crow-teleport <x,y,z> <s>  s seconds (default 3) after each scene load, move the player (its \"character\n" +
            "                         RIGIDBODY\") to world position x,y,z, to look at a place without walking there";

        static string[] args;
        static string sceneToLoad, sceneThen;
        static float sceneThenAfter;
        static float heartbeat, quitAfter = -1f, shotAfter = -1f;
        static string shotPath;
        static bool noVsync, mute, input;
        static float dumpTextAfter = -1f;
        static System.Text.RegularExpressions.Regex findPattern;
        static float findAfter = -1f;
        static Vector3 teleportTo;
        static float teleportAfter = -1f;
        static string textShader, language;
        static PropertyInfo currentLanguage;
        static float lastLanguageCheck = -10f;
        static float stallDump = -1f, stallAfter;
        static volatile float frameStartTime;  // realtimeSinceStartup at frameStartTicks
        static readonly System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
        static long frameStartTicks;

        float started, lastBeat, lastLoad, worst;
        int frames, collections;
        double frameSum;
        bool shotTaken, sceneRequested, textDumped, waitLogged, found, teleported;
        float lastShaderSwap, lastInputReport = -10f;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Install()
        {
            Quality.Install();
            Memory.Install();
            Audio.InstallDefaults();  // before Loading's title prefetch, whose loading thread it moves
            Loading.Install();
            Clock.Install();
            Alloc.Install(ProbeFiles.Args);
            IdleAudio.Install(ProbeFiles.Args);
            Fight.Install(ProbeFiles.Args);  // -crow-fight: the fight benchmark (Fight.cs)
            EnemyCost.Install(ProbeFiles.Args);  // the enemies' costs (PS Vita defaults)
            FixedStep.Install(ProbeFiles.Args);  // -crow-fixed-dt: the physics step
            EnemySplit.Install(ProbeFiles.Args);  // -crow-enemy-split: the enemies' inactive parts out
            FastPaths.Install(ProbeFiles.Args);  // -crow-fast-property, -crow-lit-camera (FastPaths.cs)
            Decor.Install(ProbeFiles.Args);  // decoration off screen, -crow-motion-report (Decor.cs)
            Cuts.Install(ProbeFiles.Args);  // the fights' cuts (Cuts.cs; PS Vita defaults), after Decor
            Pad.Install(ProbeFiles.Args);  // the PS Vita's pad without Rewired's updates (Pad.cs; PS Vita default)
            Audit.Install(ProbeFiles.Args);  // -crow-audit, -crow-renderdoc: the fight frame audit (Audit.cs)
            args = ProbeFiles.Args;
            if (!args.Any(a => a.StartsWith("-crow-"))) return;
            sceneToLoad = Value("-crow-scene") ?? Fight.Scene;
            sceneThen = Value("-crow-scene-then");
            int then = Array.IndexOf(args, "-crow-scene-then");
            if (then < 0 || then + 2 >= args.Length || !float.TryParse(args[then + 2], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out sceneThenAfter))
                sceneThenAfter = 30f;
            heartbeat = Number("-crow-heartbeat", 0f);
            quitAfter = Number("-crow-quit-after", -1f);
            shotAfter = Number("-crow-shot", -1f);
            int shot = Array.IndexOf(args, "-crow-shot");
            shotPath = shot >= 0 && shot + 2 < args.Length ? args[shot + 2] : null;
            noVsync = args.Contains("-crow-no-vsync");
            mute = args.Contains("-crow-mute");
            input = args.Contains("-crow-input");
            if (mute) AudioListener.volume = 0f;
            dumpTextAfter = Number("-crow-dump-text", -1f);
            string find = Value("-crow-find");
            if (find != null)
            {
                findPattern = new System.Text.RegularExpressions.Regex(find);
                int at = Array.IndexOf(args, "-crow-find");
                float seconds;
                findAfter = at + 2 < args.Length && float.TryParse(args[at + 2], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out seconds) ? seconds : 5f;
            }
            string teleport = Value("-crow-teleport");
            if (teleport != null)
            {
                float[] xyz = teleport.Split(',').Select(v => float.Parse(v, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
                teleportTo = new Vector3(xyz[0], xyz[1], xyz[2]);
                int at = Array.IndexOf(args, "-crow-teleport");
                float seconds;
                teleportAfter = at + 2 < args.Length && float.TryParse(args[at + 2], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out seconds) ? seconds : 3f;
            }
            language = Value("-crow-language");
            textShader = Value("-crow-text-shader");
            stallDump = Number("-crow-stall-dump", -1f);
            stallAfter = Number("-crow-stall-after", 0f);
            if (stallDump > 0f) new System.Threading.Thread(WatchForStall) { IsBackground = true, Name = "CrowStallWatch" }.Start();
            if (args.Contains("-crow-help")) Debug.Log("CROW_DIAG flags:\n" + Help);
            var probe = new GameObject("CrowDiag");
            DontDestroyOnLoad(probe);
            if (Audio.WantsTap) probe.AddComponent<AudioSource>();  // the mixer tap's silent source (Audio.cs)
            probe.AddComponent<Diag>();
            Audio.Install(probe);
        }

        // -crow-stall-dump: a frame that never ends in the logs (the PS Vita's 40-60 s frames after a
        // scene load) can't say what it spends its time on, and the console has no sampling profiler.
        // Crashing the player mid-frame leaves a core dump with every thread's stack, the main
        // thread's among them (vita.py crashes --pull --thread main).
        static void WatchForStall()
        {
            while (true)
            {
                System.Threading.Thread.Sleep(Math.Max(10, Math.Min(250, (int)(stallDump * 250f))));  // a quarter of the limit: short stalls too
                long started = System.Threading.Interlocked.Read(ref frameStartTicks);
                if (started == 0) continue;
                double seconds = (clock.ElapsedTicks - started) / (double)System.Diagnostics.Stopwatch.Frequency;
                if (seconds < stallDump || frameStartTime < stallAfter) continue;
                Debug.Log("CROW_STALL_DUMP a frame has taken " + seconds.ToString("F1") + " s: crashing for a dump");
                ProbeFiles.Flush(3000);  // the queued lines, before the crash loses them
                System.Runtime.InteropServices.Marshal.WriteInt32(new IntPtr(16), 0);  // unmapped: a data abort
                Environment.FailFast("CrowCompat stall dump");
            }
        }

        static string Value(string flag)
        {
            int index = Array.IndexOf(args, flag);
            return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
        }

        static float Number(string flag, float fallback)
        {
            string value = Value(flag);
            float number;
            return value != null && float.TryParse(value, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out number) ? number : fallback;
        }

        void Start()
        {
            if (Doppler.Owns(this)) return;  // a -crow-doppler test tone's filter (Audio.cs)
            started = lastBeat = lastLoad = Time.realtimeSinceStartup;
            collections = GC.CollectionCount(0);
            SceneManager.sceneLoaded += (scene, mode) =>
            {
                lastLoad = Time.realtimeSinceStartup;
                shotTaken = false;
                textDumped = false;
                found = false;
                teleported = false;
                Debug.Log("CROW_DIAG scene loaded " + scene.name + " (" + mode + ") at " + lastLoad.ToString("F2") + " s");
            };
            Debug.Log("CROW_DIAG started: " + string.Join(" ", args.Where(a => a.StartsWith("-crow-")).ToArray()));
        }

        // -crow-audio-tap: called on FMOD's mixer thread for each block of the probe's silent source.
        void OnAudioFilterRead(float[] data, int channels)
        {
            if (!Doppler.Filter(this, data, channels)) Audio.Tap(data, channels);
        }

        // -crow-doppler: the physics steps of each frame.
        void FixedUpdate()
        {
            Doppler.Step(this);
        }

        static float lastUpdateAt = -1f;

        void Update()
        {
            if (Doppler.Owns(this)) return;
            Audio.Frame();
            Alloc.Frame();
            Fight.Frame();
            frameStartTime = Time.realtimeSinceStartup;
            System.Threading.Interlocked.Exchange(ref frameStartTicks, clock.ElapsedTicks);
            if (mute) AudioListener.volume = 0f;  // the game sets it from its sound settings
            if (noVsync)
            {
                QualitySettings.vSyncCount = 0;
                Application.targetFrameRate = -1;
            }
            float now = Time.realtimeSinceStartup;
            // Only once the Logos scene's own load of the Title is over (2 s after it), so two scenes are
            // never in flight at once (that overstated memory: Roadside ran out of it that way).
            // The scene's name is a new string at each read: read only while it matters.
            bool titleUp = sceneToLoad != null && !sceneRequested && now - lastLoad >= 2f && SceneManager.GetActiveScene().name == "Title";
            if (sceneToLoad != null && !sceneRequested && (titleUp || now - started >= 180f))
            {
                MaraScene.Request();  // the player's scene, which the title would have loaded (Loading.cs)
                if (MaraScene.Settled)
                {
                    sceneRequested = true;
                    Debug.Log("CROW_DIAG loading " + sceneToLoad);
                    Fight.BeforeLoad();  // the globals the area's FSMs read as it starts
                    if (!SceneGap.Load(sceneToLoad, LoadSceneMode.Single)) SceneManager.LoadScene(sceneToLoad);  // as the game's loads (Loading.cs)
                }
                else if (!waitLogged)
                {
                    waitLogged = true;
                    Debug.Log("CROW_DIAG waiting for " + MaraScene.Scene + " before loading " + sceneToLoad);
                }
            }
            if (sceneThen != null && sceneRequested && now - lastLoad >= sceneThenAfter && SceneManager.GetActiveScene().name == sceneToLoad)
            {
                // "A>B>C": a chain of area loads, each s seconds after the one before.
                int split = sceneThen.IndexOf('>');
                string next = split < 0 ? sceneThen : sceneThen.Substring(0, split);
                sceneThen = split < 0 ? null : sceneThen.Substring(split + 1);
                Debug.Log("CROW_DIAG loading " + next + " from " + sceneToLoad);
                sceneToLoad = next;
                ScenePrefetch.LoadScene(next, LoadSceneMode.Single);
            }
            // By the wall clock: Time.unscaledDeltaTime is clamped to maximumDeltaTime (-crow-physics-cap).
            float delta = lastUpdateAt >= 0f ? (now - lastUpdateAt) * 1000f : Time.unscaledDeltaTime * 1000f;
            lastUpdateAt = now;
            frames++;
            frameSum += delta;
            if (delta > worst) worst = delta;
            if (heartbeat > 0f && now - lastBeat >= heartbeat) Beat(now);
            if (input)
            {
                LogInput();
                LogInputState();
                if (now - lastInputReport >= 5f)
                {
                    lastInputReport = now;
                    ReportInput();
                }
            }
            if (shotAfter >= 0f && !shotTaken && shotPath != null && now - lastLoad >= shotAfter)
            {
                shotTaken = true;
                ScreenCapture.CaptureScreenshot(shotPath);
                Debug.Log("CROW_DIAG screenshot " + shotPath);
            }
            if (dumpTextAfter >= 0f && !textDumped && now - lastLoad >= dumpTextAfter)
            {
                textDumped = true;
                DumpText();
                DumpCanvas();
            }
            if (findPattern != null && !found && now - lastLoad >= findAfter)
            {
                found = true;
                FindRenderers();
            }
            if (teleportAfter >= 0f && !teleported && now - lastLoad >= teleportAfter)
            {
                teleported = true;
                Teleport();
            }
            if (language != null && now - lastLanguageCheck >= 0.5f)
            {
                lastLanguageCheck = now;
                KeepLanguage();
            }
            if (textShader != null && now - lastShaderSwap >= 1f)
            {
                lastShaderSwap = now;
                SwapTextShader(textShader);
            }
            if (quitAfter >= 0f && now - started >= quitAfter)
            {
                Debug.Log("CROW_DIAG quitting");
                Application.Quit();
            }
        }

        // -crow-teleport: the player's body (the rigidbody PlayMaker moves) and its transform put at
        // teleportTo, at rest; the game's camera follows it.
        static void Teleport()
        {
            GameObject body = GameObject.Find("character RIGIDBODY");
            if (body == null)
            {
                Debug.Log("CROW_DIAG teleport: no active \"character RIGIDBODY\"");
                return;
            }
            var rigidbody = body.GetComponent<Rigidbody>();
            if (rigidbody != null)
            {
                rigidbody.velocity = Vector3.zero;
                rigidbody.position = teleportTo;
            }
            body.transform.position = teleportTo;
            Debug.Log("CROW_DIAG teleport to " + teleportTo.ToString("F2"));
        }

        // -crow-find: the renderers whose hierarchy path matches, whatever their state.
        static void FindRenderers()
        {
            int count = 0;
            foreach (Renderer renderer in Resources.FindObjectsOfTypeAll<Renderer>())
            {
                if (!renderer.gameObject.scene.IsValid()) continue;  // assets and prefabs
                var path = new StringBuilder(renderer.name);
                for (Transform t = renderer.transform.parent; t != null; t = t.parent) path.Insert(0, t.name + "/");
                if (!findPattern.IsMatch(path.ToString())) continue;
                count++;
                var line = new StringBuilder("CROW_FIND ").Append(path).Append(" [").Append(renderer.gameObject.scene.name).Append("]")
                    .Append(" active=").Append(renderer.gameObject.activeInHierarchy ? 1 : 0)
                    .Append(" enabled=").Append(renderer.enabled ? 1 : 0)
                    .Append(" visible=").Append(renderer.isVisible ? 1 : 0)
                    .Append(" layer=").Append(renderer.gameObject.layer)
                    .Append(" bounds=").Append(renderer.bounds.center.ToString("F2")).Append(renderer.bounds.size.ToString("F2"));
                MeshFilter filter = renderer.GetComponent<MeshFilter>();
                if (filter != null)
                {
                    Mesh mesh = filter.sharedMesh;
                    line.Append(" mesh=").Append(mesh == null ? "none" : mesh.name + "(" + mesh.vertexCount + "v," + mesh.subMeshCount + "sub)");
                }
                foreach (Material material in renderer.sharedMaterials)
                    line.Append(" mat=").Append(material == null ? "none" : material.name + "/" + (material.shader == null ? "none" : material.shader.name)
                        + "/q" + material.renderQueue + (material.HasProperty("_Color") ? "/" + material.color.ToString("F2") : ""));
                Debug.Log(line.ToString());
            }
            Debug.Log("CROW_FIND " + count + " renderers match " + findPattern);
        }

        // -crow-dump-text: each TextMeshPro text in the loaded scenes, with its font asset's tables and
        // what TMP generated for it (read by reflection: CrowCompat doesn't reference TMP).
        static void DumpText()
        {
            Type textType = Type.GetType("TMPro.TMP_Text, Unity.TextMeshPro");
            if (textType == null)
            {
                Debug.Log("CROW_TEXT no TMPro.TMP_Text type");
                return;
            }
            var all = Resources.FindObjectsOfTypeAll(textType).Cast<Component>().Where(c => c.gameObject.scene.IsValid()).ToArray();
            Debug.Log("CROW_TEXT " + all.Length + " texts");
            LogMissingCharacters(all);
            foreach (Component text in all.OrderByDescending(c => c.gameObject.activeInHierarchy).Take(40))
            {
                object font = Get(text, "font");
                object material = Get(text, "fontSharedMaterial") as Material;
                object info = Get(text, "textInfo");
                var mesh = Get(text, "mesh") as Mesh;
                var line = new StringBuilder("CROW_TEXT ");
                line.Append(Path(text.transform)).Append(" active=").Append(text.gameObject.activeInHierarchy);
                line.Append(" world=").Append(text.transform.position.ToString("F1")).Append(" scale=").Append(text.transform.lossyScale.ToString("F3"));
                line.Append(" rotation=").Append(text.transform.rotation.eulerAngles.ToString("F0"));
                var box = text.transform as RectTransform;
                if (box != null) line.Append(" rect=").Append(box.rect.ToString("F1"));
                line.Append(" enabled=").Append(((Behaviour)text).enabled);
                line.Append(" text='").Append((Get(text, "text") as string ?? "").Replace('\n', '|')).Append('\'');
                line.Append(" font=").Append(font == null ? "null" : ((UnityEngine.Object)font).name);
                if (font != null)
                {
                    line.Append(" glyphs=").Append(Count(Get(font, "glyphTable"))).Append(" chars=").Append(Count(Get(font, "characterTable")));
                    line.Append(" atlas=").Append(Get(font, "atlasTexture") is Texture ? ((Texture)Get(font, "atlasTexture")).width + "x" + ((Texture)Get(font, "atlasTexture")).height : "null");
                    object face = Get(font, "faceInfo");
                    line.Append(" pointSize=").Append(face == null ? "?" : Get(face, "pointSize"));
                    line.Append(" lineHeight=").Append(face == null ? "?" : Get(face, "lineHeight"));
                }
                line.Append(" shader=").Append(material == null ? "null" : ((Material)material).shader.name + (((Material)material).shader.isSupported ? "" : " (unsupported)"));
                line.Append(" characters=").Append(info == null ? "?" : Get(info, "characterCount"));
                line.Append(" vertices=").Append(mesh == null ? "?" : mesh.vertexCount.ToString());
                if (mesh != null && mesh.vertexCount > 0)
                {
                    line.Append(" bounds=").Append(mesh.bounds.ToString("F2"));
                    Color32[] colors = mesh.colors32;
                    if (colors.Length > 0) line.Append(" vertexColor=").Append(colors[0]);
                    var uv1 = new List<Vector2>();
                    mesh.GetUVs(1, uv1);
                    if (uv1.Count > 0) line.Append(" uv1=").Append(uv1[0].ToString("F3"));
                }
                var glyphs = font == null ? null : Get(font, "glyphTable") as System.Collections.IList;
                if (glyphs != null && glyphs.Count > 40)
                {
                    object glyph = glyphs[40];
                    line.Append(" glyph40=[index ").Append(Get(glyph, "index")).Append(" scale ").Append(Get(glyph, "scale"));
                    object metrics = Get(glyph, "metrics");
                    line.Append(" w ").Append(Get(metrics, "width")).Append(" h ").Append(Get(metrics, "height"));
                    line.Append(" adv ").Append(Get(metrics, "horizontalAdvance"));
                    object rect = Get(glyph, "glyphRect");
                    line.Append(" rect ").Append(Get(rect, "x")).Append(',').Append(Get(rect, "y")).Append(' ')
                        .Append(Get(rect, "width")).Append('x').Append(Get(rect, "height")).Append(']');
                }
                line.Append(" size=").Append(Get(text, "fontSize")).Append(" color=").Append(Get(text, "color"));
                var canvasRenderer = text.GetComponent<CanvasRenderer>();
                if (canvasRenderer != null)
                {
                    line.Append(" cr=[materials ").Append(canvasRenderer.materialCount);
                    Material drawn = canvasRenderer.materialCount > 0 ? canvasRenderer.GetMaterial(0) : null;
                    line.Append(" material ").Append(drawn == null ? "null" : drawn.name + "/" + drawn.shader.name);
                    line.Append(" cull ").Append(canvasRenderer.cull).Append(" alpha ").Append(canvasRenderer.GetAlpha())
                        .Append(" color ").Append(canvasRenderer.GetColor());
                    float groups = 1f;
                    foreach (CanvasGroup group in text.GetComponentsInParent<CanvasGroup>(true)) groups *= group.alpha;
                    line.Append(" groups ").Append(groups).Append(" absoluteDepth ").Append(canvasRenderer.absoluteDepth);
                    line.Append(" hasMoved ").Append(canvasRenderer.hasMoved).Append(']');
                    if (drawn != null)
                    {
                        line.Append(" props=[");
                        foreach (string name in new[] { "_GradientScale", "_ScaleRatioA", "_ScaleRatioB", "_ScaleRatioC", "_FaceDilate", "_WeightNormal",
                                     "_WeightBold", "_TextureWidth", "_TextureHeight", "_ScaleX", "_ScaleY", "_PerspectiveFilter", "_Sharpness",
                                     "_OutlineWidth", "_OutlineSoftness", "_VertexOffsetX", "_VertexOffsetY", "_MaskSoftnessX", "_StencilComp" })
                            if (drawn.HasProperty(name)) line.Append(name).Append('=').Append(drawn.GetFloat(name)).Append(' ');
                        foreach (string name in new[] { "_FaceColor", "_OutlineColor" })
                            if (drawn.HasProperty(name)) line.Append(name).Append('=').Append(drawn.GetColor(name)).Append(' ');
                        if (drawn.HasProperty("_ClipRect")) line.Append("_ClipRect=").Append(drawn.GetVector("_ClipRect")).Append(' ');
                        line.Append("keywords=").Append(string.Join(",", drawn.shaderKeywords)).Append(" tex=")
                            .Append(drawn.mainTexture == null ? "null" : drawn.mainTexture.name + " " + drawn.mainTexture.width + "x" + drawn.mainTexture.height)
                            .Append(" texformat=").Append(drawn.mainTexture is Texture2D ? ((Texture2D)drawn.mainTexture).format.ToString() : "?").Append(']');
                    }
                }
                Canvas canvas = text.GetComponentInParent<Canvas>();
                if (canvas != null)
                    line.Append(" canvas=[").Append(canvas.name).Append(' ').Append(canvas.renderMode).Append(" order ").Append(canvas.sortingOrder)
                        .Append(" camera ").Append(canvas.worldCamera == null ? "null" : canvas.worldCamera.name)
                        .Append(" channels ").Append(canvas.additionalShaderChannels).Append(']');
                Debug.Log(line.ToString());
            }
        }

        // With -crow-dump-text: each text whose characters its font asset, the font's fallbacks and TMP's
        // default font (and its fallbacks) lack: TMP draws those as the missing glyph, or nothing.
        static void LogMissingCharacters(Component[] texts)
        {
            Type settings = Type.GetType("TMPro.TMP_Settings, Unity.TextMeshPro");
            object defaultFont = null;
            try
            {
                PropertyInfo property = settings == null ? null : settings.GetProperty("defaultFontAsset", BindingFlags.Static | BindingFlags.Public);
                defaultFont = property == null ? null : property.GetValue(null, null);
            }
            catch (Exception) { }
            int withMissing = 0;
            foreach (Component text in texts)
            {
                object font = Get(text, "font");
                string content = Get(text, "text") as string;
                if (font == null || string.IsNullOrEmpty(content)) continue;
                var fonts = new List<object>();
                AddFontChain(font, fonts);
                if (defaultFont != null) AddFontChain(defaultFont, fonts);
                var missing = new List<char>();
                bool inTag = false;
                foreach (char c in content)
                {
                    if (c == '<') inTag = true;
                    if (inTag)
                    {
                        if (c == '>') inTag = false;
                        continue;
                    }
                    if (c < 0x20 || c == 0xA0 || char.IsSurrogate(c) || missing.Contains(c)) continue;
                    if (!fonts.Any(f => { var table = Get(f, "characterLookupTable") as System.Collections.IDictionary; return table != null && table.Contains((uint)c); }))
                        missing.Add(c);
                }
                if (missing.Count == 0) continue;
                withMissing++;
                Debug.Log("CROW_TEXT_MISSING " + Path(text.transform) + " active=" + text.gameObject.activeInHierarchy + " font=" + ((UnityEngine.Object)font).name +
                          " missing=" + string.Join(" ", missing.Select(c => c + "(U+" + ((int)c).ToString("X4") + ")").ToArray()));
            }
            Debug.Log("CROW_TEXT_MISSING " + withMissing + " of " + texts.Length + " texts lack characters");
        }

        static void AddFontChain(object font, List<object> fonts)
        {
            if (font == null || fonts.Contains(font)) return;
            fonts.Add(font);
            var fallbacks = Get(font, "fallbackFontAssetTable") as System.Collections.IEnumerable;
            if (fallbacks != null)
                foreach (object fallback in fallbacks)
                    if (fallback != null && !fallback.Equals(null)) AddFontChain(fallback, fonts);
        }

        // With -crow-dump-text: every active canvas element in draw order, to find what covers a text.
        static void DumpCanvas()
        {
            var renderers = Resources.FindObjectsOfTypeAll<CanvasRenderer>()
                .Where(r => r.gameObject.scene.IsValid() && r.gameObject.activeInHierarchy)
                .OrderBy(r => r.absoluteDepth).ToArray();
            Debug.Log("CROW_CANVAS " + renderers.Length + " active elements");
            foreach (CanvasRenderer renderer in renderers)
            {
                var line = new StringBuilder("CROW_CANVAS depth ").Append(renderer.absoluteDepth).Append(' ').Append(Path(renderer.transform));
                Behaviour graphic = renderer.GetComponents<Behaviour>().FirstOrDefault(b => b != null && b.GetType().GetProperty("mainTexture") != null);
                line.Append(" graphic=").Append(graphic == null ? "none" : graphic.GetType().Name + (graphic.enabled ? "" : " (disabled)"));
                if (graphic != null)
                {
                    line.Append(" color=").Append(Get(graphic, "color"));
                    var texture = Get(graphic, "mainTexture") as Texture;
                    line.Append(" texture=").Append(texture == null ? "null" : texture.name);
                    var drawn = renderer.materialCount > 0 ? renderer.GetMaterial(0) : null;
                    line.Append(" material=").Append(drawn == null ? "null" : drawn.name + "/" + drawn.shader.name);
                }
                var box = renderer.transform as RectTransform;
                if (box != null)
                {
                    var corners = new Vector3[4];
                    box.GetWorldCorners(corners);
                    line.Append(" screen=").Append(corners[0].ToString("F0")).Append('-').Append(corners[2].ToString("F0"));
                }
                line.Append(" cull ").Append(renderer.cull).Append(" alpha ").Append(renderer.GetAlpha());
                float groups = 1f;
                foreach (CanvasGroup group in renderer.GetComponentsInParent<CanvasGroup>(true)) groups *= group.alpha;
                line.Append(" groups ").Append(groups);
                Debug.Log(line.ToString());
            }
        }

        // -crow-text-shader <name>: every TextMeshPro text's material drawn with that shader instead,
        // to tell a shader that draws nothing from text that has no geometry.
        static void SwapTextShader(string name)
        {
            Shader shader = Shader.Find(name);
            Type textType = Type.GetType("TMPro.TMP_Text, Unity.TextMeshPro");
            if (shader == null || textType == null) return;
            foreach (UnityEngine.Object text in Resources.FindObjectsOfTypeAll(textType))
            {
                var material = Get(text, "fontSharedMaterial") as Material;
                if (material != null && material.shader != shader) material.shader = shader;
                var canvasRenderer = ((Component)text).GetComponent<CanvasRenderer>();
                for (int i = 0; canvasRenderer != null && i < canvasRenderer.materialCount; i++)
                {
                    Material drawn = canvasRenderer.GetMaterial(i);
                    if (drawn != null && drawn.shader != shader) drawn.shader = shader;
                }
            }
        }

        // -crow-language: the game sets I2's language from its settings save at startup, so this sets
        // LocalizationManager.CurrentLanguage (by reflection) whenever it differs, every half second.
        static void KeepLanguage()
        {
            if (currentLanguage == null)
            {
                Type manager = AppDomain.CurrentDomain.GetAssemblies()
                    .Select(a => a.GetType("I2.Loc.LocalizationManager", false)).FirstOrDefault(t => t != null);
                currentLanguage = manager == null ? null : manager.GetProperty("CurrentLanguage", BindingFlags.Static | BindingFlags.Public);
                if (currentLanguage == null)
                {
                    Debug.Log("CROW_DIAG no I2.Loc.LocalizationManager.CurrentLanguage");
                    language = null;
                    return;
                }
            }
            try
            {
                if (Equals(currentLanguage.GetValue(null, null), language)) return;
                Debug.Log("CROW_DIAG language " + currentLanguage.GetValue(null, null) + " -> " + language);
                currentLanguage.SetValue(null, language, null);
            }
            catch (Exception exception)
            {
                Debug.Log("CROW_DIAG language: " + exception);
                language = null;
            }
        }

        static object Get(object target, string name)
        {
            try
            {
                Type type = target.GetType();
                PropertyInfo property = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (property != null) return property.GetValue(target, null);
                FieldInfo field = type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                return field == null ? null : field.GetValue(target);
            }
            catch (Exception exception)
            {
                return "<" + exception.GetType().Name + ">";
            }
        }

        static string Count(object list)
        {
            var collection = list as System.Collections.ICollection;
            return collection == null ? "null" : collection.Count.ToString();
        }

        static string Path(Transform transform)
        {
            return transform.parent == null ? transform.name : Path(transform.parent) + "/" + transform.name;
        }

        // -crow-input: Unity's joystick buttons (any joystick, then joysticks 1-2) as they go down,
        // and the keys and mouse buttons Unity reports, to tell input that never reaches Unity from
        // input that Rewired doesn't map.
        static void LogInput()
        {
            if (!Input.anyKeyDown) return;
            var down = new List<string>();
            for (KeyCode key = KeyCode.Backspace; key <= KeyCode.Joystick2Button19; key++)
                if (Enum.IsDefined(typeof(KeyCode), key) && Input.GetKeyDown(key)) down.Add(key.ToString());
            Debug.Log("CROW_INPUT down " + string.Join(" ", down.ToArray()));
        }

        // -crow-input, every frame, logged when it changes: Unity's raw axes past 0.2 (Joy1-4, axes 1-29,
        // as Rewired's Input Manager entries name them: JoyNAxisM is Unity's axis M-1 of joystick N) and
        // the joystick buttons held; each Rewired joystick's active elements (its hardware map's view);
        // player 0's active actions (what the game's PlayMaker actions read). Moving a stick shows which
        // of the three loses it.
        static string[] axisNames;
        static string lastUnity = "", lastElements = "", lastActions = "";
        static object[] actionIds;
        static string[] actionNames;
        static MethodInfo getAxisRaw;
        static readonly Dictionary<string, PropertyInfo> properties = new Dictionary<string, PropertyInfo>();

        static void LogInputState()
        {
            if (axisNames == null)
            {
                var names = new List<string>();
                for (int joystick = 1; joystick <= 4; joystick++)
                    for (int axis = 1; axis <= 29; axis++)
                    {
                        string name = "Joy" + joystick + "Axis" + axis;
                        try
                        {
                            Input.GetAxisRaw(name);
                            names.Add(name);
                        }
                        catch (ArgumentException)
                        {
                        }
                    }
                axisNames = names.ToArray();
                Debug.Log("CROW_INPUT probing " + axisNames.Length + " Unity axes");
            }
            var unity = new StringBuilder();
            foreach (string name in axisNames)
            {
                float value = Input.GetAxisRaw(name);
                if (Mathf.Abs(value) > 0.2f) unity.Append(' ').Append(name).Append('=').Append(value.ToString("0.0"));
            }
            for (KeyCode key = KeyCode.Joystick1Button0; key <= KeyCode.Joystick4Button19; key++)
                if (Input.GetKey(key)) unity.Append(' ').Append(key);
            Changed(ref lastUnity, "unity", unity.ToString());
            // Rewired reads Unity's input: with none there and nothing active at the last look, there
            // is nothing new to see, and the reflection below allocates (about 0.3 MB/s at 17 fps).
            if (lastUnity.Length == 0 && lastElements.Length == 0 && lastActions.Length == 0) return;

            Type reInput = Type.GetType("Rewired.ReInput, Rewired_Core");
            if (reInput == null || !Equals(GetStatic(reInput, "isReady"), true)) return;
            var elements = new StringBuilder();
            object controllers = GetStatic(reInput, "controllers");
            var joysticks = controllers == null ? null : Get(controllers, "Joysticks") as System.Collections.IEnumerable;
            if (joysticks != null)
            {
                int index = 0;
                foreach (object joystick in joysticks)
                {
                    var axes = Cached(joystick, "Axes") as System.Collections.IEnumerable;
                    if (axes != null)
                        foreach (object axis in axes)
                        {
                            object value = Cached(axis, "value");
                            if (value is float && Mathf.Abs((float)value) > 0.2f)
                                elements.Append(" j").Append(index).Append(':').Append(ElementName(axis)).Append('=').Append(((float)value).ToString("0.0"));
                        }
                    var buttons = Cached(joystick, "Buttons") as System.Collections.IEnumerable;
                    if (buttons != null)
                        foreach (object button in buttons)
                            if (Equals(Cached(button, "value"), true))
                                elements.Append(" j").Append(index).Append(':').Append(ElementName(button));
                    index++;
                }
            }
            Changed(ref lastElements, "rewired elements", elements.ToString());
            string served = Pad.Active();  // the pad reader's actions while it serves (Rewired's are stale then)
            if (served != null)
            {
                Changed(ref lastActions, "player0 actions (pad reader)", served);
                return;
            }

            object players = GetStatic(reInput, "players");
            object player = null;
            try
            {
                MethodInfo getPlayer = players == null ? null : players.GetType().GetMethod("GetPlayer", new[] { typeof(int) });
                player = getPlayer == null ? null : getPlayer.Invoke(players, new object[] { 0 });
            }
            catch (Exception)
            {
            }
            if (player == null) return;
            if (actionIds == null)
            {
                object mapping = GetStatic(reInput, "mapping");
                var actions = mapping == null ? null : Get(mapping, "Actions") as System.Collections.IEnumerable;
                var ids = new List<object>();
                var names = new List<string>();
                if (actions != null)
                    foreach (object action in actions)
                    {
                        ids.Add(Get(action, "id"));
                        names.Add(Get(action, "name") as string);
                    }
                actionIds = ids.ToArray();
                actionNames = names.ToArray();
                getAxisRaw = player.GetType().GetMethod("GetAxisRaw", new[] { typeof(int) });
                Debug.Log("CROW_INPUT probing " + actionIds.Length + " Rewired actions: " + string.Join(", ", actionNames));
            }
            if (getAxisRaw == null) return;
            var active = new StringBuilder();
            for (int i = 0; i < actionIds.Length; i++)
            {
                object value;
                try
                {
                    value = getAxisRaw.Invoke(player, new[] { actionIds[i] });
                }
                catch (Exception)
                {
                    continue;
                }
                if (value is float && Mathf.Abs((float)value) > 0.2f)
                    active.Append(" [").Append(actionNames[i]).Append("]=").Append(((float)value).ToString("0.0"));
            }
            Changed(ref lastActions, "player0 actions", active.ToString());
        }

        static void Changed(ref string last, string what, string now)
        {
            if (now == last) return;
            last = now;
            Debug.Log("CROW_INPUT " + what + ":" + (now.Length == 0 ? " none" : now));
        }

        static string ElementName(object element)
        {
            object identifier = Cached(element, "elementIdentifier");
            object name = identifier == null ? null : Cached(identifier, "name");
            return name as string ?? "?";
        }

        // Get with the property looked up once per type (LogInputState reads hundreds a frame).
        static object Cached(object target, string name)
        {
            Type type = target.GetType();
            string key = type.FullName + ":" + name;
            PropertyInfo property;
            if (!properties.TryGetValue(key, out property))
            {
                const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                try
                {
                    property = type.GetProperty(name, flags);
                }
                catch (AmbiguousMatchException)
                {
                    property = type.GetProperties(flags).FirstOrDefault(p => p.Name == name && p.GetIndexParameters().Length == 0);
                }
                properties[key] = property;
            }
            try
            {
                return property == null ? null : property.GetValue(target, null);
            }
            catch (Exception)
            {
                return null;
            }
        }

        // Unity's joystick names and Rewired's state (by reflection: CrowCompat doesn't reference Rewired).
        static void ReportInput()
        {
            var line = new StringBuilder("CROW_INPUT unity joysticks=[").Append(string.Join("|", Input.GetJoystickNames())).Append(']');
            Type reInput = Type.GetType("Rewired.ReInput, Rewired_Core");
            if (reInput == null)
            {
                Debug.Log(line.Append(" no Rewired.ReInput").ToString());
                return;
            }
            object ready = GetStatic(reInput, "isReady");
            line.Append(" rewired ready=").Append(ready);
            if (Equals(ready, true))
            {
                object controllers = GetStatic(reInput, "controllers");
                line.Append(" joystickCount=").Append(controllers == null ? "?" : Get(controllers, "joystickCount"));
                var joysticks = controllers == null ? null : Get(controllers, "Joysticks") as System.Collections.IEnumerable;
                if (joysticks != null)
                    foreach (object joystick in joysticks)
                        line.Append(" [").Append(Get(joystick, "name")).Append(" / ").Append(Get(joystick, "hardwareName"))
                            .Append(" id ").Append(Get(joystick, "id")).Append(" unityId ").Append(Get(joystick, "unityId"))
                            .Append(" axes ").Append(Count(Get(joystick, "Axes")))
                            .Append(" buttons ").Append(Count(Get(joystick, "Buttons"))).Append(']');
                object keyboard = controllers == null ? null : Get(controllers, "Keyboard");
                line.Append(" keyboard=").Append(keyboard == null ? "null" : Get(keyboard, "enabled"));
                object players = GetStatic(reInput, "players");
                var all = players == null ? null : Get(players, "AllPlayers") as System.Collections.IEnumerable;
                if (all != null)
                    foreach (object player in all)
                    {
                        object playerControllers = Get(player, "controllers");
                        line.Append(" player ").Append(Get(player, "name")).Append(" joysticks=")
                            .Append(playerControllers == null ? "?" : Get(playerControllers, "joystickCount"))
                            .Append(" hasKeyboard=").Append(playerControllers == null ? "?" : Get(playerControllers, "hasKeyboard"));
                        if (Equals(Get(player, "id"), 0)) Changed(ref lastMaps, "player0 maps", Maps(playerControllers));
                    }
            }
            Debug.Log(line.ToString());
        }

        static string lastMaps = "";

        // A player's controller maps: controller type and id, category, layout, hardware GUID (the
        // Dual Analog Gamepad template is 83b427e4, the PS Vita controller d2aef070), enabled, entries.
        static string Maps(object playerControllers)
        {
            object maps = playerControllers == null ? null : Get(playerControllers, "maps");
            if (maps == null) return " ?";
            System.Collections.IEnumerable all;
            try
            {
                MethodInfo getAllMaps = maps.GetType().GetMethods().FirstOrDefault(m =>
                    m.Name == "GetAllMaps" && !m.IsGenericMethodDefinition && m.GetParameters().Length == 0);
                all = getAllMaps == null ? null : getAllMaps.Invoke(maps, null) as System.Collections.IEnumerable;
            }
            catch (Exception exception)
            {
                return " <" + exception.GetType().Name + ">";
            }
            if (all == null) return " ?";
            var line = new StringBuilder();
            foreach (object map in all)
            {
                string guid = Convert.ToString(Get(map, "hardwareGuid"));
                line.Append(" [").Append(Get(map, "controllerType")).Append(' ').Append(Get(map, "controllerId"))
                    .Append(" cat ").Append(Get(map, "categoryId")).Append(" layout ").Append(Get(map, "layoutId"))
                    .Append(' ').Append(guid.Length > 8 ? guid.Substring(0, 8) : guid)
                    .Append(Equals(Get(map, "enabled"), true) ? " on" : " off")
                    .Append(' ').Append(Get(map, "elementMapCount")).Append(']');
            }
            return line.ToString();
        }

        static object GetStatic(Type type, string name)
        {
            try
            {
                PropertyInfo property = type.GetProperty(name, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                return property == null ? null : property.GetValue(null, null);
            }
            catch (Exception e)
            {
                return "<" + e.GetType().Name + ">";
            }
        }

        void Beat(float now)
        {
            int gc = GC.CollectionCount(0);
            int visible = 0, renderers = 0, lit = 0;
            var lights = new Dictionary<LightType, int>();
            var active = new List<Light>();
            foreach (Light light in FindObjectsOfType<Light>())
            {
                if (!light.enabled || light.intensity <= 0f || !light.gameObject.activeInHierarchy) continue;
                active.Add(light);
                lit++;
                int count;
                lights.TryGetValue(light.type, out count);
                lights[light.type] = count + 1;
            }
            // Estimated draw calls, before the engine's batching: each visible renderer's materials
            // in the base pass, and an additive pass per point or spot light whose range reaches its
            // bounds, up to the quality level's pixel lights (the brightest directional light is in
            // the base pass).
            int drawBase = 0, drawAdd = 0, pixelLights = QualitySettings.pixelLightCount;
            foreach (Renderer renderer in FindObjectsOfType<Renderer>())
            {
                if (!renderer.enabled) continue;
                renderers++;
                if (!renderer.isVisible) continue;
                visible++;
                int materials = renderer.sharedMaterials.Length;
                drawBase += materials;
                if (renderer is ParticleSystemRenderer || renderer is SpriteRenderer) continue;
                Bounds bounds = renderer.bounds;
                int reaching = 0;
                foreach (Light light in active)
                {
                    if (light.type == LightType.Directional) continue;
                    if (bounds.SqrDistance(light.transform.position) <= light.range * light.range) reaching++;
                }
                drawAdd += Math.Min(reaching, pixelLights) * materials;
            }
            var shadowed = new Dictionary<LightType, int>();
            foreach (Light light in active)
            {
                if (light.shadows == LightShadows.None) continue;
                int count;
                shadowed.TryGetValue(light.type, out count);
                shadowed[light.type] = count + 1;
            }
            var line = new StringBuilder("HEARTBEAT");
            line.Append(" t=").Append((now - started).ToString("F1"));
            line.Append(" scene=").Append(SceneManager.GetActiveScene().name.Replace(' ', '_'));
            line.Append(" fps=").Append((frames / (now - lastBeat)).ToString("F1"));
            line.Append(" ms=").Append((frameSum / Math.Max(frames, 1)).ToString("F2"));
            line.Append(" worst=").Append(worst.ToString("F1"));
            line.Append(" gc=").Append(gc - collections);
            line.Append(" heapMB=").Append((GC.GetTotalMemory(false) / 1048576.0).ToString("F1"));
            line.Append(" renderers=").Append(renderers).Append(" visible=").Append(visible);
            line.Append(" lights=").Append(lit);
            foreach (KeyValuePair<LightType, int> pair in lights) line.Append(' ').Append(pair.Key).Append('=').Append(pair.Value);
            line.Append(" drawBase=").Append(drawBase).Append(" drawAdd=").Append(drawAdd);
            foreach (KeyValuePair<LightType, int> pair in shadowed) line.Append(" shadow").Append(pair.Key).Append('=').Append(pair.Value);
            Debug.Log(line.ToString());
            lastBeat = now;
            frames = 0;
            frameSum = 0;
            worst = 0f;
            collections = gc;
        }
    }
}
